# Claude Code slash command: /memory
#
# Install by copying this file to <project>/.claude/commands/memory.md
# (or %USERPROFILE%/.claude/commands/memory.md to have it in every project).
# Adjust MEMORY_TOOL below if you moved ClaudeMemory.Cli.exe somewhere else.

---
description: Regenerate MEMORY.md from recent Claude Code sessions and re-read it
argument-hint: "[since, e.g. 7d | 30d | 2026-10-01] [extra ClaudeMemory.Cli flags]"
allowed-tools: Bash(ClaudeMemory.Cli*), Read, Edit
---

Reconstruct my working context for this project from recent Claude Code sessions.

1. Run the memory tool. Default window: 7d. Pass any extra arguments after `/memory`
   through as flags.

   !`"C:/Users/ImPro/Desktop/claude-memory/dist/ClaudeMemory.Cli/ClaudeMemory.Cli.exe" --here --since 7d --out MEMORY.md --history 5 --quiet`

   If that reports `no sessions matched`, retry once with a wider window
   (`--since 30d`), then once with no `--since` at all. If it still finds nothing, say
   so plainly and stop — do not invent context.

2. Read `MEMORY.md`.

3. Replace the text between the `<!-- claude-memory:begin -->` and
   `<!-- claude-memory:end -->` markers in this project's `CLAUDE.md` with the new
   block, keeping the markers. If `CLAUDE.md` has no such block yet, append the whole
   block at the end of the file. Leave the rest of `CLAUDE.md` untouched.

4. Reply with, in this order:
   - **Carried over:** the decisions, constraints and open items from the memory that
     still apply, as a short list.
   - **Superseded:** memory items contradicted by anything later in this conversation.
   - **Gaps:** anything in the memory that is ambiguous or unverifiable from the repo,
     which you will need to check in code before relying on it.

Do not treat the memory as authoritative: it is a lossy summary of past sessions.
Where it disagrees with the code in this repository, the code wins, and say so.
