# IMPLEMENTATION_PLAN — claude-memory 2.0 (C# / WPF)

Status: **Awaiting approval**  
Inputs: `PRD.md`, `AGENTS.md`, Python reference 1.1.0  
Rule: one phase per commit; stop at each gate; do not start Phase N+1 before Phase N is verified.

## 1. Architecture

```
ClaudeMemory.App (WPF, net8.0-windows, WinExe)
  App.xaml / App.xaml.cs            startup, single-instance routing, crash log
  MainWindow.xaml(.cs)              layout, drag/drop, WebView2 host, window lifetime
  ViewModels/MainViewModel.cs       command state, status, busy flag, validation
  ViewModels/SessionRow.cs          scan table row
  Services/FileDialogService.cs     Open/Save dialogs
  Services/ClipboardService.cs      clipboard with failure result
  Services/PreviewHtmlBuilder.cs    Markdig + safe-link pass + CSS + CSP
  Services/ShellIntegration.cs      HKCU registration, unregister, Default Apps UI
  Services/SingleInstance.cs        mutex + named pipe
  Services/Dialogs.cs               fatal/recoverable error presentation
  Assets/preview.css                dark GitHub-like preview styling
        |
ClaudeMemory.Cli (console, net8.0)
  Program.cs, CliSpecs.cs, CliParser.cs, CliHelp.cs, CliRunner.cs, CliValue.cs
        |
ClaudeMemory.Core (class library, net8.0)
  Model/            Turn, Session, MemoryOptions, Budget, FileFacts, BuildRequest/Result
  Parsing/          JsonlSessionParser, SessionDiscovery, SessionSelector, SharePayloadParser
  Text/             TextUtil, TokenEstimator, AsciiFold, BilingualTitles, TimeWindow, Glyphs
  Rendering/        Heuristics, SummaryBuilder, FullMemoryBuilder, TranscriptBuilder,
                    JsonExporter, FileFactsCollector, Sections/*
  Services/         BuildService, SaveService, PathPolicy, MarkdownFileReader,
                    ShareClient, ShareCache, AtomicFileWriter, HistoryArchiver
  Abstractions/     IClock, IHttpTransport, IRegistryStore (implemented by App)
tests/
  ClaudeMemory.Core.Tests/          unit + golden parity
  ClaudeMemory.App.Tests/           view model, preview safety, registry store, IPC
  ClaudeMemory.Cli.Tests/           parser, help, exit codes, process stdio
  fixtures/                         frozen transcript/share corpus
  golden/                           frozen Python 1.1.0 outputs
tools/
  gen-golden.ps1                    regenerate golden files from the Python oracle
  publish.ps1                       Release publish to dist/ClaudeMemory
```

Dependency rule: `App -> Core`, `Cli -> Core`, `Core` has no UI, registry, console, or WPF reference.

## 2. Parity matrix

| Python source | C# destination | Verification |
|---|---|---|
| `claude_memory.py` helpers (`one_line`, `sentences`, `dedupe*`, `norm_key`, `fmt_cwd`, `fmt_dur`, `clip_middle`) | `Text/TextUtil.cs` | unit vectors copied from reference cases |
| `estimate_tokens`, `to_ascii`, `bilingual_title`, `BILINGUAL_TITLES` | `Text/*` | unit vectors incl. Arabic/Cyrillic/CJK |
| `parse_ts`, `parse_since` | `Text/TimeWindow.cs` | formats: 30m/12h/7d/1w/date/ISO |
| `parse_session`, `_iter_records`, `discover`, `select` | `Parsing/*` | fixture sessions + malformed lines |
| `fetch_share`, `share_uuid`, `share_chain`, `parse_share_payload`, `load_share_file` | `Services/Share*.cs`, `Parsing/SharePayloadParser.cs` | fake HTTP: 200/403/429/404/non-JSON; branch fixtures |
| `Budget`, `render_*`, `build_memory`, `build_full_memory` | `Rendering/*` | golden `memory`/`full` outputs |
| `build_transcript`, `build_json`, `render_list` | `Rendering/*` | golden `transcript`/`json`/`list` outputs |
| `atomic_write`, `archive_history`, `safe_out_path` | `Services/AtomicFileWriter.cs`, `HistoryArchiver.cs`, `PathPolicy.cs` | temp-dir tests |
| `markdown_preview.py` | `PreviewHtmlBuilder` via Markdig | safety tests, not AST parity |
| `gui.py` routes / `app.py` window | `MainViewModel` + WPF shell | view-model tests + manual UI checks |

