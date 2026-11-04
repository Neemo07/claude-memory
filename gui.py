#!/usr/bin/env python3
"""
gui.py - local browser interface for claude_memory.py.

  python gui.py            # serves on http://127.0.0.1:8756 and opens your browser
  python gui.py --no-open  # start the server only
  python gui.py --port 9000

Design notes
  * stdlib only (http.server + webbrowser), no pip install
  * binds 127.0.0.1 exclusively and requires a per-run token on every /api call,
    so a random web page you visit cannot drive this server
  * all parsing/summarising lives in claude_memory.py; this file is transport +
    UI only, so the tested behaviour is identical
  * conversation text is inserted with textContent, never innerHTML
"""
from __future__ import annotations

import argparse
import hmac
import json
import os
import secrets
import socket
import sys
import threading
import webbrowser
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

sys.path.insert(0, str(Path(__file__).resolve().parent))
import claude_memory as cm  # noqa: E402
from markdown_preview import parse_markdown  # noqa: E402

TOKEN = secrets.token_urlsafe(24)
MAX_UPLOAD = 32 * 1024 * 1024
ALLOWED_SUFFIX = (".md", ".markdown", ".json", ".txt")
PREVIEW_SUFFIX = (".md", ".markdown")
PREVIEW_TYPES = ("Markdown (*.md;*.markdown)", "Text files (*.txt)", "All files (*.*)")
MAX_PREVIEW_BYTES = 4 * 1024 * 1024

# ----------------------------------------------------------------------------- transports
# The page is identical for both front ends; only these six functions differ.
#   call(route, payload) -> parsed JSON object, throws Error on failure
#   quit()               -> close the window (or the browser-mode server)
#   download(text, name) -> "blob" if the page handled it, else the saved path
#   copy(text)           -> true if the host clipboard took it
#   pick(kind)           -> {path} / {cancelled} from a native Open dialog
#   quitLabel            -> what to tell the user after quitting

HTTP_TRANSPORT = r"""
{
  quitLabel: "server stopped - you can close this tab",
  quitButton: "Stop server",
  nativeOpen: false,
  async ready() { return true; },
  async call(path, body) {
    const res = await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Token": TOKEN },
      body: JSON.stringify(body || {}),
    });
    let data;
    try { data = await res.json(); }
    catch { throw new Error("server returned " + res.status); }
    if (!res.ok || data.error) throw new Error(data.error || ("HTTP " + res.status));
    return data;
  },
  async quit() {
    await this.call("/api/stop", {});
  },
  // a plain browser tab has no native Open dialog, and an <input type=file>
  // only ever hands back a bare file name, never a path this server could read
  async pick(kind) {
    throw new Error("this window cannot open a file dialog - type or paste the "
                    + "full path to the payload file instead");
  },
  async copy() { return false; },            // the browser clipboard is fine
  async download(text, name) {
    const blob = new Blob([text], { type: "text/markdown;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = name.replace(/[\\/:*?"<>|]/g, "_");
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 2000);
    return "blob";
  },
}
"""

BRIDGE_TRANSPORT = r"""
{
  quitLabel: "closing - reopen with start-app.cmd",
  quitButton: "Close window",
  nativeOpen: true,
  // pywebview injects window.pywebview.api *after* the document starts parsing,
  // so the first scan() would run before the bridge exists and fail silently
  async ready() {
    for (let i = 0; i < 150; i++) {
      if (window.pywebview && window.pywebview.api) return true;
      await new Promise((r) => setTimeout(r, 100));
    }
    throw new Error("the window bridge never appeared - close and reopen the app");
  },
  async call(path, body) {
    await this.ready();
    const data = await window.pywebview.api.call(path, JSON.stringify(body || {}));
    if (data && data.error) throw new Error(data.error);
    return data || {};
  },
  async quit() { await window.pywebview.api.quit(); },
  async pick(kind) {
    // the native Open dialog: a real picker for the saved share payload
    return await this.call("/api/pick_file", { kind: kind || "share_file" });
  },
  async copy(text) { return !!await window.pywebview.api.copy(text); },
  async download(text, name) {
    // a webview has no download manager, so ask where to put it instead
    const d = await this.call("/api/save_as", { text: text, name: name,
                                                dir: this.lastDir || "" });
    return d.cancelled ? "cancelled" : d.path;
  },
}
"""


def page_html(transport_js: str = "", token: str = "") -> str:
    """Assemble the page with one of the transports injected."""
    html = PAGE.replace("__TRANSPORT__", transport_js or HTTP_TRANSPORT)
    return html.replace("__TOKEN__", token)


