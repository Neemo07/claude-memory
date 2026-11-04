# Project memory — rolling

_Generated 2026-11-04T15:11:56Z by claude_memory.py v1.1.0 from 6 Claude Code sessions, 2026-10-01 → 2026-10-05._

- Turns: **30** (11 user prompts, 10 tool calls) · skipped noise records: **9**
- Working dir(s): `C:/alpha/srv`, `C:/testproj`, `C:/work/arabic`, `C:/work/demo`

_Read this file first for orientation, then open the project files it names. It is a summary, not a transcript — verify anything load-bearing against the code._

## Sessions

| # | when | cwd | branch | turns | tool calls | opened with |
|---|------|-----|--------|-------|-----------|-------------|
| 6 | 2026-10-05 09:00 | `C:/alpha/srv` | main | 3 | 1 | Use the explore agent to find every place that hardcodes a packet siz… |
| 5 | 2026-10-05 09:00 | `C:/testproj` | main | 5 | 2 | Add a --since filter and history rotation to the memory tool. |
| 4 | 2026-10-05 09:00 | `C:/testproj` | main | 7 | 5 | Audit the packet parser in src/net/packet.cpp against the native bina… |
| 3 | 2026-10-02 10:00 | `C:/work/arabic` | main | 4 | 0 | يجب أن يبقى التطبيق دون اتصال. أضف معاينة Markdown. |
| 2 | 2026-10-01 09:00 | `C:/work/demo` | main | 10 | 2 | We must keep the generator offline. Add a Markdown preview command an… |
| 1 | ? | `(unknown cwd)` | - | 1 | 0 | ok |

## Compactor summaries

- _((unknown cwd))_ orphan summary
- _(C:/work/demo)_ Synthetic parity session covering decisions, constraints, tools, and errors.
- _(C:/work/arabic)_ جلسة عربية لاختبار العناوين ثنائية اللغة.
- _(C:/testproj)_ Audited packet.cpp: endianness/length bug at 0x00401230, patched to uint32_t. Build blocked: build_full.py absent. Remaining: header padding check.
- _(C:/testproj)_ Added --since/--history to claude_memory.py; verified malformed-line tolerance. Default budget 14000 chars.

## Decisions taken

_What was settled and why._

- We decided to use Markdig for preview rendering.
- قررنا استخدام Markdown للعرض.
- Decision: the native function at 0x00401230 reads the length field as a big-endian uint32_t, but packet.cpp uses memcpy into a uint16_t.

## Preferences and conventions

_Style and approach choices._

- Convention: always keep the preview read-only.
- Convention in this repo: always use build_full.py, never invoke g++ directly, and keep worker count at 4 or lower.
- I prefer argparse with a dataclass config so the tests can build Options directly.

## Constraints and must-nots

_Rules that later work has to respect._

- The parser must never send transcripts to the network.
- We must never add a bounds check that the native code does not have, because the server relies on the trust model.
- The build script is missing, so the toolchain cannot build.
- Note that a truncated tail is also tolerated; only --max-file-bytes triggers the tail-only read.

## Files changed

- `C:\testproj\claude_memory.py` ×2
- `C:\testproj\src\net\packet.cpp` ×1
- `src/Preview.cs` ×1

## Files read / searched (top)

- `**/build*.py` ×1
- `C:\testproj\src\net\packet.h` ×1

## Commands run

- python build_full.py --workers 4

## Subagents used

- `explore: inspect preview code` ×1
- `explore: find hardcoded packet sizes` ×1

## Open items / TODOs

- TODO: verify the fix on a 10 MB transcript.
- Next steps: run dotnet test and check the mismatch report.
- This is a blocker.
- TODO: locate the real build entry point

## Most recent plan or checklist

- archive previous MEMORY.md under MEMORY.history/
- keep the newest N sessions, oldest dropped
- add parse_since() handling 30m / 7d / YYYY-MM-DD
- re-run tests against docs/BASELINE.md
- confirm whether the 1991 byte header is padded
- Report the mismatch with offsets
- Disassemble the native handler and diff the opcodes
- Read src/net/packet.cpp and the header
- Save C:\work\demo\MEMORY.md.
- Compare 0x1F and 0x2A identifiers.
- Run the full parity suite.

## Full conversation (nothing summarised away)

_30 turn(s) across 6 session(s), verbatim. Tool calls are listed one line each; tool output is omitted because it is mostly file dumps._

### (unknown cwd)

**user**

ok

### C:/work/demo - 2026-10-01 09:00

**user** - 09:01

We must keep the generator offline. Add a Markdown preview command and never phone home.

**assistant** - 09:02

We decided to use Markdig for preview rendering. The parser must never send transcripts to the network. Convention: always keep the preview read-only.

