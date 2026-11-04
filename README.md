# claude-memory

Turns your Claude Code sessions — and any **claude.ai share link** — into one rolling
`MEMORY.md` you can hand back to Claude, so a new session starts with the context of
the last one.

- **Source:** `~/.claude/projects/<slug>/<uuid>.jsonl`, plus the public JSON behind a
  claude.ai share link
- **Output:** one signal-first markdown file, ~14k chars (~3.5k tokens) by default.
  Token counts are estimated per script, so Arabic, Cyrillic and CJK sessions
  are reported honestly instead of inheriting the English chars-per-token rate.
- **Deps:** none at runtime. Self-contained Windows binaries; the WebView2 Runtime
  (already shipped with Windows 10 1809+) is the only thing the preview pane needs.

This is the .NET 8 / WPF rewrite of the original Python 1.1.0 tool. The ported logic
is verified against frozen reference outputs (445 tests run offline); the desktop
window, the Markdown preview and the file association are new in this version.

---

## Start here: the app

`ClaudeMemory.exe` opens a normal desktop window titled **claude-memory** — no browser,
no `http://localhost`, nothing listening on a port. Pick a source, press **Generate
memory**, then **Copy** or **Save**.

- Press **Preview MD** to render headings, lists, tables and code in a WebView2 pane;
  the toggle becomes **Show source** for raw text.
- With nothing generated yet, **Preview MD** opens a native Open dialog for any
  `.md`/`.markdown` file; **Open MD...** does that at any time.
- Any action reports its result in the **status line next to the buttons** — including
  the "nothing to copy yet" case.
- The **Generate memory** button is pinned to the bottom of the left column, so it is
  always reachable.
- If the WebView2 Runtime is missing, the app says so, offers the install link, and the
  source view keeps working.

To get the binaries, either publish them yourself (needs the .NET 8 SDK):

```powershell
powershell -File tools\publish.ps1     # -> dist\ClaudeMemory, dist\ClaudeMemory.Cli
```

or build and run from source:

```powershell
dotnet run --project src\ClaudeMemory.App -c Debug      # the window
dotnet run --project src\ClaudeMemory.Cli -c Debug -- --list
```

### Open `.md` files with claude-memory

`ClaudeMemory.exe` can become the default app for `.md` and `.markdown` files:

1. In the **Windows integration** section of the left column, press
   **Set as default for Markdown**. The app registers itself for your user only
   (`HKCU\Software\Classes`, no admin rights) and then opens **Windows' own app
   picker** so you confirm the choice — Windows does not let an app change your
   default silently, and this app never tries.
2. After that, double-clicking a `.md` file (or a second `ClaudeMemory.exe "file.md"`
   launch while the window is open) opens it in the preview. A second launch hands the
   path to the running window and exits; there is only ever one window.
3. Press **Remove** to delete exactly the registry entries the app created. Whatever
   Windows already used for `.md` stays untouched; the app never writes the
   `UserChoice` key that Explorer owns.

A file path argument is validated the same way from the command line, a second launch
and the Open dialog: missing path, folder, or non-Markdown suffix explains itself in
the status line and the window still opens. Files are size-limited (4 MiB), checked
for NUL bytes, and read as UTF-8 with replacement before rendering.

## Using a claude.ai share link

Pick **Local + a shared link** (to mix in your local sessions) or **Only a shared
link**, paste the link into the box that appears under *Source*, and press Generate:

```
https://claude.ai/share/a683405f-3f2d-4050-8b77-1c4aef7ce550
```

Have the payload saved to disk instead? Leave the link box empty and press
**Browse...** next to *...or a saved payload file* to pick it without typing the path.
Either box on its own is enough, but one of them is required whenever a shared link is
part of the source.

How the fetch works:

- The share **page** is an empty JavaScript shell — the conversation is not in the
  HTML. The data comes from a public, unauthenticated endpoint:
  `https://claude.ai/api/chat_snapshots/<uuid>?rendering_mode=messages&render_all_tools=true`
- That endpoint rejects generic command-line user agents with `403`, so a browser UA
  is sent. It also **rate-limits**, so the fetch retries with a backoff and the
  payload is cached to your Documents folder (`claude-share-<id>.json`) for offline
  re-runs.