PAGE = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>claude-memory</title>
<style>
  :root {
    --bg: #0f1115; --panel: #171a21; --panel2: #1e222b; --line: #2a2f3a;
    --fg: #e6e9ef; --dim: #99a1b3; --accent: #d97757; --accent2: #7c9cf5;
    --ok: #4ade80; --err: #f87171; --warn: #fbbf24;
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--fg);
         font: 14px/1.5 ui-sans-serif, system-ui, "Segoe UI", Roboto, sans-serif; }
  /* flex column, not calc(100vh - Npx): the header height varies with font
     scaling and wrapping, and a wrong guess leaves the page scrollable behind
     the sidebar's own scrollbar */
  body { display: flex; flex-direction: column; height: 100vh; overflow: hidden; }
  header { flex: none; padding: 14px 20px; border-bottom: 1px solid var(--line);
           display: flex; align-items: baseline; gap: 14px; flex-wrap: nowrap; }
  header h1 { font-size: 16px; margin: 0; letter-spacing: .2px; flex: none; }
  header .sub { color: var(--dim); font-size: 12px; overflow: hidden;
                text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
  header .sp { flex: 1 1 auto; min-width: 8px; }
  header button { flex: none; }
  main { display: grid; grid-template-columns: 380px 1fr; gap: 0;
         flex: 1 1 auto; min-height: 0; }
  # narrow windows stack the panels and let the page scroll as one document
  @media (max-width: 900px) {
    body { height: auto; overflow: auto; }
    main { grid-template-columns: 1fr; }
    #side { border-right: none; border-bottom: 1px solid var(--line); }
    #right { min-height: 60vh; }
  }
  #side { border-right: 1px solid var(--line); padding: 16px; overflow-y: auto;
          overflow-x: hidden; min-height: 0; background: var(--panel);
          scrollbar-width: thin; scrollbar-color: #39404f var(--panel); }
  #out { scrollbar-width: thin; scrollbar-color: #39404f var(--bg); }
  section { margin-bottom: 18px; }
  h2 { font-size: 11px; text-transform: uppercase; letter-spacing: .12em;
       color: var(--dim); margin: 0 0 8px; font-weight: 600; }
  label { display: block; font-size: 12px; color: var(--dim); margin-bottom: 4px; }
  input[type=text], input[type=number], select, textarea {
    width: 100%; background: var(--panel2); color: var(--fg);
    border: 1px solid var(--line); border-radius: 6px; padding: 7px 9px;
    font: inherit; }
  input:focus, select:focus, textarea:focus { outline: 2px solid var(--accent2); }
  .row { display: flex; gap: 8px; }
  .row > * { flex: 1; }
  .check { display: flex; align-items: center; gap: 7px; color: var(--fg);
           font-size: 13px; margin-bottom: 6px; }
  .check input { width: auto; flex: none; accent-color: var(--accent); }
  button { background: var(--panel2); color: var(--fg); border: 1px solid var(--line);
           border-radius: 6px; padding: 8px 12px; font: inherit; cursor: pointer; }
  button:hover { border-color: var(--accent2); }
  button:disabled { opacity: .5; cursor: default; }
  button.primary { background: var(--accent); border-color: var(--accent);
                   color: #1b1207; font-weight: 600; }
  button.primary:hover { filter: brightness(1.08); }
  #status { font-size: 12px; white-space: pre-wrap; word-break: break-word;
            min-height: 16px; max-width: 46%; }
  .ok { color: var(--ok); } .err { color: var(--err); } .warn { color: var(--warn); }
  /* min-height:0 is load-bearing: without it the tall <pre> forces the grid row
     past the window, the sidebar stretches to match (clientHeight 681 -> 4227)
     and stops being scrollable, so the options appear to vanish */
  #right { display: flex; flex-direction: column; min-width: 0; min-height: 0;
          overflow: hidden; }
  #toolbar { padding: 10px 16px; border-bottom: 1px solid var(--line);
             display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  #meta { color: var(--dim); font-size: 12px; margin-left: auto; }
  #out { flex: 1; margin: 0; padding: 16px; overflow: auto; white-space: pre-wrap;
         word-break: break-word; font: 13px/1.55 ui-monospace, Consolas, monospace;
         color: var(--fg); background: var(--bg); }
  /* GitHub-style Markdown body: readable measure, bordered headings, zebra tables */
  #preview { flex: 1; margin: 0; padding: 24px 32px; overflow: auto;
             color: var(--fg); background: var(--bg);
             font: 15px/1.6 ui-sans-serif, system-ui, "Segoe UI", Roboto, sans-serif; }
  #preview[hidden], #out[hidden] { display: none !important; }
  #preview > * { max-width: 1012px; margin-left: auto; margin-right: auto; }
  #preview h1, #preview h2, #preview h3, #preview h4,
  #preview h5, #preview h6 { line-height: 1.25; margin: 1.4em 0 .6em; font-weight: 600; }
  #preview h1, #preview h2 { border-bottom: 1px solid var(--line); padding-bottom: .3em; }
  #preview h1 { font-size: 2em; } #preview h2 { font-size: 1.5em; }
  #preview h3 { font-size: 1.25em; } #preview h4 { font-size: 1em; }
  #preview p { margin: .8em 0; }
  #preview a { color: #4493f8; text-decoration: none; }
  #preview a:hover { text-decoration: underline; }
  #preview del { color: var(--dim); }
  #preview ul, #preview ol { padding-left: 2em; margin: .8em 0; }
  #preview li { margin: .3em 0; }
  #preview li.task { list-style: none; margin-left: -1.45em; }
  #preview li.task input { margin: 0 .5em 0 0; vertical-align: middle;
                           accent-color: var(--ok); }
  #preview blockquote { margin: 1em 0; padding: .2em 1em;
                        border-left: 4px solid #3b434f; color: var(--dim); }
  #preview pre { margin: 1em 0; padding: 16px; overflow: auto;
                 border: 1px solid var(--line); border-radius: 6px;
                 background: #161b22; font: 13px/1.5 ui-monospace, Consolas, monospace; }
  #preview code { background: var(--panel2); padding: .2em .4em; border-radius: 4px;
                  font: 85% ui-monospace, Consolas, monospace; }
  #preview pre code { padding: 0; background: transparent; font-size: 100%; }
  #preview table { margin: 1em 0; border-collapse: collapse; width: auto;
                   max-width: 100%; }
  #preview th, #preview td { padding: 8px 13px; border: 1px solid var(--line);
                             color: var(--fg); }
  #preview th { background: var(--panel2); font-weight: 600; }
  #preview tbody tr:nth-child(2n) { background: #13161c; }
  #preview hr { margin: 1.6em 0; border: 0; border-top: 1px solid var(--line); }
  /* Arabic and other RTL scripts: the output pane must flip direction when
     the generated memory contains Arabic text. dir=auto handles mixed
     Arabic/English content correctly per-paragraph. */
  #out[dir="rtl"], #out[dir="rtl"] * { direction: rtl; unicode-bidi: embed; }
  #out[dir="ltr"], #out[dir="ltr"] * { direction: ltr; unicode-bidi: embed; }
  #preview[dir="rtl"], #preview[dir="rtl"] * { direction: rtl; unicode-bidi: embed; }
  #preview[dir="ltr"], #preview[dir="ltr"] * { direction: ltr; unicode-bidi: embed; }
  input[type=text], textarea { direction: auto; unicode-bidi: plaintext; }
  /* no flex on the empty state: its <b> elements would each become a flex item */
  #out.empty { color: var(--dim); text-align: center; padding-top: 18vh; }
  table { width: 100%; border-collapse: collapse; font-size: 12px; }
  th, td { text-align: left; padding: 4px 6px; border-bottom: 1px solid var(--line);
           color: var(--dim); font-weight: 500; }
  td.n { color: var(--fg); font-variant-numeric: tabular-nums; }
  code { background: var(--panel2); padding: 1px 5px; border-radius: 4px;
         font-size: 12px; }
  .hint { color: var(--dim); font-size: 12px; margin-top: 6px; }
  /* the primary action stays pinned: clicking it must never scroll the options
     above it out of view (and the scroll container is the sidebar) */
  #actions { position: sticky; bottom: -16px; margin-top: 18px;
             padding: 12px 0 16px; background: var(--panel);
             border-top: 1px solid var(--line); }
  #actions button { width: 100%; }