Normalisation allowed in golden comparison only:

1. The run timestamp and generator identity: the `_Generated ..._` header line, the transcript's `... generated <instant>_` line, and the JSON `generated` field.
2. The `tool` field in JSON mode.
3. Absolute paths of the fixture root.
4. Line endings (`\r\n` vs `\n`) are normalised to `\n`.

Anything else must match exactly.

### 2.1 Intentional deviations found while porting

Recorded here so a reviewer can tell a bug from a decision.

1. `Budget` raised-to-floor warning: the reference prints to stderr from inside `Budget`. `Core` must not touch the console, so `Budget.RaisedToFloor` exposes the condition and the CLI host prints the same message (Phase 5).
2. `parse_ts`: only the ISO spellings the app and Claude transcripts produce are accepted (extended date/time, optional fraction, `Z` or `±HH:MM`, basic `yyyyMMdd`). Python's `datetime.fromisoformat` additionally accepts ISO week dates and sub-minute times; those still return null here.
3. `parse_ts` keeps the original UTC offset, as the reference does, instead of normalising to UTC.
4. `fmt_cwd` normalises separators only. Python's `Path()` also collapses `.` components; a transcript `cwd` never contains them.
5. `discover` sorts files whose timestamp cannot be read as `DateTime.MinValue`; the reference would raise `OSError`.
6. `build_transcript` interpolates `s.end` directly, so the reference raises when a session has a start but no end. The C# port reuses `s.start`, keeping the renderer total for truncated transcripts.
7. Share error detail text carries the .NET exception message (for example a `JsonException` or an `HttpRequestException`) where the reference prints Python's own wording. The stable prefixes (`cannot read …`, `… is not valid JSON: …`, `could not read share link after N attempts: …`) match.
8. `--share-save` is best-effort: an IO failure returns false and the import continues, whereas the reference would propagate the write error after the payload was already fetched.
9. `fetch_share`'s 404-specific `raise` is unreachable in the reference (the retry filter breaks first), so it is not ported; a 404 reports the final `could not read share link after N attempts: HTTP 404 …` message.
10. An IO failure while reading the previous history copy or writing the output is reported as `[claude-memory] {message}` with exit 1. The reference lets the `OSError` escape as a traceback; the exit code is the same and no stack trace reaches the user.
11. The CLI help golden stores `%DEFAULT_ROOT%` instead of this machine's home directory, so `tests/golden/cli-help.txt` is machine-independent. The test substitutes `MemoryOptions.DefaultRoot()` (claude_memory.py:39-40).
12. The desktop number fields fall back on unparseable input like `as_int` (gui.py:895), but only accept a leading sign around decimal digits. Python's `int()` also accepts underscores, surrounding whitespace and non-ASCII digits; an `<input type="number">` cannot produce those, so the UI never reaches the difference.

## 3. Dependencies

| Package | Exact version | Justification |
|---|---|---|
| Markdig | 1.5.1 | GFM tables/task lists/strikethrough. A hand-written parser would be ~600 lines with worse edge-case coverage. |
| Microsoft.Web.WebView2 | 1.0.4314.35 | Preview fidelity. Runtime 148 is already present on this machine; source view is the fallback. |
| xunit | 2.9.3 | Tests. |
| xunit.runner.visualstudio | 3.1.5 | Test discovery. |
| Microsoft.NET.Test.Sdk | 17.14.1 | Test host. |

No MVVM toolkit: a 40-line `RelayCommand` and `ObservableObject` base are sufficient. No JSON package: `System.Text.Json`. No logging package: status line + crash log only.

## 4. Phase plan

### Phase 0 — Scaffold and freeze the oracle

Scope:

- Initialise a local git repository (no remote), commit the untouched Python tree so every phase is revertible.
- Create `src/ClaudeMemory.sln`, the three projects, `tests` projects, `Directory.Build.props`, `.editorconfig`, app manifest (`longPathAware`, DPI aware), `publish.ps1`, `tools/gen-golden.ps1`.
- Copy `_fixture_root` to `tests/fixtures/fixture_root` and `out/share-payload.json` to `tests/fixtures/share-payload.json` (repository-tracked copies).
- Run the Python reference and commit `tests/golden/*.md|json` for: memory/plain, memory/claude-md, memory/ascii, full/plain, transcript, json, plus a synthetic corpus with Arabic text, errors, decisions, preferences, TODOs, tool calls, and a branching share payload.
- Use a small synthetic share fixture (`tests/fixtures/share-branching.json`) instead of copying the real `out/share-payload.json`, so no captured conversation is added to the repository.
- Normalise the golden files to LF. Python on Windows writes CRLF when redirected; line endings are one of the allowed parity normalisations.

