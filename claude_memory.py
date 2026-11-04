#!/usr/bin/env python3
"""
claude_memory.py - turn Claude Code session transcripts into a rolling markdown
memory file that Claude can read back via CLAUDE.md.

Source : ~/.claude/projects/<slug>/<session-uuid>.jsonl   (offline, read-only)
Output : a single MEMORY.md (or transcript/json) with signal-first sections.

Design goals
  * stdlib only, python >= 3.9, no network, no writes outside --out/--history dir
  * token-frugal: dedupe + per-section caps + one global char budget
  * deterministic: same inputs -> same output, so the file is diffable
  * tolerant: unknown/partial jsonl records are skipped, never fatal

Usage
  python claude_memory.py                          # memory to stdout
  python claude_memory.py --out memory/MEMORY.md   # write the file
  python claude_memory.py --print                  # stdout AND --out
  python claude_memory.py --list                   # show discovered sessions
  python claude_memory.py --since 7d --sessions 3
  python claude_memory.py --format claude-md       # wrap for CLAUDE.md paste
  python claude_memory.py --mode transcript --out chat.md
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

__version__ = "1.1.0"

HOME = Path(os.environ.get("USERPROFILE") or os.environ.get("HOME") or Path.cwd())
DEFAULT_ROOT = HOME / ".claude"
QUIET = False

BEGIN = "<!-- claude-memory:begin -->"
END = "<!-- claude-memory:end -->"

# ----------------------------------------------------------------------------- share links
# A claude.ai share page (https://claude.ai/share/<uuid>) is an empty JS shell: the
# conversation arrives from this public, unauthenticated JSON endpoint. Verified
# 2026-10-06 against a live share link with a browser UA. Two hard-won details:
#   * the default urllib User-Agent is rejected with 403; a browser UA is required
#   * the endpoint rate-limits, so bursts answer 403 as well -> retry + backoff
SHARE_PAGE_RX = re.compile(
    r"^(?:https?://)?(?:www\.)?claude\.ai/share/([0-9a-fA-F-]{8,64})(?:[/?#].*)?$")
SHARE_UUID_RX = re.compile(r"^[0-9a-fA-F-]{8,64}$")
SHARE_API = ("https://claude.ai/api/chat_snapshots/{uuid}"
             "?rendering_mode=messages&render_all_tools=true")
BROWSER_UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
              "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36")
SHARE_MANUAL_HINT = (
    "open the share link in a browser, DevTools -> Network -> filter 'chat_snapshots' "
    "-> Copy response, then: --share-file <saved.json>")


class ShareError(RuntimeError):
    """Raised when a share link cannot be retrieved or understood."""

# ----------------------------------------------------------------------------- regexes

RX_DECISION = re.compile(
    r"\b(we(?:'ll|\s+will|\s+decided|\s+agreed)|i(?:'ll|\s+will)\s+(?:use|go with|choose|"
    r"switch|add|remove|write)|let'?s\s+(use|go|do|start|build|fix)|going with|"
    r"instead of|rather than|decision|approach is|plan is|chose|chosen|opted|"
    r"i(?:'d| would)\s+recommend"
    # Arabic: we decided, we will use, the approach is, plan is, chose, recommend
    r"|قررنا|سنستخدم|سنستخدم|سنختار|سنضيف|سنزيل|سنكتب|لنستخدم|لنذهب|لنبدأ|لابنى|لنصلح|"
    r"بدلا\s+من|القرار|النهج|الخطة|اخترنا|نوصي|سنهتم)\b",
    re.I,
)
RX_FACT = re.compile(
    r"\b(must(?: not)?|never|always|cannot|can'?t|do(?:n'?t| not)|required|requires|"
    r"depends on|only if|note that|important|caveat|constraint|only works|"
    r"is read[- ]only|has no|there is no"
    # Arabic: must, never, always, cannot, don't, required, note that, important
    r"|يجب|يجب\s+لا|أبدا|دائما|لا\s+يمكن|لا\s+تستطيع|مطلوب|لازم|علما\s+أن|مهم|"
    r"يعتمد\s+على|فقط\s+إذا|لا\s+يوجد|غير\s+مسموح|ممنوع)\b",
    re.I,
)
RX_PROBLEM = re.compile(
    r"\b(error|errors|exception|traceback|fail(?:ed|s|ing|ure)?|broken|breaks?|"
    r"crash(?:ed|es)?|segfault|regression|root cause|block(?:ed|er)|denied|refused|"
    r"timed? ?out|hangs?|deadlock|leak|mismatch|wrong|missing"
    # Arabic: error, failed, broken, crash, problem, wrong, missing, denied
    r"|خطأ|فشل|توقف|معطل|مشكلة|عطل|غير\s+صحيح|مفقود|مرفوض)"
    r"\b",
    re.I,
)
RX_OPEN = re.compile(
    r"(\?\s*$|\bTODO\b|\bnext steps?\b|\bremaining\b|\bstill need|\bopen (?:question|item|issue)s?\b|"
    r"\bfollow[- ]?up\b|\bblocker|\bpending\b|\bunresolved\b|\bnot yet\b"
    # Arabic: next steps, remaining, still need, open, pending, unresolved
    r"|الخطوات\s+التالية|المتبقي|ما\s+زال\s+مطلوب|مفتوح|قيد\s+الانتظار|غير\s+محلول|لم\s+يتم\s+بعد)",
    re.I,
)
RX_PREF = re.compile(
    r"\b(prefer|convention|style guide|house rule|standard is|avoid|never mix|"
    r"always use|idomatic|keep (?:it|them) simple|YAGNI"
    # Arabic: prefer, convention, standard, avoid, always use, keep simple
    r"|أفضل|اتفاقية|معيار|قاعدة|تجنب|دائما\s+استخدم|على\s+بساطة)\b",
    re.I,
)
RX_PATH = re.compile(
    r"(?<![\w/\\])((?:[A-Za-z]:)?[\\/](?:[\w.\- ]+[\\/])*[\w.\-]+\."
    r"(?:py|cpp|cxx|h|hpp|md|rst|json|jsonl|yml|yaml|toml|ini|cfg|conf|txt|log|ts|js|tsx|jsx|"
    r"rs|go|sh|ps1|bat|sql|xml|csv|zip|dll|exe|so|elf|map|asm|s|lua|dat|bin))\b"
)
RX_HEX = re.compile(r"\b0x[0-9A-Fa-f]{2,}\b")
RX_VERSION = re.compile(r"\bv?\d+\.\d+(?:\.\d+){1,3}\b")
RX_SIZE = re.compile(r"\b\d+(?:\.\d+)?\s?(?:ms|us|ns|s|sec|secs|second|seconds|min|minutes|"
                     r"hour|hours|day|days|kb|mb|gb|kib|mib|gib|bytes?|b|%|x|times)\b", re.I)
RX_BULLET = re.compile(r"^\s*(?:[-*+•]|\d+[.)])\s+")
RX_ADDR = re.compile(r"\b(?:0x)?[0-9A-Fa-f]{6,8}\b")
# short acknowledgements restate the prompt; they never carry new decisions
RX_LOW_VALUE = re.compile(r"^(yes|no|nope|yeah|correct|right|sure|ok|okay|exactly|indeed|"
                          r"got it|understood|thanks"
                          # Arabic: yes, no, correct, ok, understood, thanks
                          r"|نعم|لا|صحيح|حسنا|فهمت|شكرا|تماما|بالضبط)"
                          r"\b[\s,.!:;\u2014-]", re.I)
# a bare "Remaining work:" is a heading, not an item.
# The Arabic range covers the main Arabic block plus supplements so headings
# like "العمل المتبقي:" are recognised too.
RX_HEADER_LINE = re.compile(r"^[\w\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff]"
                             r"[\w\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff \t/&+-]{0,58}:$")

# ----------------------------------------------------------------------------- arabic detection

# Unicode ranges for Arabic script detection
ARABIC_RX = re.compile(r"[\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff\u0870-\u089f]")


def has_arabic(text: str) -> bool:
    """Return True when the text contains Arabic script characters."""
    return bool(ARABIC_RX.search(text or ""))


def arabic_ratio(text: str) -> float:
    """Fraction of characters that are Arabic script (0.0 to 1.0)."""
    if not text:
        return 0.0
    chars = [c for c in text if c.isalpha()]
    if not chars:
        return 0.0
    return sum(1 for c in chars if ARABIC_RX.match(c)) / len(chars)


# Bilingual section headers: English with Arabic translation.
# When the source content is primarily Arabic, these headers make the
# generated MEMORY.md readable for Arabic-speaking users.
BILINGUAL_TITLES = {
    "Sessions": "الجلسات",
    "Compactor summaries": "ملخصات الجلسات",
    "Most recent exchange": "آخر تبادل",
    "What was asked (verbatim user prompts)": "ما تم طلبه ( prompts المستخدم حرفيا)",
    "Decisions taken": "القرارات المتخذة",
    "Preferences and conventions": "التفضيلات والاتفاقيات",
    "Constraints and must-nots": "القيود والمحظورات",
    "Files changed": "الملفات المعدلة",
    "Files read / searched (top)": "الملفات المقروءة / بحثت (الأعلى)",
    "Commands run": "الأوامر المنفذة",
    "Subagents used": "الوكلاء الفرعيون المستخدمون",
    "Other tools used": "أدوات أخرى مستخدمة",
    "Problems, failures and root causes seen": "المشاكل والأعطال وأسبابها الجذرية",
    "Open items / TODOs": "العناصر المفتوحة / المهام",
    "Most recent plan or checklist": "أحدث خطة أو قائمة تحقق",
    "Concrete identifiers (paths, values, versions)": "معرفات ملموسة (مسارات، قيم، إصدارات)",
    "Full conversation (nothing summarised away)": "المحادثة الكاملة (بدون تلخيص)",
    "Project memory — rolling": "ذاكرة المشروع — متجددة",
}


def bilingual_title(english_title: str, sessions: Optional[List[Session]],
                    lang: str = "auto") -> str:
    """Return a section header based on the language setting.

    lang="auto"  -> bilingual header when sessions contain Arabic content.
    lang="en"    -> English only.
    lang="ar"    -> Arabic only.
    lang="both"  -> always bilingual (English | Arabic).
    """
    if not sessions:
        return english_title
    arabic = BILINGUAL_TITLES.get(english_title, "")
    if lang == "en":
        return english_title
    if lang == "ar":
        return arabic if arabic else english_title
    if lang == "both":
        return f"{english_title} | {arabic}" if arabic else english_title
    # auto-detect
    all_text = " ".join(
        t.text for s in sessions for t in s.turns if t.text
    )
    if arabic_ratio(all_text) > 0.15 and arabic:
        return f"{english_title} | {arabic}"
    return english_title


# claude.ai exposes its own tool names, not Claude Code's Read/Edit/Bash
WEB_TOOL_ALIASES = {
    "bash_tool": "Bash", "bash": "Bash", "shell": "Bash", "run_command": "Bash",
    "view": "Read", "read_file": "Read", "view_file": "Read",
    "str_replace_editor": "Edit", "str_replace_based_edit_tool": "Edit",
    "create_file": "Write", "write_file": "Write", "file_editor": "Write",
    "present_files": "PresentFiles", "present_files_tool": "PresentFiles",
    "web_search": "WebSearch", "web_fetch": "WebFetch", "fetch": "WebFetch",
    "todo": "TodoWrite", "artifact": "Artifact", "memory": "Memory",
    "computer": "Computer", "task": "Task", "agent": "Task",
}
WEB_TOOL_KEYS = {
    "Bash": ["command", "commandLine", "cmd", "script"],
    "Read": ["path", "file_path", "filePath", "target_file"],
    "Edit": ["path", "file_path", "command"],
    "Write": ["path", "file_path", "filePath", "target_file"],
    "PresentFiles": ["filepaths", "file_paths", "paths", "files"],
    "WebSearch": ["query", "url"],
    "WebFetch": ["url", "prompt", "query"],
}


def norm_key(line: str) -> str:
    """Normalise a line for dedup comparison.

    Preserves both Latin and Arabic characters so Arabic text doesn't collapse
    to an empty key (which would make every Arabic line a "duplicate").
    """
    return re.sub(r"[^a-z0-9\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff]+", "", line.lower())[:120]


# ----------------------------------------------------------------------------- helpers


def eprint(*a: Any) -> None:
    print(*a, file=sys.stderr)


# ----------------------------------------------------------------------------- token estimate
#
# Every "how big is this in tokens?" number used to be len(text) // 4. That ratio
# is calibrated for English. It is badly wrong for other scripts: Claude's
# tokenizer needs substantially more tokens per character for Arabic, Cyrillic
# and CJK than for ASCII text, so an Arabic MEMORY.md reported as "~3500 tokens"
# can actually be several thousand more.
#
# These are CHARS-PER-TOKEN divisors, averaged per script. They are an estimate,
# not a tokenizer - see TOKEN_RATIO_IS_ESTIMATE below - but they are directionally
# honest in a way that a single // 4 is not.
TOKEN_CHARS_PER_TOKEN_LATIN = 4.0
TOKEN_CHARS_PER_TOKEN_ARABIC = 1.6
TOKEN_CHARS_PER_TOKEN_CYRILLIC = 2.2
TOKEN_CHARS_PER_TOKEN_CJK = 1.0

TOKEN_RATIO_IS_ESTIMATE = True


def estimate_tokens(text: str) -> int:
    """Rough token count, weighted per script instead of assuming ASCII.

    English/prose text runs about 4 chars per token. Arabic runs closer to
    ~1.6 chars per token, so the old len(text) // 4 under-reported an Arabic
    memory by roughly 2.5x. Counting each script at its own rate keeps the
    budget note honest for whichever language the session was actually in.
    """
    if not text:
        return 0
    latin = arabic = cyrillic = cjk = other = 0
    for ch in text:
        if ch.isascii():
            latin += 1
        elif ARABIC_RX.match(ch):
            arabic += 1
        elif "\u0400" <= ch <= "\u04ff":          # cyrillic
            cyrillic += 1
        elif ("\u3040" <= ch <= "\u30ff"          # kana
              or "\u3400" <= ch <= "\u9fff"        # han
              or "\uf900" <= ch <= "\ufaff"):      # han compat
            cjk += 1
        else:
            other += 1
    total = (latin / TOKEN_CHARS_PER_TOKEN_LATIN
             + arabic / TOKEN_CHARS_PER_TOKEN_ARABIC
             + cyrillic / TOKEN_CHARS_PER_TOKEN_CYRILLIC
             + cjk / TOKEN_CHARS_PER_TOKEN_CJK
             + other / TOKEN_CHARS_PER_TOKEN_LATIN)
    return int(total + 0.5)


ASCII_MAP = {
    "\u2192": "->", "\u00b7": "*", "\u00d7": "x", "\u2026": "...", "\u2014": "--",
    "\u2013": "-", "\u2022": "-", "\u2018": "'", "\u2019": "'", "\u201c": '"',
    "\u201d": '"', "\u2265": ">=", "\u2264": "<=", "\u2713": "[ok]", "\u2717": "[x]",
    "\u2588": "#", "\u2500": "-", "\u00a0": " ",
}


def to_ascii(text: str) -> str:
    """Downgrade typographic glyphs for legacy consoles / cp1252 terminals.

    Only *decoration* is downgraded: arrows, bullets, smart quotes, box glyphs.
    Letters are left alone, because replacing them destroys the content itself -
    Arabic (and any other non-Latin script) used to come out as "?????", which
    looked like a bug in the generator rather than the ASCII switch.

    Anything still outside ASCII after that is either a letter (kept) or a symbol
    with no ASCII equivalent (dropped to '?', as before).
    """
    for src, dst in ASCII_MAP.items():
        text = text.replace(src, dst)
    out = []
    for ch in text:
        if ch.isascii():
            out.append(ch)
        elif ch.isalpha():
            out.append(ch)          # content in any script: never touch it
        else:
            out.append("?")          # untranslatable symbol
    return "".join(out)


def configure_stdio() -> None:
    """Windows consoles default to cp1252 and crash on the glyphs used below.

    Under pythonw.exe (no console) sys.stdout/sys.stderr are None, and any
    print() would raise, so point them at the null device first.
    """
    if sys.stdout is None:
        sys.stdout = open(os.devnull, "w", encoding="utf-8")
    if sys.stderr is None:
        sys.stderr = open(os.devnull, "w", encoding="utf-8")
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass


def now_utc() -> _dt.datetime:
    return _dt.datetime.now(_dt.timezone.utc)


def iso_now() -> str:
    return now_utc().strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_ts(value: Any) -> Optional[_dt.datetime]:
    if not isinstance(value, str) or not value:
        return None
    text = value.strip().replace("Z", "+00:00")
    for attempt in (text, text[:19], text.replace(" ", "T", 1)[:19]):
        try:
            dt = _dt.datetime.fromisoformat(attempt)
            return dt if dt.tzinfo else dt.replace(tzinfo=_dt.timezone.utc)
        except ValueError:
            continue
    return None


def parse_since(text: str) -> Optional[_dt.datetime]:
    """Accept 30m / 7d / 2h, YYYY-MM-DD, or an ISO timestamp."""
    text = text.strip()
    now = now_utc()
    m = re.fullmatch(r"(\d+)\s*([mhdw])", text, re.I)
    if m:
        n = int(m.group(1))
        unit = m.group(2).lower()
        delta = {"m": _dt.timedelta(minutes=n), "h": _dt.timedelta(hours=n),
                 "d": _dt.timedelta(days=n), "w": _dt.timedelta(weeks=n)}[unit]
        return now - delta
    dt = parse_ts(text)
    if dt is not None:
        return dt
    for fmt in ("%Y-%m-%d %H:%M", "%Y-%m-%d", "%m/%d/%Y", "%d/%m/%Y"):
        try:
            return _dt.datetime.strptime(text, fmt).replace(tzinfo=_dt.timezone.utc)
        except ValueError:
            continue
    raise argparse.ArgumentTypeError(f"cannot parse --since value: {text!r}")


def one_line(text: str, limit: int = 400) -> str:
    flat = re.sub(r"\s+", " ", text or "").strip()
    return flat if len(flat) <= limit else flat[: limit - 1].rstrip() + "\u2026"


def sentences(text: str, limit: int = 400) -> List[str]:
    """Split a text blob into candidate claim lines.

    Newlines separate, and prose is additionally split on sentence boundaries so
    that one sentence carrying two ideas ("must never X. Convention: always Y.")
    can be filed under two different sections instead of being lost to whichever
    section runs first. Checklist bullets are never split.
    """
    out: List[str] = []
    for raw in (text or "").splitlines():
        line = raw.strip().lstrip("#>*`").strip()
        if not line:
            continue
        if RX_BULLET.match(raw):
            out.append(one_line(RX_BULLET.sub("", line).strip(), limit))
            continue
        pieces = re.split(r"(?<=[.!?۔؟؛])\s+(?=[A-Z`\"'\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff\d\u2014-])", line)
        for piece in pieces:
            piece = piece.strip()
            if not piece:
                continue
            if len(piece) > limit:
                piece = one_line(piece, limit * 2)
            out.append(one_line(piece, limit))
    return [s for s in out if s]


def dedupe(items: Iterable[str], limit: int) -> Tuple[List[str], int]:
    seen: set = set()
    kept: List[str] = []
    dropped = 0
    for item in items:
        key = re.sub(r"[^a-z0-9]+", "", item.lower())[:120]
        if not key or key in seen:
            dropped += 1
            continue
        seen.add(key)
        kept.append(item)
        if len(kept) >= limit:
            dropped += 0
            break
    return kept, dropped


def dedupe_recent(items: Iterable[str], limit: int) -> Tuple[List[str], int]:
    """Like dedupe(), but the *newest* items win.

    Decisions, errors and TODOs are superseded over time, so a summary that keeps
    the first match reports a stale state and silently drops what was concluded
    most recently - including the end of the conversation.
    """
    rows = list(items)
    seen: set = set()
    picked: List[int] = []
    dropped = 0
    for idx in range(len(rows) - 1, -1, -1):
        key = re.sub(r"[^a-z0-9]+", "", rows[idx].lower())[:120]
        if not key or key in seen:
            dropped += 1
            continue
        seen.add(key)
        picked.append(idx)
        if len(picked) >= limit:
            break
    picked.reverse()
    return [rows[i] for i in picked], dropped


def body_quotes(record: Dict[str, Any]) -> str:
    """Best-effort extract of text from a tool_result content payload."""
    content = record.get("content")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts = []
        for blk in content:
            if isinstance(blk, dict) and blk.get("type") == "text":
                parts.append(str(blk.get("text", "")))
        return "\n".join(parts)
    return ""


def tool_brief(name: str, inp: Any, limit: int = 160) -> str:
    """One-line summary of a tool_use input, without echoing whole file bodies."""
    if not isinstance(inp, dict):
        return one_line(str(inp or ""), 80)
    if name == "Task":
        who = one_line(str(inp.get("subagent_type") or "agent"), 40)
        what = one_line(str(inp.get("description") or inp.get("prompt") or ""), limit - len(who) - 3)
        return f"{who}: {what}" if what else who

    def scalar(keys: Sequence[str]) -> str:
        for key in keys:
            val = inp.get(key)
            if isinstance(val, str) and val.strip():
                return one_line(f"{key}={val}", limit)
            if isinstance(val, list) and val:
                # e.g. present_files {"filepaths": ["a", "b"]}
                items = [one_line(str(v), 60) for v in val[:4] if isinstance(v, (str, int))]
                if items:
                    more = f" (+{len(val) - len(items)})" if len(val) > len(items) else ""
                    return one_line(f"{key}=" + ", ".join(items) + more, limit)
        return ""

    hit = scalar(WEB_TOOL_KEYS.get(name, []))
    if hit:
        return hit
    hit = scalar(["file_path", "notebook_path", "path", "url", "pattern", "query",
                  "command", "description", "prompt"])
    if hit:
        return hit
    if name in ("Edit", "MultiEdit", "Write", "NotebookEdit"):
        return ", ".join(sorted(k for k in inp.keys()))[:limit]
    if isinstance(inp.get("edits"), list):
        return f"{len(inp['edits'])} edits"
    return ", ".join(sorted(inp.keys()))[:limit] or "(no args)"


def fmt_cwd(cwd: str, home: Path) -> str:
    if not cwd:
        return "(unknown cwd)"
    p = Path(cwd)
    try:
        if home and str(p).lower().startswith(str(home).lower()):
            rel = p.relative_to(home)
            return "~/" + str(rel).replace("\\", "/")
    except ValueError:
        pass
    text = str(p).replace("\\", "/")
    return text if len(text) <= 70 else "\u2026" + text[-69:]


def fmt_dur(seconds: float) -> str:
    seconds = max(0, int(seconds))
    if seconds < 60:
        return f"{seconds}s"
    if seconds < 3600:
        return f"{seconds // 60}m {seconds % 60}s"
    return f"{seconds // 3600}h {(seconds % 3600) // 60}m"


# ----------------------------------------------------------------------------- budget


class Budget:
    """Global character budget shared by every rendered section."""

    FLOOR = 200

    def __init__(self, total: int) -> None:
        self.total = max(self.FLOOR, total)
        self.requested = total
        self.used = 0
        self.trimmed = 0
        if total < self.FLOOR and not QUIET:
            eprint(f"[claude-memory] --max-chars {total} raised to floor {self.FLOOR}")

    @property
    def left(self) -> int:
        return self.total - self.used

    def take(self, lines: Sequence[str], bullet: str = "- ", indent: str = "") -> List[str]:
        out: List[str] = []
        for line in lines:
            cost = len(line) + len(indent) + len(bullet) + 1
            if cost > self.left:
                self.trimmed += 1
                continue
            self.used += cost
            out.append(f"{indent}{bullet}{line}" if bullet else f"{indent}{line}")
        return out

    def line(self, text: str) -> bool:
        cost = len(text) + 1
        if cost > self.left:
            self.trimmed += 1
            return False
        self.used += cost
        return True


# ----------------------------------------------------------------------------- parsing


@dataclass
class Turn:
    role: str            # user | assistant | system | note
    text: str = ""
    tools: List[Tuple[str, str]] = field(default_factory=list)
    errors: List[str] = field(default_factory=list)
    ts: Optional[_dt.datetime] = None
    sidechain: bool = False


@dataclass
class Session:
    path: Path
    sid: str = ""
    cwd: str = ""
    branch: str = ""
    version: str = ""
    model: str = ""
    start: Optional[_dt.datetime] = None
    end: Optional[_dt.datetime] = None
    turns: List[Turn] = field(default_factory=list)
    summaries: List[str] = field(default_factory=list)
    skipped: int = 0
    truncated_file: bool = False

    # ---- derived views
    @property
    def duration(self) -> float:
        if self.start and self.end:
            return (self.end - self.start).total_seconds()
        return 0.0

    def prompts(self) -> List[str]:
        out = []
        for t in self.turns:
            if t.role == "user" and t.text.strip() and not t.tools and not t.errors:
                out.append(t.text.strip())
        return out

    def tool_calls(self) -> List[Tuple[str, str, bool]]:
        out = []
        for t in self.turns:
            for name, brief in t.tools:
                out.append((name, brief, t.sidechain))
        return out

    def error_lines(self) -> List[str]:
        out = []
        for t in self.turns:
            out.extend(t.errors)
        return out

    def assistant_text(self) -> List[str]:
        out = []
        for t in self.turns:
            if t.role == "assistant" and t.text.strip():
                out.append(t.text)
        return out

    def label(self) -> str:
        first = self.prompts()[0] if self.prompts() else ""
        return one_line(first, 70) if first else (self.summaries[0] if self.summaries else "(no prompt)")


def _iter_records(path: Path, max_bytes: int) -> Tuple[List[Dict[str, Any]], bool]:
    """Read jsonl defensively; returns (records, truncated)."""
    records: List[Dict[str, Any]] = []
    truncated = False
    try:
        size = path.stat().st_size
    except OSError:
        return records, truncated
    tail_from = size - max_bytes if 0 < max_bytes < size else 0
    try:
        with path.open("r", encoding="utf-8", errors="replace") as fh:
            if tail_from:
                truncated = True
                fh.seek(tail_from)
                fh.readline()  # discard partial first line
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if isinstance(rec, dict):
                    records.append(rec)
    except OSError:
        return records, truncated
    return records, truncated


def parse_session(path: Path, opts: "Options") -> Session:
    sess = Session(path=path, sid=path.stem)
    records, truncated = _iter_records(path, opts.max_file_bytes)
    sess.truncated_file = truncated

    for rec in records:
        rtype = rec.get("type")
        ts = parse_ts(rec.get("timestamp"))
        if ts:
            sess.start = ts if sess.start is None or ts < sess.start else sess.start
            sess.end = ts if sess.end is None or ts > sess.end else sess.end
        for key in ("sessionId", "session_id"):
            if isinstance(rec.get(key), str) and rec[key]:
                sess.sid = rec[key]
        if isinstance(rec.get("cwd"), str) and rec["cwd"]:
            sess.cwd = rec["cwd"]
        if isinstance(rec.get("gitBranch"), str) and rec["gitBranch"]:
            sess.branch = rec["gitBranch"]
        if isinstance(rec.get("version"), str) and rec["version"]:
            sess.version = rec["version"]

        if rtype == "summary":
            s = rec.get("summary")
            if isinstance(s, str) and s.strip():
                sess.summaries.append(s.strip())
            continue

        message = rec.get("message")
        if not isinstance(message, dict):
            if isinstance(rec.get("content"), str) and rtype in ("system", "user"):
                sess.turns.append(Turn(role="system", text=rec["content"], ts=ts))
            else:
                sess.skipped += 1
            continue

        role = message.get("role") or rtype
        model = message.get("model")
        if isinstance(model, str) and model:
            sess.model = model

        if rec.get("isMeta"):
            sess.skipped += 1
            continue
        sidechain = bool(rec.get("isSidechain"))
        if sidechain and opts.sidechains == "drop":
            sess.skipped += 1
            continue

        content = message.get("content")
        texts: List[str] = []
        tools: List[Tuple[str, str]] = []
        errors: List[str] = []

        if isinstance(content, str):
            texts.append(content)
        elif isinstance(content, list):
            for blk in content:
                if not isinstance(blk, dict):
                    continue
                btype = blk.get("type")
                if btype == "text":
                    texts.append(str(blk.get("text", "")))
                elif btype == "thinking" and opts.include_thinking:
                    texts.append(str(blk.get("thinking", "")))
                elif btype == "tool_use":
                    name = str(blk.get("name", "tool"))
                    tools.append((name, tool_brief(name, blk.get("input"),
                                                  120 if sidechain else 200)))
                elif btype == "tool_result":
                    if blk.get("is_error"):
                        errors.append(one_line(body_quotes(blk) or "tool error", 200))
        else:
            sess.skipped += 1
            continue

        text = "\n".join(t for t in texts if t and t.strip()).strip()
        # assistant noise that carries no memory value
        if not text and not tools and not errors:
            sess.skipped += 1
            continue
        if role not in ("user", "assistant", "system"):
            role = "assistant" if tools else "user"

        sess.turns.append(Turn(role=role, text=text, tools=tools, errors=errors,
                               ts=ts, sidechain=sidechain))
    return sess


# ----------------------------------------------------------------------------- discovery


def project_slug(cwd: str) -> str:
    p = Path(cwd) if cwd else Path("unknown")
    return str(p).replace("\\", "-").replace("/", "-").replace(":", "-")


# ----------------------------------------------------------------------------- share import


def share_uuid(value: str) -> str:
    """Accept a full share URL, a bare uuid, or nothing useful."""
    text = (value or "").strip().strip("<>\"'")
    if not text:
        raise ShareError("empty share link")
    m = SHARE_PAGE_RX.match(text)
    if m:
        return m.group(1)
    if SHARE_UUID_RX.match(text):
        return text
    raise ShareError(
        f"not a claude.ai share link: {text[:80]!r}\n"
        "expected https://claude.ai/share/<uuid>")


def fetch_share(value: str, attempts: int = 3, timeout: int = 30) -> Dict[str, Any]:
    """GET the public share payload. Retries, because the endpoint rate-limits."""
    import time
    import urllib.error
    import urllib.request

    uuid = share_uuid(value)
    url = SHARE_API.format(uuid=uuid)
    headers = {
        "User-Agent": BROWSER_UA,       # the default urllib UA is 403'd
        "Accept": "application/json",
        "Referer": f"https://claude.ai/share/{uuid}",
    }
    backoff = [2.0, 5.0, 12.0]
    last = "unknown error"
    for attempt in range(attempts):
        try:
            req = urllib.request.Request(url, headers=headers)
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read()
            try:
                return json.loads(raw)
            except json.JSONDecodeError:
                raise ShareError(
                    f"share payload was not JSON (got {len(raw)} bytes of "
                    f"{resp.headers.get('Content-Type', '?')}). "
                    "Claude may have changed the page; " + SHARE_MANUAL_HINT)
        except urllib.error.HTTPError as e:
            last = f"HTTP {e.code} {e.reason}"
            # 403 also means rate-limited here, so both are worth retrying
            if e.code not in (403, 429, 500, 502, 503, 504):
                break
            if e.code == 404:
                raise ShareError("share link not found or no longer public (HTTP 404)")
        except ShareError:
            raise
        except Exception as e:                      # URLError, timeout, ssl, ...
            last = f"{type(e).__name__}: {e}"
        if attempt < attempts - 1:
            if not QUIET:
                eprint(f"[claude-memory] share fetch failed ({last}), "
                       f"retrying in {backoff[attempt]:.0f}s")
            time.sleep(backoff[attempt])
    raise ShareError(f"could not read share link after {attempts} attempts: {last}. "
                     "This endpoint rate-limits bursts - wait a minute, or " + SHARE_MANUAL_HINT)


def share_chain(messages: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Reduce a message list to the active conversation branch, oldest first.

    Shared conversations are usually one linear chain, but a reply-to-reply edit
    can leave sibling messages. Follow the deepest leaf, breaking ties by index,
    so a branch that was later abandoned does not pollute the memory.
    """
    if not messages:
        return []
    by_uuid = {m.get("uuid"): m for m in messages if m.get("uuid")}

    def index_of(msg: Dict[str, Any]) -> int:
        val = msg.get("index")
        return val if isinstance(val, int) else 0

    def chain_to_root(msg: Dict[str, Any]) -> List[Dict[str, Any]]:
        out, seen = [], set()
        cur: Optional[Dict[str, Any]] = msg
        while cur is not None and cur.get("uuid") not in seen:
            seen.add(cur.get("uuid"))
            out.append(cur)
            parent = cur.get("parent_message_uuid")
            cur = by_uuid.get(parent) if parent else None
        out.reverse()
        return out

    children: Dict[str, int] = {}
    for m in messages:
        parent = m.get("parent_message_uuid")
        if parent:
            children[parent] = children.get(parent, 0) + 1
    leaves = [m for m in messages if not children.get(m.get("uuid"), 0)]
    if not leaves:
        leaves = list(messages)
    best = max(leaves, key=lambda m: (len(chain_to_root(m)), index_of(m)))
    return chain_to_root(best)


