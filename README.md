# claude-memory

Turns your Claude Code sessions â€” and any **claude.ai share link** â€” into one rolling
`MEMORY.md` you can hand back to Claude, so a new session starts with the context of
the last one.

- **Source:** `~/.claude/projects/<slug>/<uuid>.jsonl`, plus the public JSON behind a
  claude.ai share link
- **Output:** one signal-first markdown file, ~14k chars (~3.5k tokens) by default.
  Token counts are estimated per script, so Arabic, Cyrillic and CJK sessions
  are reported honestly instead of inheriting the English chars-per-token rate.
- **Deps:** none. Python 3.9+ stdlib only, fully offline for local sessions.

---

## Start here: the app

```
setup-webview.cmd     <- run once (only if you want the webview window)
start-app.cmd         <- every time after that: opens the desktop window
```

`start-app.cmd` opens a normal desktop window titled **claude-memory** â€” no browser, no
`http://localhost`, nothing listening on a port. Pick a source, press **Generate
memory**, then **Copy** or **Save**. Press **Preview MD** to render headings, lists,
tables, and code. With nothing generated yet, **Preview MD** opens a native Open dialog
for any `.md`/`.markdown` file and renders it GitHub-style; **Open MD...** does that at
any time. Web views toggle generated output back to raw text with **Show source** (a
file preview closes with **Close preview**); the tkinter fallback opens a separate
preview window. JSON output stays in source view.

It also shows **no console window at all**: the launcher runs `pythonw.exe` detached and
exits immediately. Because a hidden console would also swallow errors, the app routes
user-facing messages to a native dialog when there is no console, and any startup crash
pops a dialog and writes `claude-memory-error.log` next to the app. To watch it run with
output: `py app.py --debug` (or `--tk --debug`).

Three front ends, same logic behind all of them:

| Front end | How to run | Needs | Local host? |
|---|---|---|---|
| **Webview window** (default) | `start-app.cmd` | `pywebview` (one-time setup) | **no** |
| **Plain window** | `py app.py --tk` | nothing, stdlib tkinter | **no** |
| **Browser page** | `py app.py --browser` or `start-gui.cmd` | nothing | yes, 127.0.0.1 only |

If `pywebview` is missing, `app.py` says so and falls back to the tkinter window, so
there is always something that works. Everything is computed by `claude_memory.py`;
`gui.py` holds the shared page and request handlers, `app.py` is just the window.

### How "no local host" is achieved

The window is not a browser tab pointed at a server. `app.py` writes the same HTML page
to a temp file, loads it over `file://`, and talks to Python through the pywebview
bridge (`window.pywebview.api`). `webview.start(http_server=False)` is passed
explicitly, so nothing binds a port even if a future pywebview changes its default.
Verified by opening the window and asserting no listener on the test port.

Two details that cost real debugging time, both now covered by tests:

- pywebview injects its bridge *after* the document starts parsing, so the page's
  first `scan()` used to run too early and fail silently. Both transports now expose
  `ready()`, and the first scan waits for it.
- The injected transport must be a bare object literal â€” injecting
  `const HTTP_TRANSPORT = {...}` produced `const TRANSPORT = const ...`, a syntax error
  that blanked the page. `gui.page_html()` assembles it and tests pin the shape.

A webview has no download manager, so **Download** writes into `~/Downloads` with a
non-clobbering name. **Copy** falls back to the native clipboard, which webviews
usually block.

### Layout behaviour worth knowing

- The **Generate memory** button is pinned to the bottom of the left column, so it is
  always reachable and clicking it can never scroll the options above it out of view.
- Any action reports its result in the **toolbar next to the buttons** â€” including the
  "nothing to copy yet" case. Feedback used to sit at the bottom of the left column,
  which scrolled out of view and made working buttons look broken.
- The sidebar and the output pane scroll independently; the page itself never scrolls.


## Using a claude.ai share link

Pick **Local + a shared link** (to mix in your local sessions) or **Only a shared link**,
paste the link into the box that appears under *Source*, and press Generate:

```
https://claude.ai/share/a683405f-3f2d-4050-8b77-1c4aef7ce550
```

Have the payload saved to disk instead? Leave the link box empty and press
**Browse...** next to *...or a saved payload file* to pick it without typing the path.
Either box on its own is enough, but one of them is required whenever a shared link is
part of the source.

How it works, verified 2026-10-06 against a live share link:

- The share **page** is an empty JavaScript shell â€” the conversation is not in the HTML.
  The data comes from a public, unauthenticated endpoint:
  `https://claude.ai/api/chat_snapshots/<uuid>?rendering_mode=messages&render_all_tools=true`
- That endpoint **rejects the default Python user agent** with `403`, so a browser UA is
  sent. It also **rate-limits**, so the fetch retries with a backoff and the payload is
  cached to your Documents folder (`claude-share-<id>.json`) for offline re-runs.
