# claude-memory — Project Rules

## Current state

- `claude_memory.py` 1.1.0, `gui.py`, `app.py`, and `markdown_preview.py` are the **reference implementation** and the behavioural oracle.
- The C# rewrite lives under `src/`. Target: .NET 8 LTS, WPF, Windows 10 1809+.
- Python is removed only in the final phase, after the parity suite passes and the human approves removal.
- Until then, do not edit Python application files. Treat them as read-only evidence.

## Architecture

```
src/
  ClaudeMemory.Core/   net8.0 class library — parsing, selection, rendering, IO, share import
  ClaudeMemory.App/    net8.0-windows WPF shell — views, view models, dialogs, WebView2, shell integration
  ClaudeMemory.Cli/    net8.0 console host — headless parity with claude_memory.py
tests/
  ClaudeMemory.Core.Tests/
  ClaudeMemory.App.Tests/
```

Dependency direction: `App -> Core`, `Cli -> Core`, `Core -> (nothing UI-specific)`.
`Core` must not reference WPF, `System.Windows.Forms`, the Windows registry, or the console.

## Build and test

```powershell
dotnet build src/ClaudeMemory.sln -c Release
dotnet test src/ClaudeMemory.sln -c Release
dotnet run --project src/ClaudeMemory.App -c Debug
dotnet run --project src/ClaudeMemory.Cli -c Debug -- --root _fixture_root --list
```

A change is not done until the build and the relevant tests have actually been run and the output was observed. Say which commands ran and what they printed.

## Behaviour rules

1. **Parity first.** Every ported behaviour cites the Python source (`file:line`) in the commit body. If a difference is intentional, document it in `PRD.md` and update the golden file in the same commit.
2. **No invented behaviour.** Do not add filters, flags, formats, caches, telemetry, or "helpful" defaults that the Python app did not have, unless the PRD lists them.
3. **Recency wins.** Preserve the Python rules: newest material survives trimming; the end of a conversation is always quoted; prompt caps drop the oldest.
4. **Budget math is observable.** `Budget` accounting and the trim note must match the reference output. Do not silently raise limits.
5. **Encoding.** Read transcripts with UTF-8 and replacement fallback. Write UTF-8 without BOM, LF newlines. Never crash on a malformed line or an unknown role.
6. **Windows paths.** Long paths must work. Enable the manifest `longPathAware` setting and use `\\?\`-safe APIs where needed.

## UI rules

- MVVM-lite: logic in `Core` or a view model; code-behind only for view-specific glue (WebView2 host, drag/drop, window lifetime).
- All work that can block (disk, network, build) runs off the UI thread with a `CancellationToken`.
- Errors are shown in the status line or a dialog. Never swallow an exception silently; never show a raw stack trace to the user.
- Status text goes next to the action buttons, not at the bottom of a scrolling panel.
- Buttons disable while their operation is running. No duplicate concurrent builds or saves.
- Options must remain reachable: the Generate action is pinned at the bottom of the left column.

## Preview and rendering security invariants

- Markdown is converted with **raw HTML disabled**. Never enable `Markdig` HTML passthrough.
- Links are allowed only for `http`, `https`, `mailto`, and in-document anchors. Reject `javascript:`, `data:`, `file:`, `vbscript:`, and unknown schemes.
- The preview WebView2 blocks navigation to any origin. External links open in the default browser after scheme validation.
- The preview page runs no scripts. CSP: `default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:`.
- No local HTTP server is introduced. Do not add one.
- File dialogs and drag/drop are the only ways to obtain a preview path. Validate extension (`.md`, `.markdown`), size (4 MiB), NUL bytes, and empty files before rendering.

## Data and filesystem rules

- The app writes only: the file the user chose, its `<name>.history/` folder, and the share cache in the user's Documents folder.
- Never modify transcripts under `~/.claude`.
- Never log transcript text, prompts, share payloads, or generated memory content.
- Save paths: absolute folder, single file name, no separators, allowed suffixes `.md .markdown .json .txt`.
- History archiving keeps at most N files and never deletes anything outside `<name>.history/`.

## Network rules

- Only `https://claude.ai/api/chat_snapshots/<uuid>` is contacted, and only after validating the share URL or UUID.
- Send the documented browser User-Agent; retry `403/429/5xx` with 2 s, 5 s, 12 s backoff; honour cancellation.
- Cache a fetched payload under Documents as `claude-share-<id8>.json`; never cache to the repository.
- All network code accepts an injected `HttpMessageHandler` and delay function so tests never hit the network.

## Windows shell integration rules

- Register only under `HKCU\Software\Classes`. **Never** write `HKLM`.
- Never write `UserChoice`. Windows 10/11 ignores or resets it; the supported path is registration plus the Windows confirmation UI (`SHOpenWithDialog` / Default Apps settings).
- The app must be able to unregister exactly the keys and values it created, and must leave other handlers untouched.
- Registration code takes an `IRegistryStore` abstraction; unit tests use an in-memory store and never touch the real registry.
- Association changes require an explicit user action in the UI. No silent takeover on startup.

## Supply chain

Pinned dependencies (no substitutes without a plan update):

| Package | Version | Why |
|---|---|---|
| Markdig | 1.5.1 | CommonMark + GFM tables/task lists; a hand-written parser is not justified for a preview feature |
| Microsoft.Web.WebView2 | 1.0.4365.35 | GitHub-like preview with the runtime already shipped on Windows |
| xunit | 2.9.3 | Test framework |
| xunit.runner.visualstudio | 3.1.5 | Test discovery |
| Microsoft.NET.Test.Sdk | 17.14.1 | Test host |

Everything else uses the base class library. Do not add an MVVM toolkit, JSON library, logging framework, or icon pack.

## Diff discipline

- One phase from `IMPLEMENTATION_PLAN.md` per commit. Do not mix phases.
- No drive-by formatting, renames, or refactors outside the active phase.
- Public behaviour changes need a test. Bug fixes need a regression test where practical.
- Do not delete the Python app, fixtures, or user data without explicit approval in that phase.

## Honesty rules

- Separate "verified by running" from "inferred by reading".
- Never claim a test, build, association, or UI check passed unless it was executed.
- If a step cannot be verified in this environment (for example, a real Explorer double-click), list it as an explicit manual check for the human.