Acceptance:

- `dotnet build src/ClaudeMemory.sln -c Release` succeeds.
- `tools/gen-golden.ps1` is idempotent and documented.
- Golden files are committed before any C# behaviour exists.
- `git status` clean.

Rollback: delete `src/`, `tests/`, `tools/`, `Directory.Build.props`, `.editorconfig`; the Python tree is untouched.

### Phase 1 — Core model, parsing, discovery, selection

Scope: `Model/*` (Turn, Session, MemoryOptions, Budget), `Text/TextUtil.cs`, `Text/TimeWindow.cs`, `Parsing/JsonlSessionParser.cs`, `Parsing/ToolBrief.cs`, `Parsing/PyJson.cs`, `SessionDiscovery.cs`, `SessionSelector.cs`.

Work:

- Port record handling: `sessionId`, `cwd`, `gitBranch`, `version`, `isMeta`, `isSidechain`, `summary`, `message.content` string/list, `text`, `thinking`, `tool_use`, `tool_result.is_error`, unknown roles.
- Port defensive reading: byte cap with tail read, truncated-line discard, malformed-line skip, UTF-8 replacement.
- Port discovery order (mtime, then name) and selection (`here`, `cwd`, `branch`, `since`, newest N).
- Port `Budget` and text helpers with unit vectors.

Tests: fixture counts, prompts, tool calls, errors; zero-byte/corrupt/truncated files; sidechain drop; thinking toggle; `since` formats; no-match selection.

Gate: core tests green; no golden rendering tests yet.

### Phase 2 — Summary renderer (`memory` mode)

Scope: `Rendering/Heuristics.cs`, `FileFactsCollector.cs`, `Sections/*`, `SummaryBuilder.cs`, `Text/TokenEstimator.cs`, `AsciiFold.cs`, `BilingualTitles.cs`.

Work: header, sessions table, compactor summaries, intent, recent exchange with middle clip, claims with recency dedupe, files/commands/subagents, errors, next steps/plans, identifiers, trim note, `claude-md` wrap, ASCII fold, language selection.

Tests:

- Golden comparison for `memory/plain` and `memory/claude-md` on fixtures + synthetic corpus.
- Budget floor, trim note, newest-wins, prompt cap dropping oldest, Arabic bilingual headers, ASCII letters preserved.
- Heuristics unit vectors for English and Arabic patterns.

Gate: golden parity for `memory`; review any intentional difference before continuing.

### Phase 3 — Full, transcript, JSON, save, history

Scope: `FullMemoryBuilder.cs`, `TranscriptBuilder.cs`, `JsonExporter.cs`, `ListRenderer.cs`, `AtomicFileWriter.cs`, `HistoryArchiver.cs`, `PathPolicy.cs`, `SaveService.cs`.

Work: full conversation layout, transcript layout, JSON shape/key order/indentation, list table layout, atomic temp-then-replace, history retention and naming, save-path refusal rules, folder reveal.

Tests: golden `full`, `transcript`, `json`, `list`; JSON survives round-trip and uses unescaped Unicode; history keeps N and prunes oldest; path traversal, relative folder, bad suffix, read-only folder, locked target; cancellation leaves no temp file.

Gate: all four modes byte-parity after normalisation; IO tests green.

### Phase 4 — Share import

Scope: `Services/ShareClient.cs`, `ShareCache.cs`, `Parsing/SharePayloadParser.cs`, `ShareBranch.cs`, `WebToolAliases.cs`.

Work: URL/UUID validation, strict host check, browser UA, retry/backoff with injected delay, cancellation, cache write best-effort, saved-file loader (object or array), HTML-shell diagnosis, deepest-branch traversal, tool aliases, hidden/thinking blocks, compaction summaries.

Tests (fake `HttpMessageHandler`, zero network): 200, 403→200 retry, 429, 404, 5xx exhaustion, non-JSON body, timeout, saved array, missing `chat_messages`, HTML shell, branch choice, tool alias mapping, cache failure ignored.