- If the fetch cannot get through, the interface tells you the manual route: open the
  share link, DevTools â†’ Network â†’ filter `chat_snapshots` â†’ Copy response â†’ save as
  `.json`, then press **Browse...** to pick that file (or type its path). Fully offline.

Imported chats become a normal session, so they merge with your local sessions or can be
used on their own. Branching conversations are handled: only the deepest active branch
is imported, not abandoned side branches.

Details worth knowing:

- claude.ai's own tool names are mapped to familiar ones (`bash_tool` â†’ `Bash`,
  `view` â†’ `Read`, `str_replace_editor` â†’ `Edit`, `present_files` â†’ `PresentFiles`).
- The sharer's name (`creator.full_name`) is **not** imported.
- Thinking blocks and `hidden_in_chat` blocks are skipped unless you ask for them.
- Only `claude.ai` share URLs are accepted, so the tool can't be pointed at other hosts.

## Command line

```powershell
# local sessions
py claude_memory.py --list                                  # what exists
py claude_memory.py --here --since 7d                      # this project, last week
py claude_memory.py --here --since 30d --out MEMORY.md      # write it (5 history versions kept)
py claude_memory.py --here --format claude-md --print       # paste-ready CLAUDE.md block

# shared links
py claude_memory.py --share https://claude.ai/share/<uuid> --only-share
py claude_memory.py --share-file saved-payload.json --only-share    # offline

# full detail instead of a summary
py claude_memory.py --mode transcript --out chat.md
py claude_memory.py --mode json --out chat.json
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

Full context exists because the summary is lossy on purpose: assistant prose is filtered
into a handful of short sections and anything that matches no pattern is dropped. The
full kind keeps every user and assistant turn (tool calls as one line each, tool output
still omitted because it is mostly file dumps) and prints its own size, so you can judge
whether it will fit in a CLAUDE.md import. On a 38-message shared chat that is 83k chars
(~21k tokens) against 9k for the summary — 9× bigger, nothing lost.

The summary sections, in both memory kinds:

| Section | Why it is there |
|---|---|
| Sessions | when/what/where, one row each, so Claude knows the shape of the history |
| Compactor summaries | Claude's own session summaries, highest signal per char |
| Most recent exchange | the last question plus every reply since it, verbatim |
| What was asked | your prompts, verbatim â€” the actual intent |
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
  everything answered after it, word for word. Long replies keep their start *and* their
  end, because that is where conclusions live.
- **Newest wins.** When a section exceeds its cap, the most recent matches are kept, in
  chronological order, not the first ones found.
- **Prompt caps drop the oldest.** If a chat has more prompts than `--max-prompts`, the
  earliest are omitted and the count is stated.

What a summary still cannot hold is mid-conversation prose that matches no pattern. Use
**memory (full context)** when that matters.

Selection is heuristic (regex over assistant prose + tool-call records), not an LLM
summary. Consequences worth knowing:

- Only the assistant's own statements become claims. Anything you said but Claude never
  echoed is only visible under *What was asked*.
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
`.claude/commands/memory.md` â€” `/memory` then regenerates the block, patches it into
`CLAUDE.md`, and reports what carried over, what was superseded, and what needs
checking against the code.

## Safety

The tool only ever writes the file you choose (plus its `.history/` folder) and the
share cache in Documents. It never modifies transcripts, never reads
`server.cfg`/`.env`/credentials, and only ever contacts `claude.ai`.

Your transcripts contain whatever you pasted into them, and so does `MEMORY.md`. If your
sessions contain secrets or customer data, keep `MEMORY.md` and `MEMORY.history/` out of
git (`.gitignore`) â€” the tool does not do that for you.

## Tests

```powershell
py -3.12 -m unittest test_claude_memory test_gui test_app -v   # 266 tests, offline
py -3.14 -m unittest test_claude_memory test_gui test_app       # same suite on 3.14

# fake ~/.claude tree and a fake share payload to try things out
py -3.12 make_fixture.py .\_fixture_root
py -3.12 claude_memory.py --root .\_fixture_root
```

What the tests cover, none of it needing a network or a window:

- `claude_memory.py` â€” JSONL parsing, malformed-line tolerance, section selection,
  budget, share import (URL validation, retry/backoff, branch choice, block mapping)
- `gui.py` â€” option mapping, path-traversal refusal, token gate, body decoding, page
  assembly, route dispatch
- `app.py` â€” the bridge contract (errors come back as data, never as exceptions),
  console-less startup (`None` streams), and a check that the bridge transport never
  references localhost

The window itself was verified by hand: opened for real, confirmed no listener on the
test port, generated from a share payload through the page's own JS, and screenshotted.