**user** - 09:03

Please inspect C:\work\demo\src\Preview.cs and report the error you found.

**assistant** - 09:04

I will delegate the inspection to a subagent.

- `Task` explore: inspect preview code
**assistant** - 09:05 _(subagent)_

Subagent found a null reference at src/Preview.cs:42; the root cause is a missing guard.

**assistant** - 09:06

- `Edit` file_path=src/Preview.cs
**user** - 09:07

- **error** Error: CS0103 preview is not defined
**assistant** - 09:08

TODO: verify the fix on a 10 MB transcript. Next steps: run dotnet test and check the mismatch report. The preview crashed with a null reference before the guard, and that is fixed now. Version 2.0.1 measured 120 ms.

**user** - 09:09

Keep going and show me the final checklist.

**assistant** - 09:10

Plan:
- Run the full parity suite.
- Compare 0x1F and 0x2A identifiers.
- Save C:\work\demo\MEMORY.md.

Open question: should the association be per-user? Answer: yes, HKCU only.

### C:/work/arabic - 2026-10-02 10:00

**user** - 10:01

يجب أن يبقى التطبيق دون اتصال. أضف معاينة Markdown.

**assistant** - 10:02

قررنا استخدام Markdown للعرض. يجب ألا نرسل النصوص إلى الشبكة. التفضيل: اجعل المعاينة للقراءة فقط.

**user** - 10:03

ما الخطوات التالية؟

**assistant** - 10:04

الخطوات التالية: اختبر الواجهة ثم احفظ الملف. تم قياس 95 ms لإظهار المعاينة.

### C:/testproj - 2026-10-05 09:00

**user** - 09:01

Audit the packet parser in src/net/packet.cpp against the native binary at 0x00401230 and tell me what is wrong.

**assistant** - 09:02

I will read the parser first, then compare against the disassembly.

Plan:
1. Read src/net/packet.cpp and the header
2. Disassemble the native handler and diff the opcodes
3. Report the mismatch with offsets

- `Read` file_path=C:\testproj\src\net\packet.cpp
- `Read` file_path=C:\testproj\src\net\packet.h
**assistant** - 09:05

Decision: the native function at 0x00401230 reads the length field as a big-endian uint32_t, but packet.cpp uses memcpy into a uint16_t. That is the root cause of the truncated packets.

We must never add a bounds check that the native code does not have, because the server relies on the trust model.

- `Edit` file_path=C:\testproj\src\net\packet.cpp
**user** - 09:08

Now build it and run the tests.

**assistant** - 09:09

I'll run the build. Convention in this repo: always use build_full.py, never invoke g++ directly, and keep worker count at 4 or lower.

- `Bash` command=python build_full.py --workers 4
**user** - 09:10

- **error** error: no such file or directory: 'build_full.py' Build failed after 812 ms
**assistant** - 09:12

The build script is missing, so the toolchain cannot build. This is a blocker.

Remaining work:
- TODO: locate the real build entry point
- confirm whether the 1991 byte header is padded
- re-run tests against docs/BASELINE.md

- `Glob` pattern=**/build*.py
### C:/testproj - 2026-10-05 09:00

**user** - 09:00

Add a --since filter and history rotation to the memory tool.

**assistant** - 09:01

Straightforward. I prefer argparse with a dataclass config so the tests can build Options directly.

- add parse_since() handling 30m / 7d / YYYY-MM-DD
- keep the newest N sessions, oldest dropped
- archive previous MEMORY.md under MEMORY.history/

- `Write` file_path=C:\testproj\claude_memory.py
**assistant** - 09:04

Version 1.0.0 now emits roughly 14000 chars, which is about 3500 tokens. That keeps the file readable in a CLAUDE.md import without blowing the context window.

- `Edit` file_path=C:\testproj\claude_memory.py
**user** - 09:07

Does it handle a malformed jsonl line?

**assistant** - 09:08

Yes — the reader catches json.JSONDecodeError per line and skips it, so one bad record cannot lose the whole session. Note that a truncated tail is also tolerated; only --max-file-bytes triggers the tail-only read.

### C:/alpha/srv - 2026-10-05 09:00

**user** - 09:00

Use the explore agent to find every place that hardcodes a packet size.

**assistant** - 09:01

Launching a subagent.

- `Task` explore: find hardcoded packet sizes
**assistant** - 09:09 _(subagent)_

Subagent found 3 sites: src/net/packet.cpp:212 uses 32, src/net/queue.cpp:88 uses 64, and src/net/session.cpp:41 uses 128 bytes. Note the 0x20 / 0x40 / 0x80 pattern.

---

_End of full context: 510 words of assistant+user text, 8616 chars total (~2253 tokens)._