</style>
</head>
<body>
<header>
  <h1>claude-memory</h1>
  <span class="sub">Claude Code sessions + claude.ai share links &rarr; one markdown memory</span>
  <span class="sp"></span>
  <span class="sub" id="rootinfo"></span>
  <button id="btnStop" title="Shut the local server down">Stop server</button>
</header>
<main>
  <div id="side">
    <section>
      <h2>Source</h2>
      <label class="check"><input type="radio" name="src" value="local" checked>
        Local Claude Code sessions</label>
      <label class="check"><input type="radio" name="src" value="both">
        Local + a shared link</label>
      <label class="check"><input type="radio" name="src" value="shareonly">
        Only a shared link</label>
      <div id="shareBox" style="display:none">
        <label for="shareUrl">Shared chat link &mdash; paste it here</label>
        <input type="text" id="shareUrl" placeholder="https://claude.ai/share/xxxx-...">
        <label for="shareFile" style="margin-top:8px">...or a saved payload file</label>
        <div class="row">
          <input type="text" id="shareFile" placeholder="C:\\path\\share-payload.json">
          <button id="btnShareFile" style="flex:0 0 auto">Browse...</button>
        </div>
        <div class="hint">No link? Open the share page, DevTools &rarr; Network &rarr;
          filter <code>chat_snapshots</code> &rarr; Copy response &rarr; save it as
          .json, then press <b>Browse...</b> to pick it (or type the path).</div>
      </div>
    </section>

    <section>
      <h2>Which sessions</h2>
      <label class="check"><input type="checkbox" id="hereOnly">
        This folder only</label>
      <div class="row">
        <div><label for="since">Window</label>
          <input type="text" id="since" value="30d" placeholder="30d / 12h / 2026-10-01"></div>
        <div><label for="maxSess">Max sessions</label>
          <input type="number" id="maxSess" value="0" min="0"></div>
      </div>
      <label for="cwd" style="margin-top:8px">Path contains (optional)</label>
      <input type="text" id="cwd" placeholder="leave empty for all projects">
      <div style="margin-top:10px">
        <label for="sessions">Found on this machine</label>
        <div style="max-height:150px;overflow:auto;border:1px solid var(--line);
                    border-radius:6px">
          <table><thead><tr><th>when</th><th>folder</th><th class="n">turns</th></tr></thead>
          <tbody id="sessBody"><tr><td colspan="3">scanning...</td></tr></tbody></table>
        </div>
      </div>
    </section>

    <section>
      <h2>Output</h2>
      <div class="row">
        <div><label for="mode">Kind</label>
          <select id="mode">
            <option value="memory">memory (summary)</option>
            <option value="full">memory (full context)</option>
            <option value="transcript">transcript (raw chat)</option>
            <option value="json">json (structured)</option>
          </select></div>
        <div><label for="fmt">Wrapping</label>
          <select id="fmt">
            <option value="plain">plain markdown</option>
            <option value="claude-md">CLAUDE.md markers</option>
          </select></div>
      </div>
      <div class="row" style="margin-top:8px">
        <div><label for="lang">Language</label>
          <select id="lang">
            <option value="auto">auto-detect</option>
            <option value="en">English</option>
            <option value="ar">Arabic</option>
            <option value="both">Bilingual (EN + AR)</option>
          </select></div>
      </div>
      <div class="row" style="margin-top:8px">
        <div><label for="maxChars">Max chars (summary parts)</label>
          <input type="number" id="maxChars" value="14000" min="200" step="1000"></div>
        <div><label for="maxItems">Max items/section</label>
          <input type="number" id="maxItems" value="14" min="1"></div>
      </div>
      <div class="hint" id="modeHint"></div>
      <label class="check" style="margin-top:10px">
        <input type="checkbox" id="thinking"> Include thinking blocks</label>
      <label class="check">
        <input type="checkbox" id="dropSide"> Drop subagent turns</label>
      <label class="check">
        <input type="checkbox" id="ascii"> ASCII only (no box glyphs)</label>
    </section>

    <section>
      <h2>Save to disk</h2>
      <label for="outDir">Folder</label>
      <input type="text" id="outDir" placeholder="(defaults to Documents)">
      <label for="outName" style="margin-top:8px">File name</label>
      <input type="text" id="outName" value="MEMORY.md">
    </section>

    <section>
      <h2>Generate</h2>
      <div class="row" style="margin-top:10px">
        <button id="btnSave" style="flex:2">Save</button>
        <button id="btnOpen" style="flex:1">Folder</button>
      </div>
      <label class="check" style="margin-top:10px">
        <input type="checkbox" id="keepHist" checked> Keep 5 previous versions</label>
    </section>

    <div id="actions">
      <button class="primary" id="btnGen">Generate memory</button>
    </div>
  </div>

  <div id="right">
    <div id="toolbar">
      <button id="btnPreview">Preview MD</button>
      <button id="btnOpenMd">Open MD...</button>
      <button id="btnCopy">Copy</button>
      <button id="btnDl">Download</button>
      <button id="btnClaude">Copy for CLAUDE.md</button>
      <span id="status" role="status" aria-live="polite"></span>
      <span id="meta"></span>
    </div>
    <pre id="out" class="empty">Press <b>Generate memory</b> to build a memory file from
your Claude Code sessions and any shared claude.ai link.</pre>
    <div id="preview" hidden></div>
  </div>
</main>
<script>
const TOKEN = "__TOKEN__";
const TRANSPORT = __TRANSPORT__;
const $ = (id) => document.getElementById(id);
let currentText = "";
let currentMode = "memory";
let currentStats = "";
let previewVisible = false;
let previewIsFile = false;

function say(msg, cls) {
  const el = $("status");
  el.textContent = msg;
  el.className = cls || "";
  el.title = msg;                      // full text on hover if it has to wrap
}

function nothingYet(what) {
  say("nothing to " + what + " yet - press Generate memory first", "warn");
}

async function api(path, body) {
  return TRANSPORT.call(path, body || {});
}