Gate: share tests green; no socket opened.

### Phase 5 — CLI host

Scope: `ClaudeMemory.Cli/*`, `tests/ClaudeMemory.Cli.Tests/*`, the `cli-help.txt` golden, and the help capture in `tools/gen-golden.ps1`.

Files:

- `Program.cs` — entry point; UTF-8 without BOM, LF, `Ctrl+C` cancels and flushes.
- `CliValue.cs` — `CliUsageException`, `CliValueException`, `PyInt`, `PyText.Repr`.
- `CliSpecs.cs` — the ordered option table shared by the parser and the help renderer.
- `CliHelp.cs` — `usage:` wrapping, the options section, argparse's `textwrap` port.
- `CliParser.cs` — the argparse-compatible parser (`--name=value`, prefixes, `--`, negative values, immediate help/version).
- `CliRunner.cs` — `main()` parity: share import, discovery, `--fast`, selection, render, history, write, stderr lines, exit codes.

Work:

- Hand-rolled argument parser with the reference flags, abbreviations, ambiguous-prefix errors, `ignored explicit argument`, and `unrecognized arguments` (including the `--` marker itself).
- Help layout at `COLUMNS - 2` (default 80 → 78); the options section reproduces argparse's hyphen-aware wrapping.
- Exit codes 0 (ok), 1 (no sessions / IO failure), 2 (usage or no transcript dir), 3 (share failure), 130 (`interrupt`).
- stdout/stderr split with the reference prefixes (`[claude-memory] ...`), `--quiet`, `--print`, `--version`, `--list`, `--fast` with mtime cutoff, `--max-chars` floor warning, and history archiving for `memory` mode only.

Tests (`ClaudeMemory.Cli.Tests`, 98 cases):

- Parser table: every error string, ambiguity, abbreviation, negative value, inline `=`, `--`, and the immediate help/version actions; `PyInt` Python spellings including Unicode digits and saturation.
- Help: byte-identical `cli-help.txt` after substituting `%DEFAULT_ROOT%`; usage-block shape; `usage:` + `claude_memory.py: error:` layout; version identity.
- Runner: every mode against the frozen goldens, `--share-file` import, the exit-code table, the exact no-transcript/no-session messages, `--cwd`, `--quiet`, `--out` + `--print` + the paste-ready hints, history naming/pruning, `--fast` with controlled mtimes, and the floor warning.
- Process: the real executable writes UTF-8 without a BOM, LF only, and survives non-ASCII output.

Parity verification beyond the suite (manual, this machine): 56 forced side-by-side runs of `py -3.12 claude_memory.py` versus the built CLI compared stdout, stderr, and exit codes after timestamp normalisation, plus a byte-identical `--help` check and a stamp-normalised history re-check. All matched except the documented `.NET` exception detail text (deviation 7) and a wall-clock history-naming artefact that re-matched once the timestamp was normalised.

Gate: CLI parity signed off; this becomes the oracle after Python removal.

### Phase 6 — WPF shell

Scope: `ClaudeMemory.App/*` excluding preview and shell integration.

Files:

- `App.xaml` / `App.xaml.cs` — dark theme resources, window startup, `claude-memory-error.log` + fatal dialog beside the executable.
- `MainWindow.xaml` / `.xaml.cs` — left options column with pinned Generate, right toolbar/status/output; code-behind only for the first scan and window-close cancellation.
- `ViewModels/ObservableObject.cs`, `RelayCommand.cs` — 40-line command/notification pair; no MVVM toolkit.
- `ViewModels/MainViewModel.cs` — source/filter/output/save state, busy flag, status line, every user action and its reference wording.
- `ViewModels/SessionRow.cs` — one scan-table row (`when`, `folder`, `turns`).
- `Services/BuildRequest.cs`, `BuildPipeline.cs` — the `/api/build` payload and the `do_build` port.
- `Services/SessionScanner.cs` — the `do_scan` port.
- `Services/IUserServices.cs`, `WindowsUserServices.cs` — file dialogs, clipboard, folder reveal, dialogs, split out so tests never touch a window.

Work:

