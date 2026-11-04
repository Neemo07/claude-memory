# PRD — claude-memory 2.0 (C# / WPF)

Status: **Draft for approval**  
Target: Windows 10 1809+ / Windows 11, x64  
Rewrites: `claude-memory` 1.1.0 (Python, stdlib only)  
Classification: **MVP** — real daily use, real transcript data, user-visible system integration

## 1. Problem

Claude Code sessions are stored as raw `.jsonl` transcripts. Starting a new session with the context of the last one currently requires either a hand-written summary or the existing Python tool. The Python app works, but it depends on a Python install, ships three different shells (pywebview, tkinter, browser), and cannot act as a normal Windows Markdown previewer.

The rewrite must keep the proven behaviour, replace the shell with one modern WPF application, and add a real Windows file association so `.md` / `.markdown` files can open in this app for preview.

## 2. Users

Primary user: a developer on Windows who runs Claude Code locally and wants a rolling project memory plus a fast local Markdown previewer.

Secondary: the same user on another machine, using the published build without Python installed.

## 3. Goals

1. One native WPF desktop app that reproduces the functional behaviour of 1.1.0.
2. Byte-level parity for generated Markdown/JSON output, except for the generated timestamp, version string, and absolute fixture paths.
3. A safe, GitHub-style Markdown preview for generated output and arbitrary local `.md` / `.markdown` files.
4. Per-user Windows registration for `.md` / `.markdown`, plus a supported way for the user to confirm the default.
5. Command-line preview: `ClaudeMemory.exe "C:\path\file.md"` and drag/drop open the file directly.
6. No Python requirement in the shipped app; Python removed from the repository after parity sign-off.

## 4. Non-goals (v1)

- No cloud sync, accounts, telemetry, or LLM summarisation.
- No background watcher or filesystem service.
- No editing Markdown in the preview (read-only).
- No web version, mobile version, or local HTTP server.
- No silent takeover of the `.md` default. Windows does not support it, and the app must not fight the OS.
- No installer with machine-wide (HKLM) registration.
- No plugin system, themes marketplace, or custom CSS editor.
- No macOS/Linux build.

## 5. Functional requirements

### Generation

- **FR-1 Source selection:** Local sessions, Local + shared claude.ai chat, or shared chat only. The two share-source modes require a share URL or a saved payload file; one is enough.
- **FR-2 Session filters:** "this folder only", time window (`30m`, `12h`, `7d`, `1w`, `YYYY-MM-DD`, ISO timestamp), newest N sessions, and a "path contains" filter. Invalid windows produce the reference error, not a crash.
- **FR-3 Output modes:** `memory` (summary), `full` (summary plus every turn), `transcript` (raw readable chat), `json` (structured data).
- **FR-4 Render options:** plain vs `claude-md` markers, language `auto|en|ar|both`, max chars (floor 200), max items per section, include thinking, drop subagent turns, ASCII-only glyph downgrade.
- **FR-5 Generate result:** output text plus the status line `N session(s) · N prompt(s) · N tool call(s) · N chars ~N tokens`. Empty result is a clear message, never a silent no-op.
- **FR-6 Save:** write to an absolute folder with a single file name and an allowed suffix. Keep up to 5 previous versions in `<stem>.history/` when enabled. The write is atomic.
- **FR-7 Copy:** copy generated text to the Windows clipboard, and a second action that wraps the text in `<!-- claude-memory:begin -->` / `<!-- end -->` for `CLAUDE.md`.
- **FR-8 Download / Save As:** native Save dialog for users who do not want the fixed folder. Cancelling is not an error.
- **FR-9 Folder action:** reveal the output folder in Explorer.

### Preview

- **FR-10 Preview generated output:** render the current output as GitHub-style Markdown, with a toggle back to raw source. JSON output is not Markdown: show source and explain why preview is unavailable.
- **FR-11 Open any Markdown file:** toolbar action + `Ctrl+O` + drag/drop. Validate suffix, size, emptiness, NUL bytes. Show name and path in the status line.
- **FR-12 Command-line open:** when Windows launches the app with a file path, open it directly in preview.
- **FR-13 Parser coverage:** headings, paragraphs, emphasis, strikethrough, inline code, fenced code, ordered/unordered/task lists, block quotes, tables with alignment, horizontal rules, links. Images are not rendered from remote sources in v1.

### Share import

- **FR-14 Share URL:** accept `https://claude.ai/share/<uuid>` or a bare UUID. Reject other hosts.
- **FR-15 Fetch:** GET the public `chat_snapshots` endpoint with the browser UA; retry 403/429/500/502/503/504 with 2 s / 5 s / 12 s backoff; report HTTP 404 as "not found or no longer public".
- **FR-16 Offline payload:** load a saved `.json` file, tolerate a raw message array, and reject saved HTML shells with the reference explanation.
- **FR-17 Branch choice:** import only the deepest active branch, oldest first; abandoned sibling branches are skipped and counted.
- **FR-18 Cache:** save a fetched payload to Documents as `claude-share-<id8>.json`; ignore cache write failures.
- **FR-19 Tool mapping:** map claude.ai tool names to Claude Code names (`bash_tool → Bash`, `view → Read`, `str_replace_editor → Edit`, `present_files → PresentFiles`) and skip `hidden_in_chat` blocks.