def _block_timestamp(blocks: List[Dict[str, Any]]) -> Optional[_dt.datetime]:
    for blk in blocks:
        ts = parse_ts(blk.get("stop_timestamp")) or parse_ts(blk.get("start_timestamp"))
        if ts:
            return ts
    return None


def parse_share_payload(payload: Dict[str, Any], label_hint: str = "",
                        include_thinking: bool = False) -> Session:
    """Turn a /api/chat_snapshots payload into the same Session model as jsonl."""
    if not isinstance(payload, dict):
        raise ShareError("share payload is not a JSON object")
    messages = payload.get("chat_messages")
    if not isinstance(messages, list) or not messages:
        # a saved HTML shell has no conversation in it at all
        if isinstance(payload.get("chat_messages"), type(None)) and "html" in payload:
            raise ShareError(
                "that looks like a saved share *page*, which contains no conversation "
                "(claude.ai renders it client-side). Save the chat_snapshots JSON "
                "response instead. " + SHARE_MANUAL_HINT)
        raise ShareError(
            "no chat_messages in the payload - is this really a chat_snapshots "
            "response? " + SHARE_MANUAL_HINT)

    uuid = str(payload.get("uuid") or payload.get("conversation_uuid") or "shared")
    sess = Session(path=Path(f"share-{uuid}.json"), sid=uuid)
    sess.start = parse_ts(payload.get("created_at"))
    sess.end = parse_ts(payload.get("updated_at"))
    sess.cwd = ""
    sess.branch = "shared-link"
    sess.model = "claude.ai"

    chain = share_chain(messages)
    sess.skipped = len(messages) - len(chain)
    title = payload.get("snapshot_name") or label_hint
    if title:
        sess.summaries.insert(0, f"[shared] {title}")

    for msg in chain:
        role = "assistant" if msg.get("sender") == "assistant" else "user"
        blocks = msg.get("content")
        if not isinstance(blocks, list):
            blocks = []
        texts: List[str] = []
        tools: List[Tuple[str, str]] = []
        errors: List[str] = []
        for blk in blocks:
            if not isinstance(blk, dict):
                continue
            if blk.get("hidden_in_chat"):
                continue
            btype = blk.get("type")
            if btype == "text":
                body = blk.get("text")
                if isinstance(body, str) and body.strip():
                    texts.append(body)
            elif btype == "tool_use":
                raw_name = str(blk.get("name") or "tool")
                name = WEB_TOOL_ALIASES.get(raw_name, raw_name)
                tools.append((name, tool_brief(name, blk.get("input"))))
            elif btype == "tool_result":
                if blk.get("is_error"):
                    body = blk.get("message") or blk.get("content")
                    errors.append(one_line(share_result_text(body), 220))
            elif btype in ("thinking", "redacted_thinking"):
                if include_thinking:
                    body = blk.get("thinking") or blk.get("data")
                    if isinstance(body, str) and body.strip():
                        texts.append(body)
        text = "\n".join(texts).strip()
        # older exports carry a flat "text" field instead of content blocks, so the
        # fallback has to run before the "nothing to keep" check
        if not text and isinstance(msg.get("text"), str) and msg["text"].strip():
            text = msg["text"].strip()
        if not text and not tools and not errors:
            sess.skipped += 1
            continue
        ts = parse_ts(msg.get("created_at")) or _block_timestamp(blocks)
        sess.turns.append(Turn(role=role, text=text, tools=tools, errors=errors, ts=ts))
        if msg.get("compaction_summary"):
            summary = msg["compaction_summary"]
            if isinstance(summary, str):
                sess.summaries.append(summary)
            elif isinstance(summary, dict):
                sess.summaries.append(one_line(str(summary.get("summary") or summary), 300))

    # last resort: some payloads only carry timestamps inside the blocks
    stamps = [t.ts for t in sess.turns if t.ts]
    if stamps:
        sess.start = sess.start or min(stamps)
        sess.end = sess.end or max(stamps)
    return sess


