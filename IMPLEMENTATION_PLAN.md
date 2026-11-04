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
  Program.cs, CommandLine/ArgumentParser.cs, CommandLine/HelpText.cs
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
| `build_transcript`, `build_json` | `Rendering/*` | golden `transcript`/`json` outputs |
| `atomic_write`, `archive_history`, `safe_out_path` | `Services/AtomicFileWriter.cs`, `HistoryArchiver.cs`, `PathPolicy.cs` | temp-dir tests |
| `markdown_preview.py` | `PreviewHtmlBuilder` via Markdig | safety tests, not AST parity |
| `gui.py` routes / `app.py` window | `MainViewModel` + WPF shell | view-model tests + manual UI checks |

Normalisation allowed in golden comparison only:

1. The `_Generated ..._` header line (timestamp and generator identity).
2. The `tool` field in JSON mode.
3. Absolute paths of the fixture root.
4. Line endings (`\r\n` vs `\n`) are normalised to `\n`.

Anything else must match exactly.

## 3. Dependencies

| Package | Exact version | Justification |
|---|---|---|
| Markdig | 1.5.1 | GFM tables/task lists/strikethrough. A hand-written parser would be ~600 lines with worse edge-case coverage. |
| Microsoft.Web.WebView2 | 1.0.4365.35 | Preview fidelity. Runtime 148 is already present on this machine; source view is the fallback. |
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

Acceptance:

- `dotnet build src/ClaudeMemory.sln -c Release` succeeds.
- `tools/gen-golden.ps1` is idempotent and documented.
- Golden files are committed before any C# behaviour exists.
- `git status` clean.

Rollback: delete `src/`, `tests/`, `tools/`, `Directory.Build.props`, `.editorconfig`; the Python tree is untouched.

### Phase 1 — Core model, parsing, discovery, selection

Scope: `Model/*`, `Text/TextUtil.cs`, `TimeWindow.cs`, `Parsing/JsonlSessionParser.cs`, `SessionDiscovery.cs`, `SessionSelector.cs`.

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

Scope: `FullMemoryBuilder.cs`, `TranscriptBuilder.cs`, `JsonExporter.cs`, `AtomicFileWriter.cs`, `HistoryArchiver.cs`, `PathPolicy.cs`, `SaveService.cs`.

Work: full conversation layout, transcript layout, JSON shape/key order/indentation, atomic temp-then-replace, history retention and naming, save-path refusal rules, folder reveal.

Tests: golden `full`, `transcript`, `json`; JSON survives round-trip and uses unescaped Unicode; history keeps N and prunes oldest; path traversal, relative folder, bad suffix, read-only folder, locked target; cancellation leaves no temp file.

Gate: all four modes byte-parity after normalisation; IO tests green.

### Phase 4 — Share import

Scope: `Services/ShareClient.cs`, `ShareCache.cs`, `Parsing/SharePayloadParser.cs`, `ShareBranch.cs`.

Work: URL/UUID validation, strict host check, browser UA, retry/backoff with injected delay, cancellation, cache write best-effort, saved-file loader (object or array), HTML-shell diagnosis, deepest-branch traversal, tool aliases, hidden/thinking blocks, compaction summaries.

Tests (fake `HttpMessageHandler`, zero network): 200, 403→200 retry, 429, 404, 5xx exhaustion, non-JSON body, timeout, saved array, missing `chat_messages`, HTML shell, branch choice, tool alias mapping, cache failure ignored.

Gate: share tests green; no socket opened.

### Phase 5 — CLI host

Scope: `ClaudeMemory.Cli/*`.

Work: hand-rolled argument parser with reference flags, help text, exit codes 0/1/2/3, stdout/stderr split, `--list`, `--print`, `--version`, `--quiet`, `--fast`, history behaviour.

Tests: parser table (valid/invalid), end-to-end runs against fixtures, golden output, exit codes, stdin/stdout encoding on Windows.

Gate: CLI parity signed off; this becomes the oracle after Python removal.

### Phase 6 — WPF shell

Scope: `ClaudeMemory.App/*` excluding preview and shell integration.

Work:

- Dark modern layout per PRD §6; left options column, pinned Generate, right toolbar/status/output.
- Bindings for source, filters, output options, save options, scan table.
- Async generate with busy state and cancellation; status text from the reference stats string.
- Save / Save As / Folder / Copy / Copy for CLAUDE.md; source view.
- Startup crash log + dialog; WebView2-missing state deferred to Phase 7 but designed for.
- Keyboard: `Ctrl+O`, `Ctrl+S`, `Ctrl+Enter` (generate), `Esc` cancels dialogs.

Tests: `MainViewModel` state transitions with fake services (idle→busy→done/error/cancelled), button enablement, status messages, option mapping, save validation. Manual launch check with screenshots.

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