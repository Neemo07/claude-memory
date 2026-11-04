#!/usr/bin/env python3
"""Generate a FAKE Claude Code transcript tree to test claude_memory.py offline.

All values are synthetic (loopback paths, example.invalid hosts, made-up numbers).
Usage: python make_fixture.py <dest-root>
"""
import json
import sys
import datetime as dt
from pathlib import Path

T0 = dt.datetime(2026, 10, 5, 9, 0, 0, tzinfo=dt.timezone.utc)


def ts(minutes):
    return (T0 + dt.timedelta(minutes=minutes)).strftime("%Y-%m-%dT%H:%M:%S.000Z")


def user(minutes, sid, cwd, text, uuid):
    return {"parentUuid": None, "isSidechain": False, "userType": "external", "cwd": cwd,
            "sessionId": sid, "version": "2.0.14", "gitBranch": "main", "type": "user",
            "message": {"role": "user", "content": text}, "uuid": uuid,
            "timestamp": ts(minutes)}


def meta(minutes, sid, cwd, text, uuid):
    rec = user(minutes, sid, cwd, text, uuid)
    rec["isMeta"] = True
    return rec


def assistant(minutes, sid, cwd, blocks, uuid, model="claude-sonnet-4-5", sidechain=False):
    return {"parentUuid": uuid, "isSidechain": sidechain, "userType": "external", "cwd": cwd,
            "sessionId": sid, "version": "2.0.14", "gitBranch": "main", "type": "assistant",
            "message": {"id": f"msg_{uuid}", "type": "message", "role": "assistant",
                        "model": model,
                        "content": blocks,
                        "stop_reason": "end_turn",
                        "usage": {"input_tokens": 100, "output_tokens": 50}},
            "uuid": f"{uuid}a", "requestId": f"req_{uuid}", "timestamp": ts(minutes)}


def text(t):
    return {"type": "text", "text": t}


def tool(name, tid, inp):
    return {"type": "tool_use", "id": f"toolu_{tid}", "name": name, "input": inp}


def tool_result(tid, body, is_error=False):
    return {"type": "tool_result", "tool_use_id": f"toolu_{tid}",
            "content": [{"type": "text", "text": body}], "is_error": is_error}


def result(minutes, sid, cwd, tid, body, uuid, is_error=False):
    return {"parentUuid": uuid, "isSidechain": False, "cwd": cwd, "sessionId": sid,
            "type": "user",
            "message": {"role": "user", "content": [tool_result(tid, body, is_error)]},
            "toolUseResult": body, "uuid": f"{uuid}r", "timestamp": ts(minutes)}


def summary(minutes, sid, cwd, text_, uuid):
    return {"type": "summary", "summary": text_, "leafUuid": uuid, "timestamp": ts(minutes)}


def build_session_one(cwd, slug):
    sid = "11111111-2222-3333-4444-555555555555"
    out = []
    out.append(meta(0, sid, cwd, "<system-reminder>injected context, must be skipped</system-reminder>", "a000"))
    out.append(user(1, sid, cwd, "Audit the packet parser in src/net/packet.cpp against the native binary at 0x00401230 and tell me what is wrong.", "a001"))
    out.append(assistant(2, sid, cwd, [
        text("I will read the parser first, then compare against the disassembly.\n\n"
             "Plan:\n"
             "1. Read src/net/packet.cpp and the header\n"
             "2. Disassemble the native handler and diff the opcodes\n"
             "3. Report the mismatch with offsets"),
        tool("Read", "t01", {"file_path": str(Path(cwd) / "src/net/packet.cpp")}),
        tool("Read", "t02", {"file_path": str(Path(cwd) / "src/net/packet.h")}),
    ], "a002"))
    out.append(result(3, sid, cwd, "t01", "1\t#include \"net/packet.h\"\n2\t...", "a002"))
    out.append(result(3, sid, cwd, "t02", "1\t#pragma once\n2\tstruct Packet {", "a002"))
    out.append(assistant(5, sid, cwd, [
        text("Decision: the native function at 0x00401230 reads the length field as a big-endian "
             "uint32_t, but packet.cpp uses memcpy into a uint16_t. That is the root cause of the "
             "truncated packets.\n\n"
             "We must never add a bounds check that the native code does not have, because the "
             "server relies on the trust model."),
        tool("Edit", "t03", {"file_path": str(Path(cwd) / "src/net/packet.cpp"),
                             "old_string": "uint16_t", "new_string": "uint32_t"}),
    ], "a003"))
    out.append(result(6, sid, cwd, "t03", "The file has been updated.", "a003"))
    out.append(user(8, sid, cwd, "Now build it and run the tests.", "a004"))
    out.append(assistant(9, sid, cwd, [
        text("I'll run the build. Convention in this repo: always use build_full.py, never invoke "
             "g++ directly, and keep worker count at 4 or lower."),
        tool("Bash", "t04", {"command": "python build_full.py --workers 4", "description": "Build the project"}),
    ], "a005"))
    out.append(result(10, sid, cwd, "t04",
                      "error: no such file or directory: 'build_full.py'\nBuild failed after 812 ms",
                      "a005", is_error=True))
    out.append(assistant(12, sid, cwd, [
        text("The build script is missing, so the toolchain cannot build. This is a blocker.\n\n"
             "Remaining work:\n"
             "- TODO: locate the real build entry point\n"
             "- confirm whether the 1991 byte header is padded\n"
             "- re-run tests against docs/BASELINE.md"),
        tool("Glob", "t05", {"pattern": "**/build*.py"}),
    ], "a006"))
    out.append(result(13, sid, cwd, "t05", "No matches found.", "a006"))
    out.append(summary(14, sid, cwd,
                       "Audited packet.cpp: endianness/length bug at 0x00401230, patched to uint32_t. "
                       "Build blocked: build_full.py absent. Remaining: header padding check.", "a006"))
    return sid, out