### Windows integration

- **FR-20 Register association:** explicit action ("Set as default for Markdown") registers a per-user ProgID for `.md` and `.markdown`, the application entry, and Default Apps capability entries. No admin rights.
- **FR-21 Confirm default:** after registration, open the supported Windows confirmation surface (`SHOpenWithDialog` for a `.md` file or the Default Apps settings page) and explain that Windows requires the user to confirm.
- **FR-22 Status:** show the current registration state (registered / not registered) and offer "Remove association" that deletes only the app's own keys and values.
- **FR-23 Single instance:** a second launch forwards the file path to the running window and exits; no duplicate window, no lost open request.
- **FR-24 First run:** no association change without an explicit action. The app remains fully usable if registration is refused by policy.

### CLI parity

- **FR-25 Headless CLI:** `ClaudeMemory.Cli` supports the reference flags: `--root`, `--projects-dir`, `--out`, `--print`, `--mode`, `--format`, `--sessions`, `--since`, `--fast`, `--cwd`, `--here`, `--branch`, `--max-chars`, `--max-items`, `--max-paths`, `--max-cmds`, `--max-line`, `--max-prompts`, `--max-file-bytes`, `--sidechains`, `--include-thinking`, `--no-addresses`, `--history`, `--source-note`, `--share`, `--share-file`, `--share-save`, `--only-share`, `--ascii`, `--lang`, `--list`, `--quiet`, `--version`.
- **FR-26 Exit codes:** 0 success, 1 no sessions matched, 2 no transcript directory, 3 share import failure — matching the reference.

## 6. UX specification

Window title: `claude-memory`. Minimum size 900×600, default 1280×860.

- **Left column (320–380 px, independently scrollable):** Source, Which sessions, Output, Save to disk. Generate pinned to the bottom.
- **Right column:** toolbar (`Preview MD` / `Show source`, `Open MD…`, `Copy`, `Download`, `Copy for CLAUDE.md`, settings/menu), status line, then the output surface.
- **Output surface:** monospace source view or WebView2 preview. Empty state explains what to do.
- **Opened files as documents (post-plan addition, 2026-11-05):** while an opened Markdown file is the current document, the toolbar toggle flips between its rendered preview and its raw source in the monospace pane, and `Copy`, `Save As…`, and `Copy for CLAUDE.md` act on the file's text (Save As leaves the generated-output file name untouched). The reference (gui.py:641-648, 696-726) only closed a file preview and kept Copy/Save on the generated output, which left an opened file unreadable as source and uncopyable — a deliberate difference.
- **Preview:** Markdig renders to a self-contained document shown in WebView2 over `about:blank`; links open in the default browser after scheme validation. WebView2 needs a browser profile, so the app creates `%LOCALAPPDATA%\claude-memory\webview2` on first preview — it holds no transcript content. A missing runtime leaves the source view working and names the install page.
- **Status:** every action reports success, cancellation, or a one-line error next to the toolbar.
- **Busy state:** indeterminate progress, action buttons disabled, Cancel available for network work.
- **Theme:** dark, matching `#0f1115` background and `#d97757` accent, Segoe UI Variable; readable in Windows light mode (no hard-coded unreadable pairs).
- **Accessibility:** keyboard reachable controls, visible focus, `AutomationProperties.Name` on icon-only buttons, status announced with `AutomationProperties.LiveSetting`.
- **Error surfaces:** recoverable errors in the status line; fatal startup errors in a dialog plus `claude-memory-error.log` beside the executable.

## 7. Data model

`Session`: path, id, cwd, branch, Claude Code version, model, start, end, turns, summaries, skipped count, truncated-file flag.

`Turn`: role (`user|assistant|system|note`), text, tools (`name`, one-line brief), errors, timestamp, sidechain flag.

`MemoryOptions`: all reference defaults (mode memory, fmt plain, maxChars 14000, maxItems 14, maxPaths 18, maxCmds 10, maxLine 260, maxPromptsPerSession 12, maxFileBytes 12 MiB, sidechains keep, history 5, lang auto, includeAddresses true).

`BuildRequest` / `BuildResult`: source selection, filters, options; text, stats, session count, warnings.

## 8. Security requirements