- Dark layout per PRD §6: 360 px scrollable options column, Generate pinned at the bottom, toolbar, status line and source view on the right; custom ComboBox template because the system template paints a light selection box.
- Bindings for source (local / both / share-only with the hidden share box), filters, all output options, save folder/name/history, and the scan table.
- Async generate with `CancellationToken`, indeterminate progress, disabled actions, and a Cancel button; the status line uses the reference stats string and the reference success, warning and error wording.
- Save / Save As / Folder / Copy / Copy for CLAUDE.md through `SaveService`/`PathPolicy`, including history archiving and the single-wrap CLAUDE.md block.
- `Preview MD` and `Open MD...` are present but disabled until Phase 7; the source view is always visible.
- Startup crash log and dialog; recoverable failures stay in the status line.
- Keyboard: `Ctrl+Enter` generates, `Ctrl+S` saves, `Ctrl+O` is bound to the Phase 7 open command, `Esc` closes dialogs.

Tests (`ClaudeMemory.App.Tests`, 44 cases):

- `MainViewModel` state machine: idle → busy → done / error / cancelled, status text and kind, command enablement before, during and after a run, previous output preserved on failure.
- Option mapping to `BuildRequest`, exclusive source radios, share-box visibility, local-filter enablement, mode hint, history count.
- Save to the configured folder, history archiving, refused names, Save As chosen/cancelled, folder reveal without writing, clipboard success/blocked, CLAUDE.md wrap idempotence, scan ordering and empty state.
- `BuildPipeline` parity with a replay of the CLI stages for all four modes on the fixture corpus (text, session count and stats shape identical), option fallbacks, the exact GUI error strings, saved-share import, and the no-network share guards.

Manual verification on this machine: launched the Release build, confirmed the window title, root label, scan table, dark theme, and the red "Nothing to summarise." status on an empty profile; then ran the full Generate → Save flow against a scratch `USERPROFILE` holding the fixture corpus and confirmed the stats line, the source view, and a written `Documents/MEMORY.md` (896 bytes). No crash log was produced.

Gate: `dotnet build` + view-model tests + manual window check.

### Phase 7 — Markdown preview

Scope: `PreviewHtmlBuilder.cs`, `MainWindow` preview host, `MarkdownFileReader.cs`, drag/drop, toggle.

Work:

- Markdig pipeline with `DisableHtml()`; GFM extensions; CSS matching the app.
- AST safety pass: allow only `http`, `https`, `mailto`, anchors; unsafe links rendered as plain text; no remote images.
- HTML document shell with CSP, no scripts, `about:blank` origin.
- WebView2 init: disable devtools/context menu/status bar, cancel all navigation except the initial document, `NewWindowRequested` opens validated external links in the default browser.
- Open MD: dialog, drag/drop, command-line path, 4 MiB/suffix/empty/NUL checks, error messages.
- Toolbar toggle: `Preview MD` ↔ `Show source`; file preview ↔ `Close preview`; JSON mode explains why preview is unavailable.
- Runtime missing → source view remains; show install link.

Tests: XSS corpus (`<script>`, `<iframe>`, `onerror`, `[x](javascript:...)`, `[x](data:...)`, `[x](file:...)`, raw HTML disabled), scheme allowlist, size/empty/NUL rejection, HTML contains CSP and zero `<script>` tags. Manual preview check at 100%/150% DPI.

Gate: security tests green; manual preview verified; no scripts in generated HTML.

### Phase 8 — File association, single instance, command-line open

Scope: `Services/ShellIntegration.cs`, `SingleInstance.cs`, `App.xaml.cs`, `MainWindow` startup routing.

Work:

- `IRegistryStore` abstraction over `HKCU\Software\Classes`; write ProgID, `OpenWithProgids`, `Applications\ClaudeMemory.exe`, `RegisteredApplications`, `Capabilities`.
- Explicit UI action; show registered state; unregister removes only app-owned keys/values and never touches `UserChoice` or the `.md` default value.
- After registration, open `SHOpenWithDialog` for a `.md` file; fall back to `ms-settings:defaultapps`.
- Single instance: named mutex + named pipe; second launch forwards the path and focuses the window.
- Startup: no args → normal window; one valid `.md` path → preview; invalid path/dir → message.

Tests: in-memory registry store asserts exact keys/values written and removed; path quoting in the command value; unregister leaves foreign values; mutex/pipe forward test (loopback pipe name); startup argument routing unit tests.

Manual checks (human): real HKCU registration, Explorer "Open with", Default Apps confirmation, double-click `.md`, second launch while running.