_THINKING_OK = False


def opts_thinking_ok() -> bool:
    return _THINKING_OK


def share_result_text(body: Any) -> str:
    """tool_result payloads arrive as a string, a block list, or an object."""
    if isinstance(body, str):
        return body
    if isinstance(body, list):
        parts = []
        for blk in body:
            if isinstance(blk, dict):
                parts.append(str(blk.get("text") or blk.get("content") or ""))
            elif isinstance(blk, str):
                parts.append(blk)
        return "\n".join(p for p in parts if p)
    if isinstance(body, dict):
        for key in ("text", "content", "message", "stdout", "stderr", "output"):
            val = body.get(key)
            if isinstance(val, str) and val.strip():
                return val
            if isinstance(val, list):
                return share_result_text(val)
        return one_line(str(body), 220)
    return one_line(str(body or ""), 220)


def load_share_file(path: Path, include_thinking: bool = False) -> Session:
    """Import a payload saved earlier (offline). Also tolerates a raw json array."""
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError as e:
        raise ShareError(f"cannot read {path}: {e}")
    text_stripped = text.lstrip()
    if text_stripped[:1] not in ("{", "["):
        raise ShareError(f"{path.name} does not look like JSON "
                         "(share page HTML has no conversation in it)")
    try:
        data = json.loads(text_stripped)
    except json.JSONDecodeError as e:
        raise ShareError(f"{path.name} is not valid JSON: {e}")
    if isinstance(data, list):
        data = {"chat_messages": data, "uuid": path.stem,
                "snapshot_name": path.stem}
    sess = parse_share_payload(data, label_hint=path.stem,
                               include_thinking=include_thinking)
    sess.path = path
    return sess