- No raw HTML from Markdown; no scripts in the preview; navigation locked to the generated document.
- Safe-link allowlist; no `file:`, `javascript:`, `data:` navigation.
- No local server; no listening socket.
- No secret or transcript content in logs, crash reports, or telemetry.
- Registry writes confined to `HKCU\Software\Classes`; reversible; no `UserChoice`; no HKLM.
- Output writes confined to the chosen path, its history folder, and the share cache.
- Untrusted share payloads are size-limited and parsed defensively.
- Preview files are size-limited (4 MiB) and type-checked before reading.

## 9. Edge cases and unhappy paths

1. `~/.claude/projects` missing or empty → explain, keep the app usable, share import still works.
2. Zero-byte transcript, malformed JSONL line, truncated final line, or non-UTF-8 bytes → skip bad records, keep good ones.
3. Transcript larger than `maxFileBytes` → read the tail only and mark the session truncated.
4. Unknown role/content block types, `isMeta`, or dropped sidechains → count as skipped, never crash.
5. No sessions match the filters → message naming the window/folder filter; exit code 1 in CLI.
6. Unparseable time window → message suggesting `30d`, `12h`, `30m`, `2026-10-01`.
7. `maxChars` below 200 → clamp to 200 and report it (reference behaviour).
8. Huge prompt or reply → middle-clipped with an explicit `chars omitted` marker; start and end preserved.
9. Arabic or mixed-script content → bilingual headers according to `lang`, token estimate weighted per script, ASCII mode keeps letters.
10. Share URL on another host → rejected; UUID malformed → rejected.
11. Share endpoint returns 404, 403, 429, 5xx, non-JSON, or times out → specific message, retries where specified.
12. Saved file is an HTML share page → explain that it contains no conversation and give the DevTools route.
13. Saved payload is a raw array, or has no `chat_messages` → array is wrapped; missing messages gives the reference error.
14. Branching shared chat with abandoned siblings → only deepest branch imported; skipped count reported.
15. Preview a non-Markdown file, a file over 4 MiB, an empty file, or one containing NUL bytes → clear rejection.
16. Preview a file that was deleted or locked after selection → readable error, previous preview retained.
17. Generated output is JSON while `Preview MD` is pressed → explain, show source.
18. Clipboard unavailable/locked → status message telling the user to select and copy manually.
19. Save name contains `/`, `\`, `..`, or a disallowed suffix → refused before any write.
20. Save folder is relative, missing, read-only, or the target is locked → readable error; no partial file left behind.
21. Save over an existing file with history enabled → previous content archived first; archive failure does not destroy the new write.
22. Association registration denied by policy or registry permissions → app still works; message explains that Windows may need the user to choose the app manually.
23. Windows ignores the requested default because `UserChoice` exists → status explains the confirmation UI; no repeated silent attempts.
24. A second instance launches with a file while the first is busy → path is queued and opened after the current operation, or the window is focused and the path is shown.
25. App launched with a missing path, a directory, or a non-Markdown path → message, normal window, no crash.
26. WebView2 runtime missing or initialization fails → source view still works; show the runtime install link.
27. Very long path (> 260 chars) → works when long-path support is enabled; otherwise a clear error.
28. Machine sleep/network drop during share fetch → cancellation/timeout message; no hang.
29. Extremely large generated output → window remains responsive; preview is truncated only with a visible notice (source remains complete).
30. User cancels any dialog → operation reports "cancelled", never an error dialog.

## 10. Acceptance criteria

- [ ] `dotnet build src/ClaudeMemory.sln -c Release` succeeds with zero warnings-as-errors in `Core`.
- [ ] `dotnet test src/ClaudeMemory.sln -c Release` passes, including golden parity tests against frozen Python outputs.
- [ ] `memory`, `full`, `transcript`, and `json` outputs match the reference on the fixture corpus after normalising only timestamp, version, and absolute fixture root.
- [ ] The four GUI modes generate, preview, copy, and save; history keeps the configured number of versions.
- [ ] `ClaudeMemory.exe <file.md>` opens the file in preview within 2 seconds on this machine.
- [ ] A real Explorer double-click on a `.md` file opens the app after the user confirms the association.
- [ ] Preview rejects `javascript:` links and never executes scripts or raw HTML.
- [ ] Unregistration removes the app's own registry entries and leaves the previous handler untouched.
- [ ] No network call or real registry write occurs during the automated test suite.
- [ ] Python files are removed only after this checklist passes and the human approves the removal phase.

## 11. Compatibility and distribution

- .NET 8 LTS, `win-x64`, self-contained folder publish under `dist/ClaudeMemory/`.
- WebView2 Evergreen Runtime required for preview; source view and generation work without it.
- Long paths enabled via application manifest.
- No Python, pip, pywebview, or tkinter dependency in the shipped app.

## 12. Open questions

None blocking. The Windows default-app limitation is accepted and encoded in FR-20/FR-21/FR-23: the app registers and asks the OS to confirm; it cannot silently force the choice.