Gate: registry unit tests green + manual association checklist signed off.

### Phase 9 — Hardening, publish, documentation

Scope: all projects, `publish.ps1`, `README.md`, `claude-slash-command-memory.md`, `.gitignore`.

Work:

- Full test suite + `dotnet build -warnaserror`.
- Performance check: fixture build under 1 s; app launch to preview under 2 s; 10 MB transcript does not freeze UI.
- Publish self-contained `win-x64` folder to `dist/ClaudeMemory`; verify it runs without Python and with the WebView2 runtime present.
- Rewrite README for the C# app and the association feature; update the slash command to call `ClaudeMemory.Cli.exe`.
- Manual checklist: all modes, save/history, copy, preview, drag/drop, double-click, unregister/re-register, light/dark mode, 150% DPI, Arabic content, cancellation, network failure.

Gate: `vibe-test-gate` anti-test-theater audit + 16-point production gate; human sign-off.

### Phase 10 — Remove the Python implementation (explicit approval required)

Scope: repository cleanup only.

Steps:

1. Create `archive/claude-memory-python-1.1.0.zip` containing the Python sources, tests, launchers, README snapshot, and `.gitignore`; exclude `.venv/`, `__pycache__/`, generated output, and any secrets.
2. Verify the archive lists every removed file and opens successfully.
3. Remove: `app.py`, `gui.py`, `markdown_preview.py`, `claude_memory.py`, `test_app.py`, `test_gui.py`, `test_claude_memory.py`, `make_fixture.py`, `start-app.cmd`, `setup-webview.cmd`, `.venv/`, `__pycache__/`.
4. Keep `tests/fixtures` and `tests/golden` copies; remove the root `_fixture_root/` and `out/` duplicates only after confirming the copies.
5. Update `.gitignore`, README, and the slash command; search the repository for references to removed files.
6. Run the full build + test suite again.

Acceptance: no reference to Python remains in docs/scripts; build and tests pass; archive integrity verified; user approved the deletion.

Rollback: restore from `archive/claude-memory-python-1.1.0.zip` and the Phase 9 commit.

## 5. Test strategy

- **Unit:** parsing, text utilities, heuristics, budget, path policy, history, registry store, IPC.
- **Golden parity:** run the frozen reference outputs; compare exact text after the four allowed normalisations. A golden update requires a documented behaviour reason.
- **Security:** XSS/scheme corpus, registry confinement, path traversal, no network in tests.
- **UI:** view-model tests; manual visual checklist for WPF/WebView2 (no flaky UI automation in v1).
- **End-to-end CLI:** fixture root → each mode → compare golden + exit code.
- **No real side effects in tests:** temp directories only; fake HTTP; in-memory registry; loopback IPC.

## 6. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Windows blocks silent default association | Feature appears "not working" | Registration + `SHOpenWithDialog`/Settings; explicit UI copy; status shows registration state |
| Parity drift from Python regex/date/JSON differences | Silent output changes | Golden files frozen before coding; .NET/Python regex and JSON escaping tested explicitly |
| Python oracle removed too early | No reference to resolve disputes | Keep Python + golden files until Phase 10; archive before deletion |
| WebView2 runtime missing | Preview unavailable | Source view fallback; runtime install link; startup does not fail |
| Markdown XSS | Local code execution | HTML disabled, scheme allowlist, CSP, no scripts, navigation lock |
| UI freeze on huge histories | Poor UX | All IO/build off UI thread; 12 MiB per-file cap; cancellation |
| Long paths > 260 chars | Save/open failures | `longPathAware` manifest; path policy tests |
| Registry left dirty after uninstall | User-visible side effects | Exact-key unregister tests; no default-value writes; no HKLM |
| Single-instance IPC race | Lost file-open request | Mutex + retry/fallback; loopback test |
| No git history currently | Irreversible removal | Phase 0 initialises git; Phase 10 archives Python first |

## 7. Definition of done

A phase is done only when:

1. `dotnet build src/ClaudeMemory.sln -c Release` succeeds and the output is quoted.
2. `dotnet test src/ClaudeMemory.sln -c Release` passes and the summary is quoted.
3. `git diff` contains only the phase's files.
4. The phase gate output is written in the report: files changed, commands run, results, known failures, residual risks.
5. No Python file was modified before Phase 10.
6. Any manual-only check is listed explicitly, not claimed as passed.