- If the fetch cannot get through, the interface tells you the manual route: open the
  share link, DevTools → Network → filter `chat_snapshots` → Copy response → save as
  `.json`, then press **Browse...** to pick that file (or type its path). Fully
  offline.

Imported chats become a normal session, so they merge with your local sessions or can
be used on their own. Branching conversations are handled: only the deepest active
branch is imported, not abandoned side branches.

Details worth knowing:

- claude.ai's own tool names are mapped to familiar ones (`bash_tool` → `Bash`,
  `view` → `Read`, `str_replace_editor` → `Edit`, `present_files` → `PresentFiles`).
- The sharer's name (`creator.full_name`) is **not** imported.
- Thinking blocks and `hidden_in_chat` blocks are skipped unless you ask for them.
- Only `claude.ai` share URLs are accepted, so the tool can't be pointed at other
  hosts.

## Command line

`ClaudeMemory.Cli.exe` runs the same pipeline headless (its flags mirror the original
tool one for one):

```powershell
# local sessions
ClaudeMemory.Cli.exe --list                                  # what exists
ClaudeMemory.Cli.exe --here --since 7d                       # this project, last week
ClaudeMemory.Cli.exe --here --since 30d --out MEMORY.md      # write it (5 history versions kept)
ClaudeMemory.Cli.exe --here --format claude-md --print       # paste-ready CLAUDE.md block

# shared links
ClaudeMemory.Cli.exe --share https://claude.ai/share/<uuid> --only-share
ClaudeMemory.Cli.exe --share-file saved-payload.json --only-share    # offline

# full detail instead of a summary
ClaudeMemory.Cli.exe --mode transcript --out chat.md
ClaudeMemory.Cli.exe --mode json --out chat.json
```

Then in a chat, either append the block to `CLAUDE.md` or paste it / `@`-mention
`MEMORY.md`.

## What lands in the file

### Output kinds

| Kind | What you get |
|---|---|
| **memory (summary)** — default | the sections below; compact, ~3.5k tokens |
| **memory (full context)** | the same summary **plus every turn verbatim** |
| **transcript** | the raw chat, no memory framing |
| **json** | structured data for other tools |

Full context exists because the summary is lossy on purpose: assistant prose is
filtered into a handful of short sections and anything that matches no pattern is
dropped. The full kind keeps every user and assistant turn (tool calls as one line
each, tool output still omitted because it is mostly file dumps) and prints its own
size, so you can judge whether it will fit in a CLAUDE.md import.

The summary sections, in both memory kinds:

| Section | Why it is there |
|---|---|
| Sessions | when/what/where, one row each, so Claude knows the shape of the history |
| Compactor summaries | Claude's own session summaries, highest signal per char |
| Most recent exchange | the last question plus every reply since it, verbatim |
| What was asked | your prompts, verbatim — the actual intent |
| Decisions taken | settled choices and their reasons, newest wins |
| Preferences and conventions | style/approach rules, so they stop being re-litigated |
| Constraints and must-nots | hard rules that were imposed |
| Files changed / read | what was actually touched, with counts |
| Commands run, Subagents used | reproducible side effects |
| Problems | failures and root causes, so they are not re-discovered |
| Open items / TODOs, Most recent plan | what is unfinished right now |
| Concrete identifiers | paths, hex/addresses, versions, measured values |

### How recency is handled

A summary that favours the *oldest* statements goes stale: it reports what was decided
hours ago and silently omits what was concluded since. Three rules keep the tail:

- **The end is always quoted.** *Most recent exchange* carries the last question and
  everything answered after it, word for word. Long replies keep their start *and*
  their end, because that is where conclusions live.
- **Newest wins.** When a section exceeds its cap, the most recent matches are kept,
  in chronological order, not the first ones found.
- **Prompt caps drop the oldest.** If a chat has more prompts than `--max-prompts`,
  the earliest are omitted and the count is stated.

What a summary still cannot hold is mid-conversation prose that matches no pattern.
Use **memory (full context)** when that matters.

Selection is heuristic (regex over assistant prose + tool-call records), not an LLM
summary. Consequences worth knowing:

- Only the assistant's own statements become claims. Anything you said but Claude
  never echoed is only visible under *What was asked*.