def discover(root: Path, projects_dir: Optional[Path]) -> List[Session]:
    base = projects_dir or (root / "projects")
    files: List[Path] = []
    if base.is_dir():
        for jsonl in base.rglob("*.jsonl"):
            if jsonl.is_file() and jsonl.stat().st_size > 0:
                files.append(jsonl)
    files.sort(key=lambda p: (p.stat().st_mtime, p.name))
    return files


def select(sessions: List[Session], opts: "Options") -> List[Session]:
    chosen = sessions
    needles: List[str] = []
    if opts.here:
        needles.append(str(Path.cwd()).replace("\\", "/").lower())
    if opts.cwd:
        needles.append(opts.cwd.replace("\\", "/").lower())
    if needles:
        def matches(s: Session) -> bool:
            hay = f"{s.cwd} {s.path}".replace("\\", "/").lower()
            return any(n in hay for n in needles)
        chosen = [s for s in chosen if matches(s)]
    if opts.branch:
        chosen = [s for s in chosen if s.branch.lower() == opts.branch.lower()]
    if opts.since is not None:
        cutoff = opts.since
        kept = []
        for s in chosen:
            anchor = s.end or s.start
            if anchor is None or anchor >= cutoff:
                kept.append(s)
        chosen = kept
    if opts.sessions and opts.sessions > 0:
        chosen = chosen[-opts.sessions:]
    return chosen