def build_session_two(cwd, slug):
    sid = "99999999-8888-7777-6666-555555555555"
    out = []
    out.append(user(0, sid, cwd, "Add a --since filter and history rotation to the memory tool.", "b001"))
    out.append(assistant(1, sid, cwd, [
        text("Straightforward. I prefer argparse with a dataclass config so the tests can build "
             "Options directly.\n\n"
             "- add parse_since() handling 30m / 7d / YYYY-MM-DD\n"
             "- keep the newest N sessions, oldest dropped\n"
             "- archive previous MEMORY.md under MEMORY.history/"),
        tool("Write", "u01", {"file_path": str(Path(cwd) / "claude_memory.py")}),
    ], "b002"))
    out.append(result(2, sid, cwd, "u01", "File created successfully.", "b002"))
    out.append(assistant(4, sid, cwd, [
        text("Version 1.0.0 now emits roughly 14000 chars, which is about 3500 tokens. "
             "That keeps the file readable in a CLAUDE.md import without blowing the context window."),
        tool("Edit", "u02", {"file_path": str(Path(cwd) / "claude_memory.py"),
                             "old_string": "max_chars: int = 8000", "new_string": "max_chars: int = 14000"}),
    ], "b003"))
    out.append(result(5, sid, cwd, "u02", "applied", "b003"))
    out.append(user(7, sid, cwd, "Does it handle a malformed jsonl line?", "b004"))
    out.append(assistant(8, sid, cwd, [
        text("Yes \u2014 the reader catches json.JSONDecodeError per line and skips it, so one bad "
             "record cannot lose the whole session. Note that a truncated tail is also tolerated; "
             "only --max-file-bytes triggers the tail-only read."),
    ], "b005"))
    out.append(summary(9, sid, cwd, "Added --since/--history to claude_memory.py; verified "
                       "malformed-line tolerance. Default budget 14000 chars.", "b005"))
    return sid, out


def build_sidechain(cwd, slug):
    sid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
    out = []
    out.append(user(0, sid, cwd, "Use the explore agent to find every place that hardcodes a packet size.", "c001"))
    out.append(assistant(1, sid, cwd, [
        text("Launching a subagent."),
        tool("Task", "v01", {"subagent_type": "explore", "description": "find hardcoded packet sizes",
                             "prompt": "grep for fixed sizes in src/"}),
    ], "c002"))
    out.append(assistant(9, sid, cwd, [
        text("Subagent found 3 sites: src/net/packet.cpp:212 uses 32, src/net/queue.cpp:88 uses 64, "
             "and src/net/session.cpp:41 uses 128 bytes. Note the 0x20 / 0x40 / 0x80 pattern."),
    ], "c002", sidechain=True))
    return sid, out


def main():
    dest = Path(sys.argv[1] if len(sys.argv) > 1 else "./_fixture_root")
    made = 0
    for slug, cwd in (("-C-testproj", "C:/testproj"), ("-C-alpha-srv", "C:/alpha/srv")):
        target = dest / "projects" / slug
        target.mkdir(parents=True, exist_ok=True)
        if slug == "-C-testproj":
            _, rows = build_session_one(cwd, slug)
            _, rows2 = build_session_two(cwd, slug)
            rows += [summary(20 + i, r.get("sessionId", ""), cwd, r.get("summary", ""), "z")
                     for i, r in enumerate([])] + rows2
        else:
            _, rows = build_sidechain(cwd, slug)
        # one file per session: split on sessionId changes (summaries inherit current id)
        current_id, buf = None, []
        files = 0
        for rec in rows:
            rid = rec.get("sessionId") or current_id
            if current_id is None:
                current_id = rid
            if rid != current_id and buf:
                (target / f"{current_id}.jsonl").write_text(
                    "\n".join(json.dumps(r) for r in buf) + "\n\n", encoding="utf-8")
                files += 1
                buf, current_id = [], rid
            buf.append(rec)
        if buf:
            (target / f"{current_id}.jsonl").write_text(
                "\n".join(json.dumps(r) for r in buf) + "\n", encoding="utf-8")
            files += 1
        made += files
    # a deliberately corrupt file + an empty one
    bad = dest / "projects" / "-C-testproj" / "corrupt.jsonl"
    bad.write_text('{"type":"user","message":{"role":"user","content":"ok"}}\n'
                   'this is not json\n\n{"broken": \n'
                   '{"type":"summary","summary":"orphan summary"}\n', encoding="utf-8")
    (dest / "projects" / "-C-testproj" / "empty.jsonl").write_text("", encoding="utf-8")
    print(f"fixture written to {dest} ({made} session files + corrupt + empty)")


if __name__ == "__main__":
    main()