- `thinking` blocks are excluded by default (`--include-thinking` to include).
- Non-error `tool_result` bodies (file dumps, command output) are dropped as noise.
- One sentence is split into sentences before categorising, so a line holding both a
  rule and a convention appears in both sections instead of being lost.
- Everything is deduped and capped; a trim note tells you when lines were dropped.
  Raise `--max-chars` or use `--mode transcript` when you need the detail.

## Useful flags

| Flag | Effect |
|---|---|
| `--here` | only sessions for the current directory (prevents cross-project mixing) |
| `--cwd TEXT` | only sessions whose path/cwd contains TEXT |
| `--since 7d` | `30m` / `12h` / `7d` / `1w` / `2026-10-01` / ISO timestamp |
| `--sessions 5` | only the newest N sessions |
| `--share URL` / `--share-file PATH` | import a claude.ai share link, or a saved payload |
| `--only-share` | ignore local sessions |
| `--max-chars N` | global budget for the trimmed sections (default 14000, floor 200) |
| `--max-items N`, `--max-paths N`, `--max-cmds N` | per-section caps |
| `--sidechains drop` | ignore subagent turns |
| `--no-addresses` | skip bare hex/address harvesting |
| `--history 0` | do not archive previous versions |
| `--ascii` | downgrade arrow/box glyphs for legacy consoles (letters of any script are kept) |
| `--fast` | **approximate**: skip transcripts whose file mtime predates `--since` instead of reading every one. Opt-in, needs `--since`, and can miss sessions whose timestamps live inside the record while the file was moved or restored |
| `--lang` | `auto` (default), `en`, `ar`, or `both` — section header language |
| `--root PATH` / `--projects-dir PATH` | point at a non-default transcript location |

## The CLAUDE.md loop

```markdown
<!-- claude-memory:begin -->
... generated block ...
<!-- claude-memory:end -->
```

Those markers are the whole contract: regenerate, then replace the text *between* the
markers. The app's **Copy for CLAUDE.md** button does this wrapping for you.

To let Claude Code do it for you, copy `claude-slash-command-memory.md` to
`.claude/commands/memory.md` — `/memory` then regenerates the block with
`ClaudeMemory.Cli.exe`, patches it into `CLAUDE.md`, and reports what carried over,
what was superseded, and what needs checking against the code.

## Safety

The tool only ever writes the file you choose (plus its `.history/` folder) and the
share cache in Documents. The preview additionally keeps a WebView2 profile under
`%LOCALAPPDATA%\claude-memory\webview2`, which holds no transcript content. It never
modifies transcripts, never reads `server.cfg`/`.env`/credentials, and only ever
contacts `claude.ai`.

The preview renders with raw HTML disabled, runs no scripts, and loads links only for
`http`, `https`, `mailto` and in-document anchors — `javascript:`, `data:` and friends
become plain text. External links open in your default browser after a scheme check.

Your transcripts contain whatever you pasted into them, and so does `MEMORY.md`. If
your sessions contain secrets or customer data, keep `MEMORY.md` and `MEMORY.history/`
out of git (`.gitignore`) — the tool does not do that for you.

## Tests

```powershell
dotnet test src\ClaudeMemory.sln -c Release          # 445 tests, offline, no window needed
dotnet build src\ClaudeMemory.sln -c Release -warnaserror
```

What they cover:

- **Core** (209) — JSONL parsing, malformed-line tolerance, section selection,
  budget, share import (URL validation, retry/backoff, branch choice, block mapping)
- **App** (138) — view models, the Markdown pipeline (XSS corpus, scheme allowlist,
  CSP, no scripts), the registry contract (per-user HKCU only — `Software\Classes`
  plus the `RegisteredApplications` capability entry; `UserChoice` never touched),
  single-instance hand-off on a loopback pipe, and command-line routing
- **Cli** (98) — golden parity with the reference outputs (byte-identical after the
  four documented normalisations), stdio contract, process-level help/version checks

## Requirements

- Windows 10 1809+ (x64)
- WebView2 Runtime for the preview pane (pre-installed on up-to-date Windows; the app
  tells you the install link if it is missing)
- No Python, no admin rights, no other runtime