# ----------------------------------------------------------------------------- rendering


def session_where(s: "Session") -> str:
    """Where a session happened, phrased for humans."""
    if s.cwd:
        return fmt_cwd(s.cwd, HOME)
    if s.branch == "shared-link":
        return "(claude.ai shared chat)"
    return "(unknown cwd)"


def render_header(sessions: List[Session], opts: "Options") -> List[str]:
    turns = sum(len(s.turns) for s in sessions)
    prompts = sum(len(s.prompts()) for s in sessions)
    calls = sum(len(s.tool_calls()) for s in sessions)
    span = ""
    stamps = [s.start for s in sessions if s.start]
    if stamps:
        span = f"{min(stamps):%Y-%m-%d} \u2192 {max(stamps):%Y-%m-%d}"
    cwds = sorted({s.cwd for s in sessions if s.cwd})
    shared = [s for s in sessions if s.branch == "shared-link"]
    local = [s for s in sessions if s.branch != "shared-link"]

    def plural(n: int, word: str) -> str:
        return f"{n} {word}{'' if n == 1 else 's'}"

    if shared and local:
        origin = (f"from {plural(len(local), 'local session')} and "
                  f"{plural(len(shared), 'shared claude.ai chat')}")
    elif shared:
        origin = f"from {plural(len(shared), 'shared claude.ai chat')}"
    else:
        origin = f"from {plural(len(local), 'Claude Code session')}"

    lines = [
        f"# {bilingual_title('Project memory — rolling', sessions, opts.lang)}",
        "",
        f"_Generated {iso_now()} by claude_memory.py v{__version__} {origin}"
        + (f", {span}" if span else "")
        + "._",
        "",
        f"- Turns: **{turns}** ({prompts} user prompts, {calls} tool calls)"
        f" \u00b7 skipped noise records: **{sum(s.skipped for s in sessions)}**",
    ]
    if cwds:
        shown = cwds[:4]
        lines.append("- Working dir(s): " + ", ".join(f"`{c}`" for c in shown)
                     + (f" (+{len(cwds) - len(shown)} more)" if len(cwds) > len(shown) else ""))
    if opts.source_note:
        lines.append(f"- Source: `{opts.source_note}`")
    lines += [
        "",
        "_Read this file first for orientation, then open the project files it names. "
        "It is a summary, not a transcript \u2014 verify anything load-bearing against the code._",
    ]
    return lines


def render_sessions(sessions: List[Session], max_rows: int = 25,
                    opts: Optional["Options"] = None) -> List[str]:
    lang = opts.lang if opts else "auto"
    lines = [f"## {bilingual_title('Sessions', sessions, lang)}", "",
             "| # | when | cwd | branch | turns | tool calls | opened with |",
             "|---|------|-----|--------|-------|-----------|-------------|"]
    ordered = list(reversed(sessions))          # newest first
    shown = ordered[:max_rows]
    base = len(sessions) - len(shown)
    for offset, s in enumerate(shown):
        when = f"{s.start:%Y-%m-%d %H:%M}" if s.start else "?"
        idx = base + len(shown) - offset
        lines.append(
            f"| {idx} | {when} | `{session_where(s)}` | {s.branch or '-'} | "
            f"{len(s.turns)} | {len(s.tool_calls())} | {s.label()} |"
        )
    if base > 0:
        lines.append(f"| \u2026 | _{base} older session(s) not listed_ | | | | | |")
    lines.append("")
    return lines


def render_intent(sessions: List[Session], budget: Budget, opts: "Options") -> List[str]:
    lines: List[str] = []
    for s in sessions:
        prompts = s.prompts()
        if not prompts:
            continue
        when = f"{s.start:%Y-%m-%d %H:%M}" if s.start else "session"
        lines.append(f"### {when} \u00b7 `{session_where(s)}`")
        lines.append("")
        # keep the NEWEST prompts: on a long chat the first ones are the oldest
        # context, and dropping the tail is what makes a memory feel stale
        cap = max(1, opts.max_prompts_per_session)
        chosen = prompts[-cap:]
        omitted = len(prompts) - len(chosen)
        if omitted:
            lines.append(f"> _({omitted} earlier prompt(s) omitted; newest {cap} shown)_")
            lines.append(">")
        for p in chosen:
            lines.append(f"> {one_line(p, opts.max_line)}")
            lines.append(">")
        lines.append("")
    out = [f"## {bilingual_title('What was asked (verbatim user prompts)', sessions, opts.lang)}", ""]
    out += budget.take([ln for ln in lines if ln], bullet="")
    return out


def _claims(sessions: List[Session], rx: re.Pattern, opts: "Options",
            skip_prompts: bool = True) -> List[str]:
    found: List[str] = []
    for s in sessions:
        if skip_prompts:
            prompt_keys = {norm_key(p) for p in s.prompts()}
        else:
            prompt_keys = set()
        for turn in s.turns:
            if turn.role != "assistant" or not turn.text:
                continue
            for line in sentences(turn.text, opts.max_line):
                key = norm_key(line)
                if key in prompt_keys:
                    continue
                if RX_LOW_VALUE.match(line) or RX_HEADER_LINE.match(line):
                    continue
                if rx.search(line):
                    found.append(line)
    return found


def render_claims(title: str, hint: str, sessions: List[Session], rx: re.Pattern,
                  budget: Budget, opts: "Options", limit: int,
                  seen: Optional[set] = None) -> List[str]:
    candidates = _claims(sessions, rx, opts)
    if seen is not None:
        fresh = []
        for line in candidates:
            key = norm_key(line)
            if key in seen:
                continue
            seen.add(key)
            fresh.append(line)
        candidates = fresh
    kept, _ = dedupe_recent(candidates, limit)
    if not kept:
        return []
    display_title = bilingual_title(title, sessions, opts.lang)
    out = [f"## {display_title}", "", f"_{hint}_", ""]
    out += budget.take(kept)
    out.append("")
    return out


def render_identifiers(sessions: List[Session], budget: Budget, opts: "Options",
                      skip_paths: Optional[set] = None) -> List[str]:
    skip_paths = skip_paths or set()
    paths: Dict[str, int] = {}
    hexes: Dict[str, int] = {}
    versions: Dict[str, int] = {}
    sizes: Dict[str, int] = {}

    def bump(store: Dict[str, int], key: str) -> None:
        store[key] = store.get(key, 0) + 1

    def already_listed(path: str) -> bool:
        """Tool paths are absolute, prose paths are usually relative: suffix match."""
        norm = path.replace("\\", "/").lower()
        return any(opened.endswith(norm) or norm.endswith(opened) for opened in skip_paths)

    for s in sessions:
        for turn in s.turns:
            if not turn.text:
                continue
            for p in RX_PATH.findall(turn.text):
                clean = p.strip().strip(".,;:)\"'")
                if not already_listed(clean):
                    bump(paths, clean)
            for h in RX_HEX.findall(turn.text):
                bump(hexes, h)
            for v in RX_VERSION.findall(turn.text):
                bump(versions, v)
            for z in RX_SIZE.findall(turn.text):
                bump(sizes, one_line(z, 40))
            if opts.include_addresses:
                for a in RX_ADDR.findall(turn.text):
                    if not RX_HEX.fullmatch(a):
                        bump(hexes, "0x" + a)

    def top(store: Dict[str, int], n: int, fmt=None) -> List[str]:
        items = sorted(store.items(), key=lambda kv: (-kv[1], kv[0]))[:n]
        return [fmt(k, c) if fmt else f"`{k}` \u00d7{c}" for k, c in items]

    out = [f"## {bilingual_title('Concrete identifiers (paths, values, versions)', sessions, opts.lang)}", ""]
    blocks = []
    if paths:
        blocks.append(("Paths touched", top(paths, opts.max_paths)))
    if hexes:
        blocks.append(("Hex / addresses", top(hexes, opts.max_paths)))
    if versions:
        blocks.append(("Versions", top(versions, min(12, opts.max_paths))))
    if sizes:
        blocks.append(("Measured values", top(sizes, min(12, opts.max_paths))))
    if not blocks:
        return []
    for name, items in blocks:
        if not items or budget.left < 200:
            continue
        budget.line(f"**{name}**")
        out.append(f"**{name}**")
        out.append("")
        out += budget.take(items)
        out.append("")
    return out


@dataclass
class FileFacts:
    edits: Dict[str, int] = field(default_factory=dict)
    reads: Dict[str, int] = field(default_factory=dict)
    cmds: List[str] = field(default_factory=list)
    agents: Dict[str, int] = field(default_factory=dict)
    unknown: Dict[str, int] = field(default_factory=dict)

    def opened(self) -> set:
        return {k.replace("\\", "/").lower() for k in list(self.edits) + list(self.reads)}