function srcMode() {
  return document.querySelector('input[name=src]:checked').value;
}

function syncSrc() {
  const side = $("side");
  const keep = side.scrollTop;      // inserting the share box must not shift the view
  const mode = srcMode();
  const onlyShare = mode === "shareonly";
  // "Local + a shared link" needs this box just as much as "Only a shared link":
  // do_build() refuses to run for it unless it has a link or a payload path, so
  // hiding the box left that radio with no way to fill in what it demands
  const showShare = mode !== "local";
  $("shareBox").style.display = showShare ? "block" : "none";
  $("hereOnly").disabled = onlyShare;
  $("since").disabled = onlyShare;
  side.scrollTop = keep;
}

function syncModeHint() {
  const hints = {
    memory: "Summary only: decisions, rules, files, open items. Compact and cheap to read.",
    full: "Whole context: the summary above plus every turn verbatim. Can be tens of "
          + "thousands of tokens - check the size before importing it into CLAUDE.md.",
    transcript: "Raw readable chat, no memory framing.",
    json: "Structured data for other tools.",
  };
  $("modeHint").textContent = hints[$("mode").value] || "";
}

function num(id, fallback) {
  const v = parseInt($(id).value, 10);
  return Number.isFinite(v) ? v : fallback;
}

async function scan() {
  try {
    const d = await api("/api/scan");
    $("rootinfo").textContent = d.root;
    $("outDir").placeholder = d.default_out_dir;
    const body = $("sessBody");
    body.textContent = "";
    if (!d.sessions.length) {
      const tr = document.createElement("tr");
      const td = document.createElement("td");
      td.colSpan = 3;
      td.textContent = "none found yet - run a Claude Code session first";
      tr.appendChild(td); body.appendChild(tr);
      return;
    }
    d.sessions.slice().reverse().forEach((s) => {
      const tr = document.createElement("tr");
      [s.start || "?", s.cwd || "(unknown)", s.turns].forEach((v, i) => {
        const td = document.createElement("td");
        if (i === 2) td.className = "n";
        td.textContent = String(v);
        td.title = String(v);
        tr.appendChild(td);
      });
      body.appendChild(tr);
    });
  } catch (e) { say("scan failed: " + e.message, "err"); }
}

// Detect whether a text contains Arabic script (Unicode blocks:
// U+0600-06FF Arabic, U+0750-077F Arabic Supplement,
// U+08A0-08FF Arabic Extended-A, U+0870-089F Arabic Extended-B).
// Returns "rtl" when Arabic characters are present, otherwise "ltr".
function detectDirection(text) {
  return /[\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff\u0870-\u089f]/.test(text)
    ? "rtl" : "ltr";
}

function appendInline(parent, spans) {
  (spans || []).forEach((span) => {
    let node = document.createTextNode(span.text || "");
    if (span.code) {
      const code = document.createElement("code");
      code.appendChild(node);
      node = code;
    }
    if (span.strike) {
      const del = document.createElement("del");
      del.appendChild(node);
      node = del;
    }
    if (span.italic) {
      const em = document.createElement("em");
      em.appendChild(node);
      node = em;
    }
    if (span.bold) {
      const strong = document.createElement("strong");
      strong.appendChild(node);
      node = strong;
    }
    if (span.href) {
      const link = document.createElement("a");
      link.href = span.href;
      if (!link.href.startsWith("#")) {
        link.target = "_blank";
        link.rel = "noopener noreferrer";
      }
      link.appendChild(node);
      node = link;
    }
    parent.appendChild(node);
  });
}

function appendMarkdownHeading(block, target) {
  const level = Math.min(6, Math.max(1, block.level || 1));
  const heading = document.createElement("h" + level);
  appendInline(heading, block.spans);
  target.appendChild(heading);
}

function appendMarkdownParagraph(block, target) {
  const paragraph = document.createElement("p");
  appendInline(paragraph, block.spans);
  target.appendChild(paragraph);
}

function appendMarkdownList(block, target) {
  const list = document.createElement(block.ordered ? "ol" : "ul");
  if (block.ordered && Number.isInteger(block.start)) list.start = block.start;
  (block.items || []).forEach((item) => {
    const entry = document.createElement("li");
    if (item.checked === true || item.checked === false) {
      entry.className = "task";
      const box = document.createElement("input");
      box.type = "checkbox";
      box.disabled = true;
      box.checked = item.checked;
      entry.appendChild(box);
    }
    appendInline(entry, item.spans);
    list.appendChild(entry);
  });
  target.appendChild(list);
}

function appendMarkdownQuote(block, target) {
  const quote = document.createElement("blockquote");
  renderMarkdownBlocks(block.blocks, quote);
  target.appendChild(quote);
}

function appendMarkdownCode(block, target) {
  const pre = document.createElement("pre");
  const code = document.createElement("code");
  code.textContent = block.text || "";
  pre.appendChild(code);
  target.appendChild(pre);
}

function tableAlign(block, index) {
  const align = (block.align || [])[index];
  return align === "center" || align === "right" ? align : "";
}

function appendMarkdownTableHeader(block, table) {
  const head = document.createElement("thead");
  const headerRow = document.createElement("tr");
  (block.headers || []).forEach((cell, index) => {
    const th = document.createElement("th");
    const align = tableAlign(block, index);
    if (align) th.style.textAlign = align;
    appendInline(th, cell);
    headerRow.appendChild(th);
  });
  head.appendChild(headerRow);
  table.appendChild(head);
}

function appendMarkdownTableRows(block, table) {
  const body = document.createElement("tbody");
  (block.rows || []).forEach((row) => {
    const tr = document.createElement("tr");
    row.forEach((cell, index) => {
      const td = document.createElement("td");
      const align = tableAlign(block, index);
      if (align) td.style.textAlign = align;
      appendInline(td, cell);
      tr.appendChild(td);
    });
    body.appendChild(tr);
  });
  table.appendChild(body);
}

function appendMarkdownTable(block, target) {
  const table = document.createElement("table");
  appendMarkdownTableHeader(block, table);
  appendMarkdownTableRows(block, table);
  target.appendChild(table);
}

function renderMarkdownBlocks(blocks, target) {
  (blocks || []).forEach((block) => {
    const renderer = MARKDOWN_RENDERERS[block.type];
    if (renderer) renderer(block, target);
  });
}