def collect_files(sessions: List[Session]) -> FileFacts:
    edit_tools = {"Edit", "MultiEdit", "Write", "NotebookEdit", "NotebookEditCell"}
    read_tools = {"Read", "Glob", "Grep"}
    facts = FileFacts()

    def bump(store: Dict[str, int], key: str) -> None:
        store[key] = store.get(key, 0) + 1

    for s in sessions:
        for name, brief, side in s.tool_calls():
            key = brief.split("=", 1)[-1][:160]
            if name in edit_tools:
                bump(facts.edits, key)
            elif name in read_tools:
                bump(facts.reads, key)
            elif name == "Bash":
                if len(facts.cmds) < 100:
                    facts.cmds.append(one_line(key, 150))
            elif name == "Task":
                bump(facts.agents, brief[:100])
            else:
                bump(facts.unknown, name)
    # a file that was edited was obviously read too: do not list it twice
    facts.reads = {k: v for k, v in facts.reads.items() if k not in facts.edits}
    return facts


def render_files(facts: FileFacts, budget: Budget, opts: "Options",
                 sessions: Optional[List[Session]] = None) -> List[str]:
    out = []
    def _title(en: str) -> str:
        return bilingual_title(en, sessions, opts.lang) if sessions else en
    if facts.edits:
        ranked = sorted(facts.edits.items(), key=lambda kv: (-kv[1], kv[0]))[: opts.max_paths]
        out += [f"## {_title('Files changed')}", ""]
        out += budget.take([f"`{k}` \u00d7{v}" for k, v in ranked])
        out.append("")
    if facts.reads:
        ranked = sorted(facts.reads.items(), key=lambda kv: (-kv[1], kv[0]))[: opts.max_paths]
        out += [f"## {_title('Files read / searched (top)')}", ""]
        out += budget.take([f"`{k}` \u00d7{v}" for k, v in ranked])
        out.append("")
    if facts.cmds:
        kept, _ = dedupe(facts.cmds, opts.max_cmds)
        out += [f"## {_title('Commands run')}", ""]
        out += budget.take(kept)
        out.append("")
    if facts.agents:
        out += [f"## {_title('Subagents used')}", ""]
        out += budget.take([f"`{k}` \u00d7{v}" for k, v in
                            sorted(facts.agents.items(), key=lambda kv: -kv[1])[:10]])
        out.append("")
    if facts.unknown:
        out += [f"## {_title('Other tools used')}", ""]
        out += budget.take([f"`{k}` \u00d7{v}" for k, v in
                            sorted(facts.unknown.items(), key=lambda kv: -kv[1])[:12]])
    return out


def render_errors(sessions: List[Session], budget: Budget, opts: "Options",
                  seen: Optional[set] = None) -> List[str]:
    items: List[str] = []
    # checklist bullets belong in the plan section; fuzzy problem words in a
    # plan item ("diff the offsets", "report the mismatch") are not real problems
    bullet_keys: set = set()
    for s in sessions:
        for turn in s.turns:
            if turn.role != "assistant" or not turn.text:
                continue
            for ln in turn.text.splitlines():
                if RX_BULLET.match(ln):
                    bullet_keys.add(norm_key(one_line(RX_BULLET.sub("", ln), opts.max_line)))
    for s in sessions:
        for err in s.error_lines():
            items.append(one_line(err, opts.max_line))
        for turn in s.turns:
            if turn.role != "assistant" or not turn.text:
                continue
            for line in sentences(turn.text, opts.max_line):
                if norm_key(line) in bullet_keys:
                    continue
                if RX_PROBLEM.search(line) and not RX_LOW_VALUE.match(line):
                    items.append(line)
    if seen is not None:
        items = [ln for ln in items if not _claim(ln, seen)]
    kept, _ = dedupe_recent(items, opts.max_items)
    if not kept:
        return []
    out = [f"## {bilingual_title('Problems, failures and root causes seen', sessions, opts.lang)}", "",
           "_Re-check these before repeating the same work \u2014 they may already be fixed._", ""]
    out += budget.take(kept)
    out.append("")
    return out


def _claim(line: str, seen: set) -> bool:
    """Register a line as already rendered; returns True when it is a duplicate."""
    key = norm_key(line)
    if key in seen:
        return True
    seen.add(key)
    return False