const MARKDOWN_RENDERERS = {
  heading: appendMarkdownHeading,
  paragraph: appendMarkdownParagraph,
  list: appendMarkdownList,
  quote: appendMarkdownQuote,
  code: appendMarkdownCode,
  table: appendMarkdownTable,
  rule: (block, target) => target.appendChild(document.createElement("hr")),
};

function setPreviewVisible(visible, fileMode) {
  previewVisible = visible;
  $("out").hidden = visible;
  $("preview").hidden = !visible;
  $("btnPreview").textContent = visible
    ? (fileMode ? "Close preview" : "Show source")
    : "Preview MD";
}

function showMarkdownPreview(blocks, text, name) {
  const preview = $("preview");
  preview.textContent = "";
  preview.setAttribute("dir", detectDirection(text || ""));
  renderMarkdownBlocks(blocks, preview);
  previewIsFile = !!name;
  setPreviewVisible(true, previewIsFile);
  $("meta").textContent = name || currentStats;
}

function chooseLocalMarkdown() {
  // Browser front end: no native path dialog, so read the file locally instead.
  return new Promise((resolve) => {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = ".md,.markdown,text/markdown,text/plain";
    input.onchange = () => resolve(input.files && input.files[0] ? input.files[0] : null);
    window.addEventListener("focus", () => setTimeout(() => {
      if (!input.files || !input.files.length) resolve(null);
    }, 800), { once: true });
    input.click();
  });
}

async function openMarkdownFile() {
  try {
    if (TRANSPORT.nativeOpen) {
      const d = await api("/api/open_markdown", { dir: $("outDir").value.trim() });
      if (d.cancelled) { say("no file chosen - nothing changed", "warn"); return; }
      showMarkdownPreview(d.blocks, d.text || "", d.name || d.path || "Markdown file");
      say("reading " + (d.name || d.path), "ok");
      return;
    }
    const file = await chooseLocalMarkdown();
    if (!file) { say("no file chosen - nothing changed", "warn"); return; }
    if (file.size > 4 * 1024 * 1024) throw new Error(file.name + " is too large (4 MB max).");
    const text = await file.text();
    if (!text.trim()) throw new Error(file.name + " is empty.");
    const d = await api("/api/preview", { text: text, mode: "memory" });
    showMarkdownPreview(d.blocks, text, file.name);
    say("reading " + file.name, "ok");
  } catch (e) {
    say("cannot open Markdown file: " + e.message, "err");
  }
}

async function togglePreview() {
  if (previewVisible) {
    const wasFile = previewIsFile;
    setPreviewVisible(false, false);
    previewIsFile = false;
    $("meta").textContent = currentStats;
    say(wasFile ? "preview closed" : "source view", "ok");
    return;
  }
  if (!currentText) {
    say("nothing generated yet - choose a Markdown file", "warn");
    return openMarkdownFile();
  }
  if (currentMode === "json") {
    say("JSON output is not Markdown - choose memory, full, or transcript", "warn");
    return;
  }
  try {
    const data = await api("/api/preview", { text: currentText, mode: currentMode });
    const preview = $("preview");
    preview.textContent = "";
    preview.setAttribute("dir", detectDirection(currentText));
    renderMarkdownBlocks(data.blocks, preview);
    previewIsFile = false;
    setPreviewVisible(true, false);
    $("meta").textContent = currentStats;
    say("Markdown preview", "ok");
  } catch (e) {
    say("preview failed: " + e.message, "err");
  }
}

async function generate() {
  const btn = $("btnGen");
  const side = $("side");
  const keepScroll = side.scrollTop;     // clicking must not scroll the options away
  btn.disabled = true;
  say("working...");
  try {
    const payload = {
      src: srcMode(),
      share: $("shareUrl").value.trim(),
      share_file: $("shareFile").value.trim(),
      here: $("hereOnly").checked,
      since: $("since").value.trim(),
      sessions: num("maxSess", 0),
      cwd: $("cwd").value.trim(),
      mode: $("mode").value,
      fmt: $("fmt").value,
      lang: $("lang") ? $("lang").value : "auto",
      max_chars: num("maxChars", 14000),
      max_items: num("maxItems", 14),
      include_thinking: $("thinking").checked,
      sidechains: $("dropSide").checked ? "drop" : "keep",
      ascii_only: $("ascii").checked,
      history: $("keepHist").checked ? 5 : 0,
    };
    const d = await api("/api/build", payload);
    currentText = d.text;
    currentMode = payload.mode;
    currentStats = d.stats;
    previewIsFile = false;
    setPreviewVisible(false);
    const out = $("out");
    out.textContent = d.text;          // never innerHTML: content is untrusted
    out.className = "";
    // Set text direction based on content: Arabic text needs RTL
    out.setAttribute("dir", detectDirection(d.text));
    out.scrollTop = 0;
    $("meta").textContent = d.stats;
    say("done", "ok");        // the stats live in #meta, right beside this
  } catch (e) {
    say(e.message, "err");
  } finally {
    btn.disabled = false;
    // Chromium applies scroll anchoring (and the click's own focus scroll) at
    // paint time, i.e. after this synchronous restore, so repeat it
    const restore = () => { side.scrollTop = keepScroll; };
    restore();
    requestAnimationFrame(restore);
    setTimeout(restore, 60);
  }
}

async function save() {
  if (!currentText) { nothingYet("save"); return; }
  try {
    const d = await api("/api/save", {
      text: currentText, dir: $("outDir").value.trim(), name: $("outName").value.trim(),
      history: $("keepHist").checked ? 5 : 0,
    });
    say("saved " + d.path, "ok");
    $("outName").value = d.path.split(/[\\/]/).pop();
  } catch (e) { say(e.message, "err"); }
}

async function openFolder() {
  try {
    const d = await api("/api/open", { dir: $("outDir").value.trim(),
                                        name: $("outName").value.trim() });
    say("opened " + d.path, "ok");
  } catch (e) { say(e.message, "err"); }
}

async function pickShareFile() {
  try {
    const d = await TRANSPORT.pick("share_file");
    if (d.cancelled) { say("no file chosen - nothing changed", "warn"); return; }
    $("shareFile").value = d.path;
    say("selected " + d.path, "ok");
  } catch (e) { say("cannot pick a file: " + e.message, "err"); }
}