def render_next_steps(sessions: List[Session], budget: Budget, opts: "Options",
                      seen: Optional[set] = None) -> List[str]:
    """Explicit TODOs + the newest plan-shaped assistant blocks (recency wins)."""
    todos: List[str] = []
    plans: List[str] = []
    for s in sessions:
        for turn in s.turns:
            if turn.role != "assistant" or not turn.text:
                continue
            raw_lines = [ln.rstrip() for ln in turn.text.splitlines() if ln.strip()]
            is_list = sum(1 for ln in raw_lines if RX_BULLET.match(ln)) >= 2
            for line in sentences(turn.text, opts.max_line):
                if RX_OPEN.search(line) and not line.endswith("?"):
                    if RX_HEADER_LINE.match(line) or RX_LOW_VALUE.match(line):
                        continue
                    todos.append(line)
            if is_list:
                for ln in raw_lines:
                    if RX_BULLET.match(ln):
                        plans.append(one_line(RX_BULLET.sub("", ln), opts.max_line))
    if seen is not None:
        todos = [ln for ln in todos if not _claim(ln, seen)]
    kept_todos, _ = dedupe_recent(todos, opts.max_items // 2)
    # newest plan bullets first; never repeat a bullet already shown as a TODO
    plan_seen: set = {norm_key(t) for t in kept_todos}
    fresh_plans = [ln for ln in plans if not _claim(ln, plan_seen)]
    kept_plans, _ = dedupe_recent(list(reversed(fresh_plans)), opts.max_items)
    out = []
    if kept_todos:
        out += [f"## {bilingual_title('Open items / TODOs', sessions, opts.lang)}", ""]
        out += budget.take(kept_todos)
        out.append("")
    if kept_plans:
        out += [f"## {bilingual_title('Most recent plan or checklist', sessions, opts.lang)}", ""]
        out += budget.take(kept_plans)
        out.append("")
    return out


def render_prefs(sessions: List[Session], budget: Budget, opts: "Options") -> List[str]:
    kept, _ = dedupe(_claims(sessions, RX_PREF, opts), opts.max_items // 2)
    if not kept:
        return []
    out = [f"## {bilingual_title('Stated preferences and conventions', sessions, opts.lang)}", "",
           "_Reuse these instead of re-deciding style or approach._", ""]
    out += budget.take(kept)
    out.append("")
    return out


def render_summaries(sessions: List[Session]) -> List[str]:
    items = [s for s in sessions if s.summaries]
    if not items:
        return []
    out = ["## Compactor summaries", ""]
    for s in items:
        for summary in s.summaries[-3:]:
            out.append(f"- _({session_where(s)})_ {one_line(summary, 300)}")
    out.append("")
    return out


def clip_middle(text: str, cap: int) -> Tuple[str, bool]:
    """Shorten to `cap`, keeping the start AND the end.

    Conclusions live at the end of a reply, so clipping only the front throws
    away exactly the part a memory is supposed to preserve.
    """
    if len(text) <= cap:
        return text, False
    head = int(cap * 0.55)
    tail = cap - head
    omitted = len(text) - cap
    return (text[:head].rstrip()
            + f"\n\n\u2026 [{omitted} chars omitted] \u2026\n\n"
            + text[-tail:].lstrip()), True


def render_recent_exchange(sessions: List[Session], budget: Budget,
                           opts: "Options") -> List[str]:
    """Always carry the end of the conversation into the summary.

    Without this, a summary of a long chat silently omits whatever the last few
    turns concluded, because only sentences matching a claim pattern survive.
    """
    cap = max(400, opts.max_line * 6)
    picked: List[Tuple[str, str, str]] = []
    for s in sessions:
        body_of = lambda t: (t.text or "").strip()      # noqa: E731 - local shorthand
        user_idx = [i for i, t in enumerate(s.turns)
                    if t.role == "user" and body_of(t)]
        if user_idx:
            start = user_idx[-1]
            tail_turns = ([s.turns[start]]
                          + [t for t in s.turns[start + 1:]
                             if t.role == "assistant" and body_of(t)])[-5:]
        else:
            replies = [t for t in s.turns
                       if t.role == "assistant" and body_of(t)][-1:]
            tail_turns = replies
        for turn in tail_turns:
            body = body_of(turn)
            if not body:
                continue
            clipped, trimmed = clip_middle(body, cap)
            who = "user" if turn.role == "user" else "assistant"
            label = "last question" if who == "user" else "reply since then"
            note = f" _({len(body)} chars, middle omitted)_" if trimmed else ""
            picked.append((session_where(s), f"**{who}** \u2014 {label}{note}", clipped))

    if not picked:
        return []
    out = [f"## {bilingual_title('Most recent exchange', sessions, opts.lang)}", "",
           "_The last question and everything answered since it, kept verbatim so the "
           "summary never stops short of what was actually concluded._", ""]
    for place, head, body in picked:
        out += budget.take([f"### {place}", "", head, "", body, ""], bullet="")
    return out


def build_memory(sessions: List[Session], opts: "Options") -> str:
    budget = Budget(opts.max_chars)
    seen: set = set()

    # header / tables are never trimmed, so charge them to the budget *before*
    # rendering the trimmed sections, otherwise a huge session table would blow
    # the --max-chars limit without ever squeezing anything out.
    out: List[str] = []
    out += render_header(sessions, opts)
    out.append("")
    out += render_sessions(sessions, opts=opts)
    out += render_summaries(sessions)
    budget.used += sum(len(line) + 1 for line in out)

    facts = collect_files(sessions)
    sections = [
        render_intent(sessions, budget, opts),
        render_recent_exchange(sessions, budget, opts),
        render_claims("Decisions taken", "What was settled and why (newest wins).",
                      sessions,
                      RX_DECISION, budget, opts, opts.max_items, seen),
        # preferences run before constraints: their keywords are more specific,
        # so the broad "always/never" patterns cannot swallow every convention
        render_claims("Preferences and conventions", "Style and approach choices.",
                      sessions, RX_PREF, budget, opts, opts.max_items, seen),
        render_claims("Constraints and must-nots", "Rules that later work has to respect.",
                      sessions, RX_FACT, budget, opts, opts.max_items, seen),
        render_files(facts, budget, opts, sessions),
        render_errors(sessions, budget, opts, seen),
        render_next_steps(sessions, budget, opts, seen),
        render_identifiers(sessions, budget, opts, facts.opened()),
    ]
    for sec in sections:
        if sec:
            out += sec

    if budget.trimmed:
        so_far = chr(10).join(out)
        out += ["---", "",
                f"_Trimmed {budget.trimmed} lower-priority line(s) to stay inside "
                f"--max-chars {opts.max_chars} (~{estimate_tokens(so_far)} tokens). "
                "Raise --max-chars, or re-run with --mode transcript for full detail._", ""]

    text = "\n".join(out).rstrip() + "\n"
    if opts.fmt == "claude-md":
        text = f"{BEGIN}\n{text}\n{END}\n"
    return text


def build_full_memory(sessions: List[Session], opts: "Options") -> str:
    """A memory file that keeps the whole conversation, not just the summary.

    The summary modes are lossy by design: assistant prose is filtered into a few
    short sections and everything else is dropped. This variant keeps every turn
    verbatim (tool calls summarised to one line each, tool output dropped) so
    nothing is lost, with the summary sections kept at the top for orientation.
    """
    budget = Budget(opts.max_chars)
    seen: set = set()

    out: List[str] = []
    out += render_header(sessions, opts)
    out.append("")

    facts = collect_files(sessions)
    static = render_sessions(sessions, opts=opts) + render_summaries(sessions)
    out += static
    budget.used += sum(len(line) + 1 for line in out)

    # orientation first, still capped, so the head of the file stays cheap to read
    sections = [
        render_claims("Decisions taken", "What was settled and why.", sessions,
                      RX_DECISION, budget, opts, opts.max_items, seen),
        render_claims("Preferences and conventions", "Style and approach choices.",
                      sessions, RX_PREF, budget, opts, opts.max_items, seen),
        render_claims("Constraints and must-nots", "Rules that later work has to respect.",
                      sessions, RX_FACT, budget, opts, opts.max_items, seen),
        render_files(facts, budget, opts, sessions),
        render_next_steps(sessions, budget, opts, seen),
    ]
    for sec in sections:
        if sec:
            out += sec

    # ---- the unabridged conversation
    turns = sum(len(s.turns) for s in sessions)
    words = 0
    convo: List[str] = [f"## {bilingual_title('Full conversation (nothing summarised away)', sessions, opts.lang)}", "",
                        f"_{turns} turn(s) across {len(sessions)} session(s), verbatim. "
                        "Tool calls are listed one line each; tool output is omitted "
                        "because it is mostly file dumps._", ""]
    for s in sessions:
        span = ""
        if s.start:
            span = f" - {s.start:%Y-%m-%d %H:%M}"
        convo.append(f"### {session_where(s)}{span}")
        convo.append("")
        if not s.turns:
            convo.append("_no turns in this session_")
            convo.append("")
            continue
        for turn in s.turns:
            who = "user" if turn.role == "user" else "assistant"
            if turn.role not in ("user", "assistant"):
                who = turn.role
            when = f" - {turn.ts:%H:%M}" if turn.ts else ""
            tag = " _(subagent)_" if turn.sidechain else ""
            convo.append(f"**{who}**{when}{tag}")
            convo.append("")
            body = (turn.text or "").strip()
            if body:
                words += len(body.split())
                convo.append(body)
                convo.append("")
            for name, brief in turn.tools:
                convo.append(f"- `{name}` {brief}")
            for err in turn.errors:
                convo.append(f"- **error** {err}")
            if not body and not turn.tools and not turn.errors:
                convo.append("_(no text)_")
                convo.append("")
    out += convo
    out.append("---")
    out.append("")
    full_text = chr(10).join(out)
    out.append(f"_End of full context: {words} words of assistant+user text, "
               f"{len(full_text)} chars total "
               f"(~{estimate_tokens(full_text)} tokens)._")
    out.append("")

    text = "\n".join(out).rstrip() + "\n"
    if opts.fmt == "claude-md":
        text = f"{BEGIN}\n{text}\n{END}\n"
    return text


def build_transcript(sessions: List[Session], opts: "Options") -> str:
    out = [f"# Claude Code transcript export", "",
           f"_{len(sessions)} session(s), generated {iso_now()}_", ""]
    for i, s in enumerate(sessions, 1):
        out += ["---", "", f"## Session {i} \u00b7 `{s.path.name}`", ""]
        meta = [f"- cwd: `{s.cwd or session_where(s)}`"]
        if s.branch:
            meta.append(f"- branch: `{s.branch}`")
        if s.model:
            meta.append(f"- model: `{s.model}`")
        if s.start:
            meta.append(f"- span: {s.start:%Y-%m-%d %H:%M} \u2192 "
                        f"{s.end:%H:%M} ({fmt_dur(s.duration)})")
        meta.append(f"- turns: {len(s.turns)} \u00b7 tool calls: {len(s.tool_calls())} "
                    f"\u00b7 records skipped: {s.skipped}")
        out += meta + [""]
        if s.truncated_file:
            out += ["> _Only the last part of this file was read (--max-file-bytes)._", ""]
        for t in s.turns:
            if t.sidechain:
                t.tools = [(n, b) for n, b in t.tools]
            if t.text:
                out.append(f"### {t.role}" + (" _(subagent)_" if t.sidechain else "")
                           + (f" \u00b7 {t.ts:%H:%M}" if t.ts else ""))
                out.append("")
                out.append(t.text.strip())
                out.append("")
            for name, brief in t.tools:
                out.append(f"- `{name}` {brief}")
            for err in t.errors:
                out.append(f"- **error** {err}")
    return "\n".join(out).rstrip() + "\n"


def build_json(sessions: List[Session]) -> str:
    payload = []
    for s in sessions:
        payload.append({
            "id": s.sid,
            "file": str(s.path),
            "cwd": s.cwd,
            "branch": s.branch,
            "model": s.model,
            "claude_code_version": s.version,
            "start": s.start.isoformat() if s.start else None,
            "end": s.end.isoformat() if s.end else None,
            "duration_seconds": round(s.duration, 1),
            "turns": len(s.turns),
            "tool_calls": len(s.tool_calls()),
            "records_skipped": s.skipped,
            "summaries": s.summaries,
            "prompts": s.prompts(),
            "messages": [{"role": t.role, "ts": t.ts.isoformat() if t.ts else None,
                          "sidechain": t.sidechain, "text": t.text,
                          "tools": [{"name": n, "input": b} for n, b in t.tools],
                          "errors": t.errors} for t in s.turns],
        })
    return json.dumps({"generated": iso_now(), "tool": f"claude_memory.py {__version__}",
                       "sessions": payload}, indent=2, ensure_ascii=False) + "\n"


# ----------------------------------------------------------------------------- io


def atomic_write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(text, encoding="utf-8", newline="\n")
    tmp.replace(path)


def archive_history(out_path: Path, previous: Optional[str], keep: int) -> None:
    if keep <= 0:
        return
    hist_dir = out_path.parent / (out_path.stem + ".history")
    hist_dir.mkdir(parents=True, exist_ok=True)
    prev_files = sorted(hist_dir.glob("*.md"))
    while len(prev_files) >= keep:
        oldest = prev_files.pop(0)
        try:
            oldest.unlink()
        except OSError:
            pass
    stamp = now_utc().strftime("%Y%m%d-%H%M%S")
    n = 1
    while (hist_dir / f"{stamp}-{n}.md").exists():
        n += 1
    atomic_write(hist_dir / f"{stamp}-{n}.md", previous or "")


def render_list(sessions: List[Session]) -> str:
    if not sessions:
        return ("No Claude Code transcripts found.\n"
                "Expected .jsonl files under ~/.claude/projects/<slug>/.\n"
                "Start a Claude Code session in a project directory first.\n")
    rows = ["| # | file | cwd | branch | start | turns | tools | size |",
            "|---|------|-----|--------|-------|-------|-------|------|"]
    for i, s in enumerate(sessions, 1):
        try:
            size = s.path.stat().st_size
        except OSError:
            size = 0
        when = f"{s.start:%Y-%m-%d %H:%M}" if s.start else "?"
        rows.append(
            f"| {i} | `{s.path.name}` | `{session_where(s)}` | {s.branch or '-'} | "
            f"{when} | {len(s.turns)} | {len(s.tool_calls())} | {size / 1024:.0f} KB |"
        )
    total = sum(s.path.stat().st_size for s in sessions if s.path.exists())
    rows += ["", f"_{len(sessions)} session file(s), {total / 1024 / 1024:.2f} MB total._"]
    return "\n".join(rows) + "\n"


# ----------------------------------------------------------------------------- options


@dataclass
class Options:
    root: Path = DEFAULT_ROOT
    projects_dir: Optional[Path] = None
    out: Optional[Path] = None
    print_stdout: bool = False
    mode: str = "memory"
    fmt: str = "plain"
    sessions: int = 0
    since: Optional[_dt.datetime] = None
    fast: bool = False
    cwd: str = ""
    here: bool = False
    branch: str = ""
    max_chars: int = 14000
    max_items: int = 14
    max_paths: int = 18
    max_cmds: int = 10
    max_line: int = 260
    max_prompts_per_session: int = 12
    max_file_bytes: int = 12 * 1024 * 1024
    sidechains: str = "keep"
    include_thinking: bool = False
    include_addresses: bool = True
    history: int = 5
    quiet: bool = False
    list_only: bool = False
    source_note: str = ""
    ascii_only: bool = False
    lang: str = "auto"
    share: str = ""
    share_file: Optional[Path] = None
    share_save: Optional[Path] = None
    only_share: bool = False


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="claude_memory.py",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description="Summarise Claude Code sessions into a rolling markdown memory file.",
        epilog="examples:\n"
               "  %(prog)s --out memory/MEMORY.md --sessions 5 --since 7d\n"
               "  %(prog)s --format claude-md --print\n"
               "  %(prog)s --list\n",
    )
    p.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    p.add_argument("--root", type=Path, default=DEFAULT_ROOT,
                   help="Claude config dir (default: %(default)s)")
    p.add_argument("--projects-dir", type=Path, default=None,
                   help="explicit transcript dir, overrides <root>/projects")
    p.add_argument("--out", type=Path, default=None, help="write the file here")
    p.add_argument("--print", dest="print_stdout", action="store_true",
                   help="also write to stdout")
    p.add_argument("--mode", choices=("memory", "full", "transcript", "json"),
                   default="memory",
                   help="memory = summary (default); full = summary plus the entire "
                        "conversation; transcript = raw readable chat; json = structured")
    p.add_argument("--format", dest="fmt", choices=("plain", "claude-md"), default="plain",
                   help="claude-md wraps output in CLAUDE.md paste markers")
    p.add_argument("--sessions", type=int, default=0, help="only the newest N sessions (0=all)")
    p.add_argument("--since", type=parse_since, default=None, metavar="WHEN",
                   help="7d, 12h, 30m, YYYY-MM-DD or ISO timestamp")
    p.add_argument("--fast", action="store_true",
                   help="APPROXIMATE: skip transcripts whose file mtime predates "
                        "--before deciding, instead of reading every one. Much faster "
                        "on big histories, but can miss sessions whose timestamps live "
                        "inside the record and whose file was moved/restored. Needs "
                        "--since to do anything")
    p.add_argument("--cwd", default="", help="only sessions whose path/cwd contains this")
    p.add_argument("--here", action="store_true",
                   help="only sessions belonging to the current directory (default cwd filter)")
    p.add_argument("--branch", default="", help="only sessions on this git branch")
    p.add_argument("--max-chars", type=int, default=14000,
                   help="global body budget in chars (default: %(default)s)")
    p.add_argument("--max-items", type=int, default=14, help="max lines per claim section")
    p.add_argument("--max-paths", type=int, default=18, help="max paths/identifiers listed")
    p.add_argument("--max-cmds", type=int, default=10, help="max shell commands listed")
    p.add_argument("--max-line", type=int, default=260, help="max chars per quoted line")
    p.add_argument("--max-prompts", dest="max_prompts_per_session", type=int, default=12,
                   help="max user prompts quoted per session")
    p.add_argument("--max-file-bytes", type=int, default=12 * 1024 * 1024,
                   help="per-transcript read cap; tail is kept (default: %(default)s)")
    p.add_argument("--sidechains", choices=("keep", "drop"), default="keep",
                   help="include subagent turns (default: keep)")
    p.add_argument("--include-thinking", action="store_true", help="include thinking blocks")
    p.add_argument("--no-addresses", dest="include_addresses", action="store_false",
                   help="skip bare hex/address harvesting")
    p.add_argument("--history", type=int, default=5,
                   help="keep N previous versions in <out>.history (default: %(default)s)")
    p.add_argument("--source-note", default="", help="free-text provenance line for the header")
    p.add_argument("--share", metavar="URL", default="",
                   help="also import a claude.ai share link "
                        "(https://claude.ai/share/<uuid>, or a bare uuid)")
    p.add_argument("--share-file", type=Path, default=None, metavar="PATH",
                   help="import a saved share payload (.json) instead of fetching")
    p.add_argument("--share-save", type=Path, default=None, metavar="PATH",
                   help="cache the fetched share payload here for offline re-runs")
    p.add_argument("--only-share", action="store_true",
                   help="ignore local sessions, use only the shared link")
    p.add_argument("--ascii", dest="ascii_only", action="store_true",
                   help="downgrade box/arrow glyphs to ASCII (legacy consoles)")
    p.add_argument("--lang", choices=("auto", "en", "ar", "both"), default="auto",
                   help="section header language: auto-detect (default), English, "
                        "Arabic, or bilingual")
    p.add_argument("--list", dest="list_only", action="store_true",
                   help="list discovered sessions and exit")
    p.add_argument("--quiet", action="store_true", help="suppress the stderr summary")
    return p


def main(argv: Optional[Sequence[str]] = None) -> int:
    global QUIET
    args = build_parser().parse_args(argv)
    configure_stdio()
    QUIET = args.quiet
    opts = Options(
        root=args.root,
        projects_dir=args.projects_dir,
        out=args.out,
        print_stdout=args.print_stdout or args.out is None,
        mode=args.mode,
        fmt=args.fmt,
        sessions=args.sessions,
        since=args.since,
        fast=args.fast,
        cwd=args.cwd,
        here=args.here,
        branch=args.branch,
        max_chars=args.max_chars,
        max_items=args.max_items,
        max_paths=args.max_paths,
        max_cmds=args.max_cmds,
        max_line=args.max_line,
        max_prompts_per_session=args.max_prompts_per_session,
        max_file_bytes=args.max_file_bytes,
        sidechains=args.sidechains,
        include_thinking=args.include_thinking,
        include_addresses=args.include_addresses,
        history=args.history,
        quiet=args.quiet,
        list_only=args.list_only,
        source_note=args.source_note,
        ascii_only=args.ascii_only,
        lang=args.lang,
        share=args.share,
        share_file=args.share_file,
        share_save=args.share_save,
        only_share=args.only_share,
    )

    # shared links are independent of the local transcript dir: they must work
    # even when ~/.claude/projects does not exist yet
    share_sessions: List[Session] = []
    if opts.share or opts.share_file:
        try:
            if opts.share_file:
                share_sessions.append(load_share_file(opts.share_file,
                                                     opts.include_thinking))
                if not QUIET:
                    eprint(f"[claude-memory] imported share payload from {opts.share_file}")
            if opts.share:
                payload = fetch_share(opts.share)
                share_sessions.append(
                    parse_share_payload(payload, include_thinking=opts.include_thinking))
                if not QUIET:
                    eprint(f"[claude-memory] imported share link "
                           f"({len(payload.get('chat_messages') or [])} messages)")
                if not opts.source_note:
                    opts.source_note = f"claude.ai share link ({share_uuid(opts.share)})"
                if opts.share_save:
                    atomic_write(opts.share_save,
                                 json.dumps(payload, indent=1, ensure_ascii=False) + "\n")
                    if not QUIET:
                        eprint(f"[claude-memory] cached payload -> {opts.share_save}")
        except ShareError as e:
            eprint(f"[claude-memory] {e}")
            return 3

    base = opts.projects_dir or (opts.root / "projects")
    have_local = base.exists()
    if not have_local and not share_sessions:
        eprint(f"[claude-memory] no transcript dir at {base}")
        eprint("[claude-memory] run a Claude Code session in a project, pass "
               "--projects-dir, or import a share link with --share.")
        return 2

    files = discover(opts.root, opts.projects_dir) if have_local else []
    if opts.fast and opts.since is not None and files:
        # Opt-in approximation: a transcript written before the cutoff cannot hold
        # records newer than the cutoff, so its mtime is enough to skip it without
        # reading 12MB of JSON. select() still decides the final membership.
        cutoff_ts = opts.since.timestamp()
        kept = []
        for p in files:
            try:
                if p.stat().st_mtime >= cutoff_ts:
                    kept.append(p)
            except OSError:
                kept.append(p)
        skipped = len(files) - len(kept)
        if not QUIET and skipped:
            eprint(f"[claude-memory] --fast: skipped {skipped} transcript(s) older than "
                   f"{opts.since:%Y-%m-%d} by file mtime (approximate)")
        files = kept
    parsed = [parse_session(p, opts) for p in files]
    parsed = [s for s in parsed if s.turns or s.summaries]

    if opts.list_only:
        sys.stdout.write(render_list(parsed + share_sessions))
        return 0

    chosen: List[Session] = [] if opts.only_share else select(parsed, opts)
    chosen = chosen + share_sessions
    if not chosen:
        eprint(f"[claude-memory] no sessions matched (searched {len(parsed)} of {len(files)}).")
        eprint("[claude-memory] try widening --since / --cwd, or drop --sessions.")
        return 1
    # oldest first, so the sessions table and recency-based sections agree
    chosen.sort(key=lambda s: (s.start or _dt.datetime.min.replace(
        tzinfo=_dt.timezone.utc)))

    if opts.mode == "transcript":
        text = build_transcript(chosen, opts)
    elif opts.mode == "full":
        text = build_full_memory(chosen, opts)
    elif opts.mode == "json":
        text = build_json(chosen)
    else:
        text = build_memory(chosen, opts)

    if opts.ascii_only:
        text = to_ascii(text)

    if opts.out:
        previous = None
        if opts.mode == "memory" and opts.history > 0 and opts.out.exists():
            try:
                previous = opts.out.read_text(encoding="utf-8", errors="replace")
            except OSError:
                previous = None
            archive_history(opts.out, previous, opts.history)
        atomic_write(opts.out, text)
        if not opts.quiet:
            eprint(f"[claude-memory] wrote {opts.out} "
                   f"({len(text)} chars, {len(chosen)} session(s), "
                   f"{len(text.splitlines())} lines)")

    if opts.print_stdout:
        sys.stdout.write(text)
        if not text.endswith("\n"):
            sys.stdout.write("\n")

    if not opts.quiet and opts.out:
        if opts.mode == "memory" and opts.fmt == "plain":
            eprint(f"[claude-memory] paste-ready block: re-run with "
                   f"--format claude-md, or use --print to copy it.")
        elif opts.mode == "memory":
            eprint("[claude-memory] file already carries CLAUDE.md paste markers; "
                   "append it to CLAUDE.md (it re-imports cleanly).")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        eprint("\n[claude-memory] interrupted")
        sys.exit(130)