async function copy(text, label) {
  try {
    await navigator.clipboard.writeText(text);
    say(label + " copied to clipboard", "ok");
    return;
  } catch (e) { /* webviews often block clipboard permission */ }
  if (await TRANSPORT.copy(text)) {
    say(label + " copied to clipboard", "ok");
    return;
  }
  const ta = document.createElement("textarea");
  ta.value = text; document.body.appendChild(ta); ta.select();
  const ok = document.execCommand("copy");
  document.body.removeChild(ta);
  say(ok ? label + " copied" : "copy blocked - select the text manually",
      ok ? "ok" : "warn");
}

function download() {
  const name = ($("outName").value.trim() || "MEMORY.md");
  TRANSPORT.download(currentText, name).then((where) => {
    if (where === "blob") say("downloaded " + name, "ok");
    else if (where === "cancelled") say("save cancelled - nothing written", "warn");
    else say("saved " + where, "ok");
  }).catch((e) => { say("save failed: " + e.message, "err"); });
}

$("btnGen").onclick = generate;
// never let the click focus the button: focusing scrolls it into view, which
// dragged the sidebar down and made the options above appear to vanish
$("btnGen").addEventListener("mousedown", (e) => e.preventDefault());
$("btnGen").addEventListener("touchstart", (e) => e.preventDefault(), {passive: false});
$("btnPreview").onclick = togglePreview;
$("btnOpenMd").onclick = openMarkdownFile;
$("btnSave").onclick = save;
$("btnOpen").onclick = openFolder;
$("btnShareFile").onclick = pickShareFile;
$("btnCopy").onclick = () => {
  if (!currentText) { nothingYet("copy"); return; }
  copy(currentText, "memory");
};
$("btnDl").onclick = () => {
  if (!currentText) { nothingYet("download"); return; }
  download();
};
$("btnClaude").onclick = () => {
  if (!currentText) { nothingYet("copy"); return; }
  const body = currentText.trim();
  const wrapped = body.startsWith("<!-- claude-memory:begin -->")
    ? body
    : `<!-- claude-memory:begin -->\n${body}\n<!-- claude-memory:end -->`;
  copy(wrapped, "CLAUDE.md block");
};
$("btnStop").onclick = async () => {
  try {
    await TRANSPORT.quit();
    say(TRANSPORT.quitLabel, "ok");
  } catch (e) { say("could not close: " + e.message, "err"); }
  $("btnStop").disabled = true;
};
document.querySelectorAll('input[name=src]').forEach((r) => r.onchange = syncSrc);
document.addEventListener("keydown", (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key === "Enter") { e.preventDefault(); generate(); }
});
syncSrc();
$("mode").addEventListener("change", syncModeHint);
syncModeHint();
// the header button means different things per front end
$("btnStop").textContent = TRANSPORT.quitButton || "Close";
// wait for the host bridge before the first scan, or it fails silently
TRANSPORT.ready().then(scan).catch((e) => say("cannot reach the app: " + e.message, "err"));
</script>
</body>
</html>
"""


def default_out_dir() -> Path:
    for env in ("USERPROFILE", "HOME"):
        base = os.environ.get(env)
        if base:
            docs = Path(base) / "Documents"
            return docs if docs.is_dir() else Path(base)
    return Path.cwd()


def as_int(value, fallback: int) -> int:
    """Number fields accept junk from humans; fall back instead of crashing."""
    try:
        parsed = int(str(value).strip())
    except (TypeError, ValueError):
        return fallback
    return parsed


def build_options(data: dict) -> cm.Options:
    """Turn the UI payload into the same Options the CLI builds."""
    opts = cm.Options(
        root=cm.DEFAULT_ROOT,
        mode=data.get("mode") if data.get("mode") in ("memory", "full", "transcript",
                                                      "json") else "memory",
        fmt=data.get("fmt") if data.get("fmt") in ("plain", "claude-md") else "plain",
        lang=data.get("lang") if data.get("lang") in ("auto", "en", "ar", "both") else "auto",
        sessions=as_int(data.get("sessions"), 0),
        cwd=data.get("cwd") or "",
        here=bool(data.get("here")),
        max_chars=as_int(data.get("max_chars"), 14000),
        max_items=as_int(data.get("max_items"), 14),
        include_thinking=bool(data.get("include_thinking")),
        sidechains="drop" if data.get("sidechains") == "drop" else "keep",
        ascii_only=bool(data.get("ascii_only")),
        history=as_int(data.get("history"), 0),
        source_note=data.get("source_note") or "",
    )
    since = (data.get("since") or "").strip()
    if since:
        try:
            opts.since = cm.parse_since(since)
        except Exception:
            raise ValueError(f"cannot understand the time window {since!r}. "
                             "Try 30d, 12h, 30m or 2026-10-01.")
    return opts


def do_scan() -> dict:
    base = cm.DEFAULT_ROOT / "projects"
    opts = cm.Options(root=cm.DEFAULT_ROOT)
    sessions = []
    if base.is_dir():
        for path in cm.discover(cm.DEFAULT_ROOT, None):
            sess = cm.parse_session(path, opts)
            if not (sess.turns or sess.summaries):
                continue
            sessions.append({
                "file": path.name,
                "cwd": cm.session_where(sess),
                "branch": sess.branch,
                "start": f"{sess.start:%Y-%m-%d %H:%M}" if sess.start else "",
                "turns": len(sess.turns),
                "tools": len(sess.tool_calls()),
                "size": path.stat().st_size,
            })
    return {"sessions": sessions, "root": str(cm.DEFAULT_ROOT),
            "projects_dir": str(base), "default_out_dir": str(default_out_dir())}


def do_build(data: dict) -> dict:
    opts = build_options(data)
    cm.QUIET = True                       # the UI has its own status line

    share_sessions = []
    src = data.get("src", "local")
    if src in ("share", "both", "shareonly"):
        share_file = (data.get("share_file") or "").strip()
        share_url = (data.get("share") or "").strip()
        if not share_url and not share_file:
            raise ValueError("Paste a claude.ai share link (or a saved payload path) first.")
        if share_file:
            path = Path(os.path.expandvars(share_file))
            if not path.exists():
                raise ValueError(f"No such file: {path}")
            share_sessions.append(cm.load_share_file(path, opts.include_thinking))
        if share_url:
            payload = cm.fetch_share(share_url)
            sess = cm.parse_share_payload(payload, include_thinking=opts.include_thinking)
            share_sessions.append(sess)
            # cache next to the output so a re-run needs no network
            cache = Path(default_out_dir()) / f"claude-share-{sess.sid[:8]}.json"
            try:
                cm.atomic_write(cache, json.dumps(payload, indent=1, ensure_ascii=False) + "\n")
            except OSError:
                pass
            if not opts.source_note:
                opts.source_note = f"claude.ai share link ({cm.share_uuid(share_url)})"

    local = []
    if src in ("local", "both"):
        base = opts.root / "projects"
        if base.is_dir():
            parsed = [cm.parse_session(p, opts) for p in cm.discover(opts.root, None)]
            local = cm.select([s for s in parsed if s.turns or s.summaries], opts)

    chosen = local + share_sessions
    if not chosen:
        raise ValueError(
            "Nothing to summarise. No local sessions matched, and no share link was "
            "imported. Try a wider time window, untick 'this folder only', or paste a "
            "share link.")
    chosen.sort(key=lambda s: (s.start or datetime.min))

    if opts.mode == "transcript":
        text = cm.build_transcript(chosen, opts)
    elif opts.mode == "full":
        text = cm.build_full_memory(chosen, opts)
    elif opts.mode == "json":
        text = cm.build_json(chosen)
    else:
        text = cm.build_memory(chosen, opts)
    if opts.ascii_only:
        text = cm.to_ascii(text)

    prompts = sum(len(s.prompts()) for s in chosen)
    calls = sum(len(s.tool_calls()) for s in chosen)
    stats = (f"{len(chosen)} session(s) · {prompts} prompt(s) · {calls} tool call(s) "
             f"· {len(text)} chars ~{cm.estimate_tokens(text)} tokens")
    return {"text": text, "stats": stats}


def do_preview(data: dict) -> dict:
    """Parse current Markdown output into safe, structured preview blocks."""
    text = data.get("text")
    if not isinstance(text, str) or not text.strip():
        raise ValueError("There is nothing to preview yet.")
    if data.get("mode") == "json":
        raise ValueError("JSON output is not Markdown. Choose memory, full, or transcript.")
    return {"blocks": parse_markdown(text)}


def read_markdown_file(path) -> tuple:
    """Read one local Markdown file for preview, with a size and type guard."""
    target = Path(os.path.expandvars(str(path))).expanduser()
    if not target.is_file():
        raise ValueError(f"No such file: {target}")
    if target.suffix.lower() not in PREVIEW_SUFFIX:
        raise ValueError("Choose a Markdown file (.md or .markdown).")
    try:
        size = target.stat().st_size
    except OSError as e:
        raise ValueError(f"Cannot read {target}: {e}")
    if size > MAX_PREVIEW_BYTES:
        raise ValueError(f"{target.name} is too large to preview "
                         f"({size} bytes; the limit is {MAX_PREVIEW_BYTES}).")
    try:
        text = target.read_text(encoding="utf-8-sig", errors="replace")
    except OSError as e:
        raise ValueError(f"Cannot read {target}: {e}")
    if "\x00" in text:
        raise ValueError(f"{target.name} is not a text file.")
    if not text.strip():
        raise ValueError(f"{target.name} is empty.")
    return target, text


def do_open_markdown(data: dict, open_file=None) -> dict:
    """Pick a local Markdown file and return its parsed preview blocks.

    The page may pass a path directly (tkinter does); otherwise `open_file` must
    show the native Open dialog and return the chosen path, or None if cancelled.
    """
    path = data.get("path")
    path = path.strip() if isinstance(path, str) else ""
    if not path:
        if open_file is None:
            raise ValueError(
                "This window cannot show a native file dialog. "
                "Use the Open MD button in the desktop app.")
        start = data.get("dir")
        start = start.strip() if isinstance(start, str) and start.strip() else ""
        chosen = open_file("README.md", start or str(default_out_dir()), PREVIEW_TYPES)
        if not chosen:
            return {"ok": True, "cancelled": True}
        path = str(chosen)
    target, text = read_markdown_file(path)
    return {"ok": True, "path": str(target), "name": target.name,
            "text": text, "blocks": parse_markdown(text)}


def safe_out_path(data: dict) -> Path:
    """Resolve the save target; refuse anything that is not a plain data file."""
    name = (data.get("name") or "MEMORY.md").strip() or "MEMORY.md"
    folder = (data.get("dir") or "").strip()
    base = Path(folder) if folder else default_out_dir()
    base = Path(os.path.expandvars(str(base)))
    if not base.is_absolute():
        raise ValueError("The folder must be an absolute path, e.g. C:\\Users\\you\\Documents")
    if name != Path(name).name or "/" in name or "\\" in name:
        raise ValueError("The file name must not contain a path.")
    if not name.lower().endswith(ALLOWED_SUFFIX):
        raise ValueError("The file name must end with " + ", ".join(ALLOWED_SUFFIX))
    return base / name


def do_save(data: dict) -> dict:
    text = data.get("text")
    if not isinstance(text, str) or not text.strip():
        raise ValueError("There is nothing to save yet.")
    target = safe_out_path(data)
    keep = int(data.get("history") or 0)
    if keep > 0 and target.exists():
        try:
            cm.archive_history(target, target.read_text(encoding="utf-8", errors="replace"), keep)
        except OSError:
            pass
    cm.atomic_write(target, text)
    return {"ok": True, "path": str(target)}


def do_open(data: dict) -> dict:
    target = safe_out_path(data)
    # always reveal the containing folder: opening the file itself would fail
    # whenever it already exists, and mkdir would then target a file
    folder = target.parent
    folder.mkdir(parents=True, exist_ok=True)
    if hasattr(os, "startfile"):
        os.startfile(str(folder))            # type: ignore[attr-defined]
    else:
        webbrowser.open(folder.as_uri())
    return {"ok": True, "path": str(folder)}


ROUTES = ("/api/scan", "/api/build", "/api/preview", "/api/open_markdown",
          "/api/save", "/api/save_as", "/api/pick_file", "/api/open", "/api/stop")


def dispatch(route: str, data: dict, choose=None, open_file=None) -> dict:
    """Run one UI request. Shared by the local server and the desktop window.

    `choose` is an optional (name, start_dir) -> path|None hook used by the native
    Save As dialog. `open_file` is the native Open hook: (name, start_dir) for the
    share-payload Browse button, (name, start_dir, file_types) for Open MD. The
    browser front end has neither and gets an error that names the alternative.
    """
    if route == "/api/scan":
        return do_scan()
    if route == "/api/build":
        return do_build(data)
    if route == "/api/preview":
        return do_preview(data)
    if route == "/api/open_markdown":
        return do_open_markdown(data, open_file)
    if route == "/api/save":
        return do_save(data)
    if route == "/api/save_as":
        return do_save_as(data, choose)
    if route == "/api/pick_file":
        return do_pick_file(data, open_file)
    if route == "/api/open":
        return do_open(data)
    if route == "/api/stop":
        return {"ok": True}
    raise ValueError(f"unknown route {route}")


# the share payload is normally .json, but the picker must not reject a file the
# user happens to have saved under another data extension
PAYLOAD_TYPES = ("Saved share payload (*.json;*.md;*.markdown;*.txt)",
                 "All files (*.*)")


def do_pick_file(data: dict, open_file=None) -> dict:
    """Pick an existing file in a native Open dialog, for the share-payload field.

    `open_file(start_name, start_dir)` must return the chosen path, or None if
    cancelled. Nothing is read or validated here: the path is handed back for the
    user to review, and do_build() does the reading (and reports any bad file).
    """
    if open_file is None:
        raise ValueError(
            "This window cannot show a native file dialog. "
            "Type or paste the full path to the payload file instead.")
    start = (data.get("dir") or "").strip() or str(default_out_dir())
    chosen = open_file((data.get("kind") or "").strip() or "share-payload.json", start)
    if not chosen:
        return {"ok": True, "cancelled": True}
    return {"ok": True, "path": str(chosen)}


def do_save_as(data: dict, choose=None) -> dict:
    """Write to a location the user picks in a native Save As dialog.

    `choose(name, start_dir)` must return the chosen path, or None if cancelled.
    """
    text = data.get("text")
    if not isinstance(text, str) or not text.strip():
        raise ValueError("There is nothing to save yet.")
    if choose is None:
        raise ValueError(
            "This window cannot show a native Save As dialog. "
            "Use Save (writes to the folder above) or Download instead.")
    name = Path((data.get("name") or "MEMORY.md")).name or "MEMORY.md"
    start = (data.get("dir") or "").strip() or str(default_out_dir())

    chosen = choose(name, start)
    if not chosen:
        return {"ok": True, "cancelled": True}
    target = Path(os.path.expandvars(str(chosen)))
    if not target.name.lower().endswith(ALLOWED_SUFFIX):
        target = target.with_name(target.name + ".md")
    keep = int(data.get("history") or 0)
    if keep > 0 and target.exists():
        try:
            cm.archive_history(target, target.read_text(encoding="utf-8",
                                                        errors="replace"), keep)
        except OSError:
            pass
    cm.atomic_write(target, text)
    return {"ok": True, "path": str(target)}


class Handler(BaseHTTPRequestHandler):
    server_version = "claude-memory-gui"
    protocol_version = "HTTP/1.1"

    # -- plumbing
    def log_message(self, fmt: str, *args) -> None:   # keep the console clean
        pass

    def _send(self, code: int, body: bytes, ctype: str) -> None:
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(body)

    def _json(self, code: int, payload: dict) -> None:
        self._send(code, json.dumps(payload).encode("utf-8"),
                   "application/json; charset=utf-8")

    @staticmethod
    def _decode_body(raw: bytes) -> dict:
        """JSON bodies are UTF-8 per spec (browsers always comply). If some other
        client sends legacy single-byte text, decoding it the same way is
        lossless, so fall back instead of failing the request."""
        try:
            text = raw.decode("utf-8-sig")
        except UnicodeDecodeError:
            text = raw.decode("cp1252", errors="replace")
        data = json.loads(text or "{}")
        if not isinstance(data, dict):
            raise ValueError("expected a JSON object")
        return data

    def _authorised(self) -> bool:
        got = self.headers.get("X-Token", "")
        if hmac.compare_digest(got, TOKEN):
            return True
        self._json(403, {"error": "bad or missing token - reload the page"})
        return False

    # -- routes
    def do_GET(self) -> None:
        route = urlparse(self.path).path
        if route in ("/", "/index.html"):
            self._send(200, page_html(HTTP_TRANSPORT, TOKEN).encode("utf-8"),
                       "text/html; charset=utf-8")
        else:
            self._json(404, {"error": "not found"})

    def do_POST(self) -> None:
        if urlparse(self.path).path not in ROUTES:
            self._json(404, {"error": "not found"})
            return
        if not self._authorised():
            return
        try:
            length = int(self.headers.get("Content-Length") or 0)
            if length > MAX_UPLOAD:
                self._json(413, {"error": "request too large"})
                return
            raw = self.rfile.read(length) if length else b"{}"
            data = self._decode_body(raw)
            self._json(200, dispatch(urlparse(self.path).path, data))
            if urlparse(self.path).path == "/api/stop":
                threading.Thread(target=self.server.shutdown, daemon=True).start()
        except cm.ShareError as e:
            self._json(400, {"error": str(e)})
        except Exception as e:
            self._json(400, {"error": f"{type(e).__name__}: {e}"})


def free_port(preferred: int, tries: int = 20) -> int:
    for offset in range(tries):
        port = preferred + offset
        with socket.socket() as s:
            try:
                s.bind(("127.0.0.1", port))
                return port
            except OSError:
                continue
    raise SystemExit(f"no free port in {preferred}..{preferred + tries - 1}")


def serve(port: int, open_browser: bool) -> None:
    port = free_port(port)
    httpd = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    url = f"http://127.0.0.1:{port}/"
    print(f"claude-memory GUI on {url}")
    print("local only - press Ctrl+C to stop")
    if open_browser:
        threading.Timer(0.5, lambda: webbrowser.open(url)).start()
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nbye")
    finally:
        httpd.server_close()


def main() -> int:
    ap = argparse.ArgumentParser(description="local GUI for claude_memory.py")
    ap.add_argument("--port", type=int, default=8756)
    ap.add_argument("--no-open", action="store_true", help="do not open a browser")
    args = ap.parse_args()
    cm.configure_stdio()
    serve(args.port, not args.no_open)
    return 0


if __name__ == "__main__":
    sys.exit(main())
