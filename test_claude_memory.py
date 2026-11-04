#!/usr/bin/env python3
"""Tests for claude_memory.py. Stdlib only, offline, synthetic data.

    py -3.12 -m unittest test_claude_memory -v
"""
import io
import json
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import claude_memory as cm  # noqa: E402

CWD = "C:/proj"


def rec_user(text, minutes=0, sid="sid-1", cwd=CWD, meta=False, side=False):
    r = {"type": "user", "cwd": cwd, "sessionId": sid, "gitBranch": "main",
         "version": "2.0.0",
         "timestamp": f"2026-10-05T09:{minutes:02d}:00.000Z",
         "uuid": f"u{minutes}", "isSidechain": side,
         "message": {"role": "user", "content": text}}
    if meta:
        r["isMeta"] = True
    return r


def rec_assistant(blocks, minutes=1, sid="sid-1", cwd=CWD, side=False):
    return {"type": "assistant", "cwd": cwd, "sessionId": sid, "gitBranch": "main",
            "isSidechain": side,
            "timestamp": f"2026-10-05T09:{minutes:02d}:00.000Z",
            "uuid": f"a{minutes}",
            "message": {"role": "assistant", "model": "claude-x",
                        "content": blocks}}


def text_blk(t):
    return {"type": "text", "text": t}


def tool_blk(name, tid, inp):
    return {"type": "tool_use", "id": tid, "name": name, "input": inp}


def result_blk(tid, body, is_error=False):
    return {"type": "tool_result", "tool_use_id": tid,
            "content": [{"type": "text", "text": body}], "is_error": is_error}


class TempRoot:
    """Builds a fake ~/.claude tree of jsonl transcripts."""

    def __init__(self):
        self.dir = Path(tempfile.mkdtemp(prefix="cm-test-"))
        self.projects = self.dir / "projects"
        self.projects.mkdir(parents=True)

    def write(self, name, rows, raw=None):
        slug = self.projects / "proj"
        slug.mkdir(exist_ok=True)
        path = slug / name
        if raw is not None:
            path.write_text(raw, encoding="utf-8")
        else:
            path.write_text("\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")
        return path

    def cleanup(self):
        shutil.rmtree(self.dir, ignore_errors=True)


class TestHelpers(unittest.TestCase):
    def setUp(self):
        cm.QUIET = True

    def test_parse_since_relative_units(self):
        import datetime as dt
        now = dt.datetime.now(dt.timezone.utc)
        for spec, delta in (("30m", dt.timedelta(minutes=30)),
                            ("7d", dt.timedelta(days=7)),
                            ("2h", dt.timedelta(hours=2)),
                            ("1w", dt.timedelta(weeks=1))):
            got = cm.parse_since(spec)
            self.assertAlmostEqual((now - got).total_seconds(), delta.total_seconds(), delta=5,
                                   msg=spec)

    def test_parse_since_dates(self):
        self.assertIsNotNone(cm.parse_since("2026-10-01"))
        self.assertIsNotNone(cm.parse_since("2026-10-01 12:30"))
        self.assertIsNotNone(cm.parse_since("2026-10-01T12:30:00Z"))

    def test_parse_since_rejects_garbage(self):
        with self.assertRaises(Exception):
            cm.parse_since("last tuesday-ish")

    def test_one_line_flattens_and_truncates(self):
        self.assertEqual(cm.one_line("a\n  b\tc  "), "a b c")
        out = cm.one_line("x" * 50, 10)
        self.assertEqual(len(out), 10)
        self.assertTrue(out.endswith("\u2026"))

    def test_sentences_strips_bullets_and_splits_long_lines(self):
        got = cm.sentences("- item one\n* item two", 100)
        self.assertEqual(got, ["item one", "item two"])
        long_line = "A" * 200 + ". " + "B" * 200
        self.assertGreaterEqual(len(cm.sentences(long_line, 100)), 2)

    def test_dedupe_counts_drops(self):
        kept, dropped = cm.dedupe(["Alpha beta", "alpha  beta!", "gamma"], 10)
        self.assertEqual(kept, ["Alpha beta", "gamma"])
        self.assertEqual(dropped, 1)

    def test_dedupe_respects_limit(self):
        kept, _ = cm.dedupe([f"item {i}" for i in range(50)], 5)
        self.assertEqual(len(kept), 5)

    def test_to_ascii_is_identity_on_plain_text(self):
        self.assertEqual(cm.to_ascii("plain ascii 123"), "plain ascii 123")

    def test_to_ascii_downgrades_glyphs(self):
        out = cm.to_ascii("a \u2192 b \u00b7 c \u00d7 d \u2026")
        self.assertTrue(out.isascii(), out)
        self.assertIn("->", out)
        self.assertIn("...", out)

    def test_to_ascii_keeps_arabic_letters(self):
        # regression: --ascii / "ASCII only" is about box glyphs, not about
        # destroying non-Latin content, which used to come out as "?????".
        ar = "\u0642\u0631\u0631\u0646\u0627 \u0627\u0633\u062a\u062e\u062f\u0627\u0645 PySide6 \u0627\u0644\u0622\u0646."
        out = cm.to_ascii(ar)
        self.assertIn("\u0642\u0631\u0631\u0646\u0627", out)
        self.assertNotIn("?", out)

    def test_estimate_tokens_tracks_len_div4_for_english(self):
        # same 4-chars-per-token rate, so it must agree with the old divisor
        # to within the rounding of the final int()
        en = "the quick brown fox jumps over the lazy dog. " * 50
        self.assertLessEqual(abs(cm.estimate_tokens(en) - len(en) // 4), 1)

    def test_estimate_tokens_does_not_undercount_arabic(self):
        # regression: len//4 is calibrated for English and under-reports Arabic
        # by ~2.5x, so an Arabic memory looked far cheaper to import than it is.
        ar = "\u0642\u0631\u0631\u0646\u0627 \u0627\u0633\u062a\u062e\u062f\u0627\u0645 \u0627\u0644\u0645\u0643\u062a\u0628 " * 40
        self.assertGreater(cm.estimate_tokens(ar), len(ar) // 4 * 2)

    def test_estimate_tokens_counts_other_scripts_too(self):
        self.assertGreater(cm.estimate_tokens("\u041f\u0440\u0438\u0432\u0435\u0442 " * 40),
                           len("\u041f\u0440\u0438\u0432\u0435\u0442 " * 40) // 4)
        self.assertGreater(cm.estimate_tokens("\u4f60\u597d\u4e16\u754c " * 40),
                           len("\u4f60\u597d\u4e16\u754c " * 40) // 4)

    def test_estimate_tokens_empty(self):
        self.assertEqual(cm.estimate_tokens(""), 0)

    def test_to_ascii_keeps_other_scripts_but_drops_symbols(self):
        out = cm.to_ascii("\u041f\u0440\u0438\u0432\u0435\u0442 \u4f60\u597d \U0001f600")
        self.assertIn("\u041f\u0440\u0438\u0432\u0435\u0442", out)   # cyrillic kept
        self.assertIn("\u4f60\u597d", out)                          # han kept
        self.assertNotIn("\U0001f600", out)                         # emoji dropped
        self.assertIn("?", out)

    def test_tool_brief_task_prefixes_agent(self):
        got = cm.tool_brief("Task", {"subagent_type": "explore",
                                     "description": "find sizes", "prompt": "x" * 500})
        self.assertTrue(got.startswith("explore: find sizes"))

    def test_tool_brief_file_path(self):
        self.assertEqual(cm.tool_brief("Read", {"file_path": "a/b.cpp"}), "file_path=a/b.cpp")

    def test_tool_brief_survives_non_dict(self):
        self.assertEqual(cm.tool_brief("Weird", None), "")

    def test_norm_key_normalises(self):
        self.assertEqual(cm.norm_key("Decision: the Endianness!"), "decisiontheendianness")

    def test_fmt_cwd_shortens_home_paths(self):
        got = cm.fmt_cwd(str(Path.home() / "work" / "x"), Path.home())
        self.assertTrue(got.startswith("~/"), got)

    def test_fmt_dur(self):
        self.assertEqual(cm.fmt_dur(45), "45s")
        self.assertEqual(cm.fmt_dur(90), "1m 30s")
        self.assertEqual(cm.fmt_dur(3700), "1h 1m")

    def test_parse_ts_variants(self):
        self.assertIsNotNone(cm.parse_ts("2026-10-05T09:00:00.000Z"))
        self.assertIsNotNone(cm.parse_ts("2026-10-05 09:00:00"))
        self.assertIsNone(cm.parse_ts("not a date"))
        self.assertIsNone(cm.parse_ts(None))


class TestBudget(unittest.TestCase):
    def test_budget_trims_and_counts(self):
        b = cm.Budget(500)
        taken = b.take(["x" * 100] * 10)
        self.assertLessEqual(b.used, 500)
        self.assertLess(len(taken), 10)
        self.assertGreater(b.trimmed, 0)

    def test_budget_raises_absurd_floor_with_warning(self):
        cm.QUIET = False
        try:
            buf = io.StringIO()
            with redirect_stderr(buf):
                b = cm.Budget(10)
            self.assertEqual(b.total, cm.Budget.FLOOR)
            self.assertEqual(b.requested, 10)
            self.assertIn("floor", buf.getvalue())
        finally:
            cm.QUIET = True


class TestParsing(unittest.TestCase):
    def setUp(self):
        self.tmp = TempRoot()

    def tearDown(self):
        self.tmp.cleanup()

    def parse(self, rows, **kw):
        path = self.tmp.write("s1.jsonl", rows)
        opts = cm.Options(root=self.tmp.dir, **kw)
        return cm.parse_session(path, opts)

    def test_basic_turn_counts_and_metadata(self):
        s = self.parse([
            rec_user("do the thing"),
            rec_assistant([text_blk("Plan:\n- one\n- two"),
                           tool_blk("Read", "t1", {"file_path": "a.cpp"})]),
            {"type": "user", "sessionId": "sid-1", "cwd": CWD,
             "timestamp": "2026-10-05T09:03:00.000Z",
             "message": {"role": "user",
                         "content": [result_blk("t1", "file body")]}},
        ])
        self.assertEqual(s.sid, "sid-1")
        self.assertEqual(s.cwd, CWD)
        self.assertEqual(s.branch, "main")
        # the third record is a plain tool_result: no memory value, dropped
        self.assertEqual(len(s.turns), 2)
        self.assertEqual(s.skipped, 1)
        self.assertEqual(len(s.tool_calls()), 1)
        self.assertEqual(s.model, "claude-x")
        self.assertEqual(s.duration, 180)

    def test_is_meta_records_are_skipped(self):
        s = self.parse([rec_user("<system-reminder>noise</system-reminder>", meta=True),
                        rec_user("real question")])
        self.assertEqual(s.prompts(), ["real question"])
        self.assertEqual(s.skipped, 1)

    def test_sidechain_drop_removes_turns(self):
        rows = [rec_user("q"),
                rec_assistant([text_blk("sub finding")], minutes=2, side=True)]
        kept = self.parse(rows)
        dropped = self.parse(rows, sidechains="drop")
        self.assertEqual(len(kept.turns), 2)
        self.assertEqual(len(dropped.turns), 1)

    def test_thinking_blocks_excluded_by_default(self):
        rows = [rec_assistant([{"type": "thinking", "thinking": "secret reasoning"}])]
        self.assertEqual(self.parse(rows).assistant_text(), [])
        got = self.parse(rows, include_thinking=True)
        self.assertIn("secret reasoning", "".join(got.assistant_text()))

    def test_tool_errors_captured(self):
        rows = [{"type": "user", "sessionId": "sid-1",
                 "message": {"role": "user",
                             "content": [result_blk("t1", "error: boom", is_error=True)]}}]
        s = self.parse(rows)
        self.assertEqual(s.error_lines(), ["error: boom"])

    def test_malformed_lines_are_skipped_not_fatal(self):
        raw = (json.dumps(rec_user("first")) + "\n"
               "this is not json\n\n"
               '{"truncated": \n'
               + json.dumps(rec_user("second")) + "\n")
        path = self.tmp.write("bad.jsonl", None, raw=raw)
        s = cm.parse_session(path, cm.Options(root=self.tmp.dir))
        self.assertEqual(s.prompts(), ["first", "second"])

    def test_summary_records_collected(self):
        s = self.parse([{"type": "summary", "summary": "compactor said this"}])
        self.assertEqual(s.summaries, ["compactor said this"])

    def test_empty_content_records_do_not_become_turns(self):
        s = self.parse([{"type": "assistant", "sessionId": "x",
                         "message": {"role": "assistant", "content": []}}])
        self.assertEqual(s.turns, [])

    def test_max_file_bytes_reads_tail(self):
        rows = [rec_user(f"prompt number {i} " + "z" * 200, minutes=i % 60) for i in range(200)]
        path = self.tmp.write("big.jsonl", rows)
        full = cm.parse_session(path, cm.Options(root=self.tmp.dir))
        tail = cm.parse_session(path, cm.Options(root=self.tmp.dir, max_file_bytes=20000))
        self.assertTrue(tail.truncated_file)
        self.assertLess(len(tail.turns), len(full.turns))

    def test_tool_result_string_content_form(self):
        rows = [{"type": "user", "sessionId": "s",
                 "message": {"role": "user",
                             "content": [{"type": "tool_result", "tool_use_id": "t",
                                          "content": "plain string error", "is_error": True}]}}]
        s = self.parse(rows)
        self.assertEqual(s.error_lines(), ["plain string error"])


class TestRendering(unittest.TestCase):
    def setUp(self):
        self.tmp = TempRoot()
        self.rows = [
            rec_user("Audit the parser in src/net/packet.cpp against 0x00401230."),
            rec_assistant([
                text_blk("Plan:\n- Read the header\n- Diff the opcodes"),
                tool_blk("Read", "t1", {"file_path": "C:/proj/src/net/packet.cpp"}),
                tool_blk("Read", "t0", {"file_path": "C:/proj/src/net/packet.h"}),
                tool_blk("Bash", "t2", {"command": "build_all.py --workers 4"}),
            ]),
            rec_assistant([
                text_blk("Decision: the native code reads a big-endian uint32_t at 0x00401230, "
                         "which is the root cause of the truncation."),
                tool_blk("Edit", "t3", {"file_path": "C:/proj/src/net/packet.cpp",
                                        "old_string": "u16", "new_string": "u32"}),
            ], minutes=5),
            rec_assistant([
                text_blk("We must never add bounds checks the native code lacks. "
                         "Convention: always use build_all.py, never g++ directly."),
                tool_blk("Task", "t4", {"subagent_type": "explore", "description": "sweep"}),
            ], minutes=8),
            rec_assistant([
                text_blk("The header is 1991 bytes and the build failed after 812 ms.\n"
                         "Remaining work:\n- TODO: locate the real build entry point"),
                tool_blk("Bash", "t5", {"command": "build_all.py --workers 8"}),
            ], minutes=11),
            {"type": "user", "sessionId": "sid-1", "cwd": CWD,
             "timestamp": "2026-10-05T09:12:00.000Z",
             "message": {"role": "user",
                         "content": [result_blk("t5", "error: missing entry point",
                                                is_error=True)]}},
        ]
        self.path = self.tmp.write("s1.jsonl", self.rows)
        self.opts = cm.Options(root=self.tmp.dir)
        self.session = cm.parse_session(self.path, self.opts)

    def tearDown(self):
        self.tmp.cleanup()

    def mem(self, **kw):
        opts = cm.Options(root=self.tmp.dir, **{**dict(), **kw})
        return cm.build_memory([self.session], opts)

    def test_all_expected_sections_present(self):
        out = self.mem()
        for heading in ("## Sessions", "## What was asked", "## Decisions taken",
                        "## Constraints and must-nots", "## Preferences and conventions",
                        "## Files changed", "## Commands run", "## Subagents used",
                        "## Problems", "## Open items / TODOs",
                        "## Most recent plan or checklist", "## Concrete identifiers"):
            self.assertIn(heading, out, heading)

    def test_key_facts_survive(self):
        out = self.mem()
        self.assertIn("0x00401230", out)
        self.assertIn("uint32_t", out)
        self.assertIn("1991 bytes", out)
        self.assertIn("812 ms", out)
        self.assertIn("missing entry point", out)
        self.assertIn("packet.cpp", out)

    def test_no_line_duplicated_across_sections(self):
        """A claim rendered in an earlier section must not repeat in a later one."""
        # The verbatim recent exchange and the plan checklist legitimately restate
        # text that the extractor also lists, so they take no part in the check.
        verbatim = ("plan or checklist", "Most recent exchange")
        out = self.mem()
        sections, current = {}, None
        for line in out.splitlines():
            if line.startswith("## "):
                current = line.strip()
                sections[current] = set()
            elif current and line.startswith("- ") and not any(v in current for v in verbatim):
                sections[current].add(cm.norm_key(line[2:]))
        seen, dupes = set(), []
        for name, keys in sections.items():
            for key in keys:
                if key in seen:
                    dupes.append((name, key))
                seen.add(key)
        self.assertEqual(dupes, [], f"duplicate bullets across sections: {dupes}")

    def test_preferences_section_wins_over_broad_constraints(self):
        """A convention line must not be swallowed by the "always/never" patterns."""
        out = self.mem()
        prefs = out.split("## Preferences and conventions")[1].split("\n## ")[0]
        self.assertIn("always use build_all.py", prefs)
        self.assertIn("## Constraints and must-nots", out)

    def test_edited_files_are_not_listed_twice(self):
        out = self.mem()
        read_sec = out.split("## Files read / searched (top)")[1].split("\n## ")[0]
        self.assertIn("packet.h", read_sec)
        self.assertNotIn("packet.cpp", read_sec)

    def test_one_sentence_is_filed_under_two_sections(self):
        """A line holding both a rule and a convention must not be lost to one."""
        out = self.mem()
        self.assertIn("## Preferences and conventions", out)
        self.assertIn("## Constraints and must-nots", out)
        self.assertIn("We must never add bounds checks", out)
        self.assertIn("always use build_all.py", out)

    def test_prompt_echo_is_not_reported_as_decision(self):
        out = self.mem()
        decisions = out.split("## Decisions taken")[1].split("##")[0]
        self.assertNotIn("Audit the parser in src", decisions)

    def test_acknowledgements_dropped(self):
        self.tmp.write("s2.jsonl", [
            rec_user("is it safe?"),
            rec_assistant([text_blk("Yes, it is safe to run the tests concurrently.")]),
        ], )
        s = cm.parse_session(self.tmp.projects / "proj" / "s2.jsonl", self.opts)
        out = cm.build_memory([s], self.opts)
        constraints = out.split("## Constraints")[1].split("##")[0] if "## Constraints" in out else ""
        self.assertNotIn("Yes, it is safe", constraints)

    def test_budget_is_respected_and_reported(self):
        out = self.mem(max_chars=600)
        self.assertLess(len(out), 4000)
        self.assertIn("Trimmed", out)

    def test_each_section_is_rendered_exactly_once(self):
        """Guards against a section list being rendered twice."""
        out = self.mem()
        headings = [ln for ln in out.splitlines() if ln.startswith("## ")]
        self.assertTrue(headings)
        dupes = [h for h in set(headings) if headings.count(h) > 1]
        self.assertEqual(dupes, [], f"section rendered more than once: {dupes}")

    def test_sections_appear_in_priority_order(self):
        out = self.mem()
        order = ["## What was asked", "## Decisions taken",
                 "## Preferences and conventions", "## Constraints and must-nots",
                 "## Files changed", "## Problems", "## Open items / TODOs",
                 "## Most recent plan", "## Concrete identifiers"]
        positions = [out.index(h) for h in order if h in out]
        self.assertEqual(positions, sorted(positions), "sections out of priority order")

    def test_static_text_counts_against_the_budget(self):
        """Header/tables are not trimmed, so they must still squeeze the prose."""
        tight = self.mem(max_chars=600)
        loose = self.mem(max_chars=20000)
        self.assertLess(len(tight), len(loose))

    def test_output_is_deterministic(self):
        self.assertEqual(self.mem(), self.mem())

    def test_claude_md_format_wraps_in_markers(self):
        out = self.mem(fmt="claude-md")
        self.assertTrue(out.startswith(cm.BEGIN))
        self.assertTrue(out.rstrip().endswith(cm.END))

    def test_transcript_mode_contains_roles_and_tools(self):
        out = cm.build_transcript([self.session], self.opts)
        self.assertIn("### assistant", out)
        self.assertIn("`Read`", out)
        self.assertIn("cwd:", out)

    def test_json_mode_is_valid_and_complete(self):
        data = json.loads(cm.build_json([self.session]))
        self.assertEqual(data["sessions"][0]["sid"] if "sid" in data["sessions"][0]
                         else data["sessions"][0]["id"], "sid-1")
        self.assertEqual(len(data["sessions"][0]["messages"]), len(self.session.turns))
        self.assertEqual(len(data["sessions"][0]["prompts"]), 1)

    def test_ascii_flag_output_is_pure_ascii(self):
        out = self.mem(ascii_only=False)
        self.assertFalse(out.isascii())
        self.assertTrue(cm.to_ascii(out).isascii())


class TestSelection(unittest.TestCase):
    def setUp(self):
        self.tmp = TempRoot()
        self.opts = cm.Options(root=self.tmp.dir)

    def tearDown(self):
        self.tmp.cleanup()

    def make(self, name, sid, cwd, day, minutes=0):
        rows = [dict(rec_user(f"question in {sid}", minutes=minutes), sessionId=sid, cwd=cwd,
                     timestamp=f"2026-10-{day:02d}T09:00:00.000Z")]
        return cm.parse_session(self.tmp.write(name, rows), self.opts)

    def test_sessions_limit_keeps_newest(self):
        a = self.make("a.jsonl", "a", CWD, 1)
        b = self.make("b.jsonl", "b", CWD, 2)
        c = self.make("c.jsonl", "c", CWD, 3)
        got = cm.select([a, b, c], cm.Options(sessions=2))
        self.assertEqual([s.sid for s in got], ["b", "c"])

    def test_cwd_filter(self):
        a = self.make("a.jsonl", "a", "C:/alpha", 1)
        b = self.make("b.jsonl", "b", "C:/beta", 2)
        got = cm.select([a, b], cm.Options(cwd="alpha"))
        self.assertEqual([s.sid for s in got], ["a"])

    def test_branch_filter(self):
        a = self.make("a.jsonl", "a", CWD, 1)
        b = self.make("b.jsonl", "b", CWD, 2)
        b.branch = "feature/x"
        got = cm.select([a, b], cm.Options(branch="feature/x"))
        self.assertEqual([s.sid for s in got], ["b"])

    def test_since_filter_excludes_old(self):
        import datetime as dt
        a = self.make("a.jsonl", "a", CWD, 1)
        b = self.make("b.jsonl", "b", CWD, 5)
        cutoff = dt.datetime(2026, 10, 4, tzinfo=dt.timezone.utc)
        got = cm.select([a, b], cm.Options(since=cutoff))
        self.assertEqual([s.sid for s in got], ["b"])

    def test_since_filter_keeps_sessions_without_timestamps(self):
        # anchor is None -> kept. This is why an mtime pre-filter is only
        # ever opt-in: it cannot see a session that carries no timestamp.
        import datetime as dt
        a = self.make("a.jsonl", "a", CWD, 1)
        a.start = a.end = None
        b = self.make("b.jsonl", "b", CWD, 1)
        cutoff = dt.datetime(2026, 10, 4, tzinfo=dt.timezone.utc)
        got = cm.select([a, b], cm.Options(since=cutoff))
        self.assertIn("a", [s.sid for s in got])

    def test_here_filter_uses_current_directory(self):
        import os
        here = str(Path(os.getcwd()))
        a = self.make("a.jsonl", "a", here, 1)
        b = self.make("b.jsonl", "b", "D:/somewhere-else", 2)
        got = cm.select([a, b], cm.Options(here=True))
        self.assertEqual([s.sid for s in got], ["a"])


class TestShareParsing(unittest.TestCase):
    """Offline tests for the claude.ai share importer (no network)."""

    UUID = "a683405f-3f2d-4050-8b77-1c4aef7ce550"

    def payload(self, messages, **extra):
        base = {"uuid": self.UUID, "conversation_uuid": self.UUID,
                "created_at": "2026-10-06T08:00:00.000000Z",
                "updated_at": "2026-10-06T09:00:00.000000Z",
                "created_by": "human", "creator": {"full_name": "Someone", "uuid": "x"},
                "is_public": True, "snapshot_name": "A shared chat title",
                "up_to_date": True, "project_uuid": None, "working_documents": []}
        base.update(extra)
        base["chat_messages"] = messages
        return base

    @staticmethod
    def msg(index, sender, blocks, parent=None, uuid=None, **extra):
        m = {"uuid": uuid or f"m{index}", "parent_message_uuid": parent,
             "sender": sender, "index": index, "created_at": f"2026-10-06T08:{index:02d}:00.000000Z",
             "content": blocks, "text": "", "attachments": [], "files": [],
             "stop_reason": None, "compaction_summary": None, "truncated": False}
        m.update(extra)
        return m

    def local_session(self, prompt="local question"):
        """A minimal in-memory local session (no file IO needed here)."""
        sess = cm.Session(path=Path("local.jsonl"), sid="local-1", cwd="C:/proj",
                          branch="main")
        sess.start = cm.parse_ts("2026-10-05T09:00:00Z")
        sess.end = cm.parse_ts("2026-10-05T09:05:00Z")
        sess.turns.append(cm.Turn(role="user", text=prompt, ts=sess.start))
        return sess

    def test_share_uuid_accepts_url_bare_uuid_and_rejects_junk(self):
        self.assertEqual(cm.share_uuid(f"https://claude.ai/share/{self.UUID}"), self.UUID)
        self.assertEqual(cm.share_uuid(f"http://www.claude.ai/share/{self.UUID}?src=x"), self.UUID)
        self.assertEqual(cm.share_uuid(self.UUID), self.UUID)
        self.assertEqual(cm.share_uuid(f"<https://claude.ai/share/{self.UUID}>"), self.UUID)
        for bad in ("", "https://evil.example/share/abc", "https://claude.ai/chat/123",
                    "ftp://claude.ai/share/xyz"):
            with self.assertRaises(cm.ShareError, msg=bad):
                cm.share_uuid(bad)

    def test_parses_text_tools_and_roles(self):
        p = self.payload([
            self.msg(0, "human", [{"type": "text", "text": "Explain the build failure.",
                                   "citations": [], "flags": {}}]),
            self.msg(1, "assistant", [
                {"type": "text", "text": "Decision: use build_all.py, never g++ directly."},
                {"type": "tool_use", "name": "bash_tool", "id": "t1",
                 "input": {"command": "build_all.py --workers 4", "timeout": 100}},
            ], parent="m0"),
            self.msg(2, "assistant", [
                {"type": "tool_result", "tool_use_id": "t1", "name": "bash_tool",
                 "is_error": True, "content": [{"type": "text", "text": "error: no entry point"}]},
            ], parent="m1"),
            self.msg(3, "assistant", [
                {"type": "text", "text": "We must never change the native struct layout."},
            ], parent="m2"),
        ])
        s = cm.parse_share_payload(p)
        self.assertEqual([t.role for t in s.turns], ["user", "assistant", "assistant", "assistant"])
        self.assertEqual(s.turns[0].text, "Explain the build failure.")
        # claude.ai tool names are mapped onto familiar ones
        self.assertEqual([n for n, _ in s.turns[1].tools], ["Bash"])
        self.assertIn("build_all.py --workers 4", s.turns[1].tools[0][1])
        self.assertEqual(s.error_lines(), ["error: no entry point"])
        self.assertEqual(s.branch, "shared-link")
        self.assertIn("A shared chat title", " ".join(s.summaries))
        self.assertEqual(s.model, "claude.ai")

    def test_present_files_list_input_is_summarised(self):
        p = self.payload([
            self.msg(0, "human", [{"type": "text", "text": "here are the files"}]),
            self.msg(1, "assistant", [
                {"type": "tool_use", "name": "present_files", "id": "t1",
                 "input": {"filepaths": ["src/a.cpp", "src/b.cpp", "src/c.cpp", "src/d.cpp",
                                         "src/e.cpp"]}},
            ], parent="m0"),
        ])
        s = cm.parse_share_payload(p)
        brief = s.turns[1].tools[0][1]
        self.assertIn("src/a.cpp", brief)
        self.assertIn("(+1)", brief)
        self.assertLess(len(brief), 200)

    def test_hidden_blocks_are_skipped(self):
        p = self.payload([
            self.msg(0, "human", [{"type": "text", "text": "hi"}]),
            self.msg(1, "assistant", [
                {"type": "text", "text": "visible answer"},
                {"type": "tool_use", "name": "bash_tool", "id": "t1", "hidden_in_chat": True,
                 "input": {"command": "secret-internal-command"}},
            ], parent="m0"),
        ])
        s = cm.parse_share_payload(p)
        self.assertIn("visible answer", s.turns[1].text)
        self.assertEqual(s.turns[1].tools, [])
        self.assertNotIn("secret-internal-command", cm.build_memory([s], cm.Options()))

    def test_thinking_excluded_by_default(self):
        p = self.payload([
            self.msg(0, "human", [{"type": "text", "text": "q"}]),
            self.msg(1, "assistant", [
                {"type": "thinking", "thinking": "internal chain of thought"},
                {"type": "text", "text": "answer"},
            ], parent="m0"),
        ])
        self.assertNotIn("chain of thought", cm.parse_share_payload(p).turns[1].text)
        got = cm.parse_share_payload(p, include_thinking=True)
        self.assertIn("chain of thought", got.turns[1].text)

    def test_branch_follows_deepest_leaf_not_message_order(self):
        """An abandoned sibling branch must not leak into the memory."""
        p = self.payload([
            self.msg(0, "human", [{"type": "text", "text": "root question"}]),
            self.msg(1, "assistant", [{"type": "text", "text": "answer on main branch"}],
                     parent="m0"),
            self.msg(2, "assistant", [{"type": "text", "text": "ABANDONED side branch"}],
                     parent="m0"),
            self.msg(3, "assistant", [{"type": "text", "text": "follow up on main"}],
                     parent="m1"),
            self.msg(4, "assistant", [{"type": "text", "text": "latest main answer"}],
                     parent="m3"),
        ])
        s = cm.parse_share_payload(p)
        blob = "\n".join(t.text for t in s.turns)
        self.assertIn("latest main answer", blob)
        self.assertNotIn("ABANDONED", blob)
        self.assertEqual(s.skipped, 1)

    def test_compaction_summary_imported(self):
        p = self.payload([
            self.msg(0, "human", [{"type": "text", "text": "go"}]),
            self.msg(1, "assistant", [{"type": "text", "text": "ok"}], parent="m0",
                     compaction_summary={"summary": "Earlier: chose X over Y"}),
        ])
        s = cm.parse_share_payload(p)
        self.assertTrue(any("chose X over Y" in x for x in s.summaries))

    def test_legacy_flat_text_field_supported(self):
        m = self.msg(0, "human", [])
        m["text"] = "flat legacy prompt"
        p = self.payload([m])
        s = cm.parse_share_payload(p)
        self.assertEqual(s.turns[0].text, "flat legacy prompt")

    def test_share_result_text_shapes(self):
        self.assertEqual(cm.share_result_text("plain"), "plain")
        self.assertEqual(cm.share_result_text([{"type": "text", "text": "block"}]), "block")
        self.assertEqual(cm.share_result_text({"stdout": "out", "stderr": "err"}), "out")
        self.assertEqual(cm.share_result_text(None), "")

    def test_payload_without_messages_raises_with_guidance(self):
        with self.assertRaises(cm.ShareError) as ctx:
            cm.parse_share_payload({"uuid": "x", "html": "<html>"})
        self.assertIn("chat_snapshots", str(ctx.exception))
        with self.assertRaises(cm.ShareError):
            cm.parse_share_payload({"uuid": "x", "chat_messages": []})

    def test_load_share_file_rejects_saved_html_page(self):
        tmp = Path(tempfile.mkdtemp(prefix="cm-share-"))
        try:
            page = tmp / "share.html"
            page.write_text("<!doctype html><html><body>SPA shell</body></html>", encoding="utf-8")
            with self.assertRaises(cm.ShareError) as ctx:
                cm.load_share_file(page)
            self.assertIn("does not look like JSON", str(ctx.exception))

            broken = tmp / "broken.json"
            broken.write_text("{not json", encoding="utf-8")
            with self.assertRaises(cm.ShareError):
                cm.load_share_file(broken)

            as_list = tmp / "list.json"
            as_list.write_text(json.dumps([self.msg(0, "human",
                                                    [{"type": "text", "text": "hi"}])]),
                               encoding="utf-8")
            s = cm.load_share_file(as_list)
            self.assertEqual(s.turns[0].text, "hi")
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_share_session_renders_into_memory_and_transcript(self):
        p = self.payload([
            self.msg(0, "human", [{"type": "text", "text": "Audit the packet parser please."}]),
            self.msg(1, "assistant", [
                {"type": "text", "text": "Decision: the length field is big-endian uint32_t. "
                                         "That is the root cause."},
                {"type": "tool_use", "name": "bash_tool", "id": "t1",
                 "input": {"command": "build_all.py"}},
            ], parent="m0"),
        ])
        s = cm.parse_share_payload(p)
        mem = cm.build_memory([s], cm.Options())
        self.assertIn("## Decisions taken", mem)
        self.assertIn("Audit the packet parser", mem)
        self.assertIn("shared-link", mem)          # shows in the sessions table
        self.assertNotIn("Someone", mem)            # creator name is not imported
        tr = cm.build_transcript([s], cm.Options())
        self.assertIn("### assistant", tr)
        data = json.loads(cm.build_json([s]))
        self.assertEqual(data["sessions"][0]["branch"], "shared-link")

    def test_shared_chat_is_not_called_a_claude_code_session(self):
        """Regression: the header used to claim local sessions for a share link."""
        shared = cm.parse_share_payload(self.payload([
            self.msg(0, "human", [{"type": "text", "text": "hello"}]),
        ]))
        mem = cm.build_memory([shared], cm.Options())
        self.assertIn("from 1 shared claude.ai chat", mem)
        self.assertNotIn("Claude Code session(s)", mem)
        # a shared chat has no working directory: say so instead of "unknown"
        self.assertIn("(claude.ai shared chat)", mem)
        self.assertNotIn("(unknown cwd)", mem)

    def test_header_mixes_local_and_shared_counts(self):
        local = self.local_session()
        shared = cm.parse_share_payload(self.payload([
            self.msg(0, "human", [{"type": "text", "text": "shared question"}]),
        ]))
        mem = cm.build_memory([local, shared], cm.Options())
        self.assertIn("1 local session and 1 shared claude.ai chat", mem)

    def test_header_uses_singular_for_one_local_session(self):
        mem = cm.build_memory([self.local_session()], cm.Options())
        self.assertIn("from 1 Claude Code session,", mem)

    def test_session_where_variants(self):
        s = cm.Session(path=Path("x.jsonl"))
        self.assertEqual(cm.session_where(s), "(unknown cwd)")
        s.branch = "shared-link"
        self.assertEqual(cm.session_where(s), "(claude.ai shared chat)")
        s.cwd = "C:/proj"
        self.assertIn("proj", cm.session_where(s))

    def test_fetch_share_retries_then_raises_share_error(self):
        import urllib.error

        calls = []
        body = json.dumps(self.payload([
            self.msg(0, "human", [{"type": "text", "text": "ok"}])])).encode()

        class FakeResp:
            headers = {"Content-Type": "application/json"}

            def read(self):
                return body

            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

        def fake_urlopen(req, timeout=None):
            calls.append(getattr(req, "headers", {}))
            if len(calls) < 3:
                raise urllib.error.HTTPError("u", 429, "Too Many Requests", {}, None)
            return FakeResp()

        import unittest.mock as mock
        with mock.patch("urllib.request.urlopen", fake_urlopen), \
             mock.patch("time.sleep", lambda s: None):
            sess_payload = cm.fetch_share(f"https://claude.ai/share/{self.UUID}",
                                          attempts=3)
        self.assertEqual(len(calls), 3)
        sent = {k.lower(): v for k, v in calls[0].items()}
        self.assertTrue(sent["user-agent"].lower().startswith("mozilla/5.0"),
                        sent["user-agent"])
        self.assertEqual(sent["accept"], "application/json")
        self.assertIn("chat_messages", sess_payload)

    def test_fetch_share_gives_actionable_error_when_exhausted(self):
        import urllib.error
        import unittest.mock as mock

        def always_403(req, timeout=None):
            raise urllib.error.HTTPError("u", 403, "Forbidden", {}, None)

        with mock.patch("urllib.request.urlopen", always_403), \
             mock.patch("time.sleep", lambda s: None):
            with self.assertRaises(cm.ShareError) as ctx:
                cm.fetch_share(self.UUID, attempts=2)
        msg = str(ctx.exception)
        self.assertIn("rate-limits", msg)
        self.assertIn("--share-file", msg)


class TestFullContextMemory(unittest.TestCase):
    """--mode full: a memory file that keeps the entire conversation."""

    def setUp(self):
        self.tmp = TempRoot()
        rows = [
            rec_user("please fix the length bug in the parser"),
            rec_assistant([
                text_blk("Plan:\n- read the header\n- diff the opcodes"),
                tool_blk("Read", "f1", {"file_path": "C:/proj/src/net/packet.cpp"}),
                tool_blk("Bash", "f2", {"command": "build_all.py --workers 4"}),
            ]),
            rec_assistant([
                text_blk("Decision: read the length as big-endian uint32_t. "
                         "That is the root cause of the truncation you saw."),
            ], minutes=5),
            {"type": "user", "sessionId": "sid-1", "cwd": "C:/proj",
             "timestamp": "2026-10-05T09:06:00.000Z",
             "message": {"role": "user",
                         "content": [result_blk("f2", "error: missing entry point",
                                                is_error=True)]}},
            rec_assistant([text_blk("Done. Run the tests.")], minutes=8),
        ]
        self.path = self.tmp.write("s1.jsonl", rows)
        self.session = cm.parse_session(self.path, cm.Options(root=self.tmp.dir))

    def tearDown(self):
        self.tmp.cleanup()

    def full(self, **kw):
        return cm.build_full_memory([self.session], cm.Options(root=self.tmp.dir, **kw))

    def test_has_both_the_summary_and_the_whole_conversation(self):
        out = self.full()
        self.assertIn("## Decisions taken", out)          # orientation kept
        self.assertIn("## Full conversation", out)
        self.assertIn("## Sessions", out)

    def test_every_turn_text_appears_verbatim(self):
        out = self.full()
        missing = [t.text[:40] for t in self.session.turns
                   if t.text and t.text[:40] not in out]
        self.assertEqual(missing, [], f"turns dropped from the full memory: {missing}")

    def test_speaker_labels_and_timestamps(self):
        out = self.full()
        self.assertIn("**user**", out)
        self.assertIn("**assistant**", out)
        self.assertTrue(any(line.startswith("**assistant** - 09:0")
                            for line in out.splitlines()),
                        "turns should carry their time")

    def test_tool_calls_listed_but_output_dropped(self):
        out = self.full()
        self.assertIn("- `Read` ", out)
        self.assertIn("- `Bash` ", out)
        self.assertIn("build_all.py --workers 4", out)
        # tool_result bodies are noise and must not bloat the memory
        self.assertNotIn("error: missing entry point" * 2, out)
        self.assertIn("- **error** error: missing entry point", out)

    def test_reports_its_own_size(self):
        out = self.full()
        self.assertIn("End of full context:", out)
        self.assertIn("words of assistant+user text", out)

    def test_full_keeps_what_the_summary_still_drops(self):
        """Full mode is for mid-conversation prose the summary cannot hold."""
        summary = cm.build_memory([self.session], cm.Options(root=self.tmp.dir))
        full = self.full()
        self.assertIn("read the header", full)
        # the summary now quotes the tail verbatim, so pick an early turn instead
        many = cm.Session(path=Path("many.jsonl"), sid="many", cwd="C:/proj")
        many.turns = [
            cm.Turn(role="user", text="first question about the parser"),
            cm.Turn(role="assistant", text="early reply mentioning zebracorn 42"),
            cm.Turn(role="user", text="second question about the layout"),
            cm.Turn(role="assistant", text="final answer about padding"),
        ]
        many_summary = cm.build_memory([many], cm.Options(root=self.tmp.dir))
        self.assertIn("final answer about padding", many_summary)
        self.assertIn("first question about the parser", many_summary)
        self.assertNotIn("zebracorn 42", many_summary)
        self.assertIn("zebracorn 42", cm.build_full_memory(
            [many], cm.Options(root=self.tmp.dir)))

    def test_cli_mode_full(self):
        import io
        from contextlib import redirect_stderr, redirect_stdout
        buf_out, buf_err = io.StringIO(), io.StringIO()
        with redirect_stdout(buf_out), redirect_stderr(buf_err):
            code = cm.main(["--root", str(self.tmp.dir), "--mode", "full"])
        self.assertEqual(code, 0)
        self.assertIn("## Full conversation", buf_out.getvalue())

    def test_claude_md_markers_honoured(self):
        out = self.full(fmt="claude-md")
        self.assertTrue(out.startswith(cm.BEGIN))
        self.assertTrue(out.rstrip().endswith(cm.END))

    def test_deterministic(self):
        self.assertEqual(self.full(), self.full())

    def test_summary_sections_are_still_capped(self):
        """The orientation part must not eat the whole budget."""
        tight = cm.build_full_memory([self.session],
                                     cm.Options(root=self.tmp.dir, max_chars=600))
        self.assertIn("## Full conversation", tight)

    def test_session_with_no_turns_is_handled(self):
        empty = cm.Session(path=Path("empty.jsonl"), sid="empty")
        out = cm.build_full_memory([empty], cm.Options(root=self.tmp.dir))
        self.assertIn("_no turns in this session_", out)

    def test_subagent_turns_are_marked(self):
        sess = cm.parse_session(self.path, cm.Options(root=self.tmp.dir))
        sess.turns.append(cm.Turn(role="assistant", text="subagent finding",
                                  sidechain=True))
        out = cm.build_full_memory([sess], cm.Options(root=self.tmp.dir))
        self.assertIn("subagent finding", out)
        self.assertIn("_(subagent)_", out)

    def test_gui_accepts_the_full_mode(self):
        import gui
        opts = gui.build_options({"mode": "full"})
        self.assertEqual(opts.mode, "full")

    def test_page_offers_the_full_option(self):
        import gui
        self.assertIn('value="full"', gui.PAGE)
        self.assertIn("memory (full context)", gui.PAGE)


class TestRecencyRules(unittest.TestCase):
    """The summary used to favour the OLDEST statements, which made a long chat's
    memory stop short of where the conversation actually ended."""

    def test_dedupe_recent_keeps_the_newest_and_restores_order(self):
        kept, _ = cm.dedupe_recent(["one", "two", "three"], 2)
        self.assertEqual(kept, ["two", "three"])
        kept, _ = cm.dedupe_recent(["a", "b", "a"], 10)
        self.assertEqual(kept, ["b", "a"])          # newest occurrence wins

    def test_dedupe_recent_drops_dupes(self):
        kept, dropped = cm.dedupe_recent(["x", "x", "y"], 10)
        self.assertEqual(kept, ["x", "y"])
        self.assertEqual(dropped, 1)

    def test_clip_middle_keeps_both_ends(self):
        body = "HEAD-MARKER " + ("z" * 500) + " TAIL-MARKER"
        clipped, trimmed = cm.clip_middle(body, 120)
        self.assertTrue(trimmed)
        self.assertIn("HEAD-MARKER", clipped)
        self.assertIn("TAIL-MARKER", clipped)       # conclusions live at the end
        self.assertLessEqual(len(clipped), 160)
        self.assertIn("chars omitted", clipped)

    def test_clip_middle_leaves_short_text_alone(self):
        clipped, trimmed = cm.clip_middle("short", 100)
        self.assertEqual(clipped, "short")
        self.assertFalse(trimmed)

    def test_summary_always_contains_the_final_answer(self):
        sess = cm.Session(path=Path("s.jsonl"), sid="s", cwd="C:/proj")
        sess.turns = [
            cm.Turn(role="user", text="what about the padding?"),
            cm.Turn(role="assistant", text="Padding is 4 bytes and FINAL-CONCLUSION-XY."),
        ]
        out = cm.build_memory([sess], cm.Options())
        self.assertIn("## Most recent exchange", out)
        self.assertIn("FINAL-CONCLUSION-XY", out)

    def test_recent_exchange_holds_every_reply_after_the_last_question(self):
        sess = cm.Session(path=Path("s.jsonl"), sid="s", cwd="C:/proj")
        sess.turns = [
            cm.Turn(role="user", text="first question"),
            cm.Turn(role="assistant", text="old reply about apples"),
            cm.Turn(role="user", text="second question"),
            cm.Turn(role="assistant", text="reply one about pears"),
            cm.Turn(role="user", text=""),           # empty continue, dropped
            cm.Turn(role="assistant", text="reply two about pears"),
        ]
        out = cm.build_memory([sess], cm.Options())
        recent = out.split("## Most recent exchange")[1].split("\n## ")[0]
        self.assertIn("second question", recent)
        self.assertIn("reply one about pears", recent)
        self.assertIn("reply two about pears", recent)
        self.assertNotIn("old reply about apples", recent)

    def test_recent_exchange_labels_are_readable(self):
        sess = cm.Session(path=Path("s.jsonl"), sid="s", cwd="C:/proj")
        sess.turns = [cm.Turn(role="user", text="q"), cm.Turn(role="assistant", text="a")]
        out = cm.build_memory([sess], cm.Options())
        self.assertIn("**user** \u2014 last question", out)
        self.assertIn("**assistant** \u2014 reply since then", out)

    def test_prompt_cap_keeps_the_newest(self):
        sess = cm.Session(path=Path("s.jsonl"), sid="s", cwd="C:/proj")
        sess.turns = [cm.Turn(role="user", text=f"PROMPT-{i}") for i in range(20)]
        sess.turns.append(cm.Turn(role="assistant", text="done"))
        out = cm.build_memory([sess], cm.Options(max_prompts_per_session=3))
        asked = out.split("## What was asked")[1].split("\n## ")[0]
        self.assertIn("PROMPT-19", asked)
        self.assertNotIn("PROMPT-0 ", asked)
        self.assertIn("earlier prompt(s) omitted", asked)

    def test_decisions_prefer_the_newest_when_capped(self):
        sess = cm.Session(path=Path("s.jsonl"), sid="s", cwd="C:/proj")
        sess.turns = [
            cm.Turn(role="user", text="go"),
            cm.Turn(role="assistant", text="Decision: use approach OLD-ONE."),
            cm.Turn(role="user", text="go on"),
            cm.Turn(role="assistant", text="Decision: use approach NEWEST-ONE."),
            cm.Turn(role="user", text="again"),
            cm.Turn(role="assistant", text="Decision: use approach MIDDLE-ONE."),
        ]
        out = cm.build_memory([sess], cm.Options(max_items=1))
        decisions = out.split("## Decisions taken")[1].split("\n## ")[0]
        self.assertIn("MIDDLE-ONE", decisions)
        self.assertNotIn("OLD-ONE", decisions)


class TestCli(unittest.TestCase):
    def setUp(self):
        self.tmp = TempRoot()
        self.rows = [
            rec_user("why is the build failing?"),
            rec_assistant([text_blk("Decision: the toolchain cannot build because "
                                   "build_all.py is absent from the repository."),
                           tool_blk("Bash", "t1", {"command": "build_all.py"})]),
            {"type": "user", "sessionId": "sid-1", "cwd": CWD,
             "timestamp": "2026-10-05T09:05:00.000Z",
             "message": {"role": "user",
                         "content": [result_blk("t1", "error: not found", is_error=True)]}},
        ]
        self.tmp.write("s1.jsonl", self.rows)
        self.out = self.tmp.dir / "out" / "MEMORY.md"

    def tearDown(self):
        self.tmp.cleanup()

    def run_cli(self, *argv):
        buf_out, buf_err = io.StringIO(), io.StringIO()
        with redirect_stdout(buf_out), redirect_stderr(buf_err):
            code = cm.main(["--root", str(self.tmp.dir), *argv])
        return code, buf_out.getvalue(), buf_err.getvalue()

    def test_missing_transcript_dir_exits_2(self):
        code, _, err = self.run_cli("--projects-dir", str(self.tmp.dir / "nope"))
        self.assertEqual(code, 2)
        self.assertIn("no transcript dir", err)

    def test_default_run_prints_memory_to_stdout(self):
        code, out, _ = self.run_cli()
        self.assertEqual(code, 0)
        self.assertIn("## Decisions taken", out)
        self.assertIn("build_all.py", out)

    def test_out_writes_file_and_reports_on_stderr(self):
        code, out, err = self.run_cli("--out", str(self.out))
        self.assertEqual(code, 0)
        self.assertEqual(out, "")
        self.assertTrue(self.out.exists())
        self.assertIn("wrote", err)

    def test_print_and_out_together(self):
        code, out, _ = self.run_cli("--out", str(self.out), "--print")
        self.assertEqual(code, 0)
        self.assertIn("## Sessions", out)
        self.assertTrue(self.out.exists())

    def test_history_rotation_keeps_previous_versions(self):
        self.run_cli("--out", str(self.out), "--history", "2")
        first = self.out.read_text(encoding="utf-8")
        self.run_cli("--out", str(self.out), "--history", "2")
        hist = list((self.out.parent / "MEMORY.history").glob("*.md"))
        self.assertEqual(len(hist), 1)
        self.assertEqual(hist[0].read_text(encoding="utf-8"), first)

    def test_history_prunes_beyond_keep_count(self):
        import time
        for _ in range(4):
            self.run_cli("--out", str(self.out), "--history", "2", "--quiet")
            time.sleep(1.05)  # history file names are second-resolution
        hist = list((self.out.parent / "MEMORY.history").glob("*.md"))
        self.assertLessEqual(len(hist), 2)

    def test_no_match_exits_1_with_hint(self):
        code, _, err = self.run_cli("--cwd", "no-such-project-anywhere")
        self.assertEqual(code, 1)
        self.assertIn("no sessions matched", err)

    def test_list_mode(self):
        code, out, _ = self.run_cli("--list")
        self.assertEqual(code, 0)
        self.assertIn("s1.jsonl", out)
        self.assertIn("| # | file |", out)

    def test_json_mode_to_file_is_parseable(self):
        target = self.tmp.dir / "out" / "chat.json"
        code, _, _ = self.run_cli("--mode", "json", "--out", str(target), "--quiet")
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(target.read_text(encoding="utf-8"))["sessions"][0]["id"],
                         "sid-1")

    def test_transcript_mode_to_file(self):
        target = self.tmp.dir / "out" / "chat.md"
        code, _, _ = self.run_cli("--mode", "transcript", "--out", str(target), "--quiet")
        self.assertEqual(code, 0)
        self.assertIn("### assistant", target.read_text(encoding="utf-8"))

    def test_ascii_flag_avoids_glyph_crash(self):
        code, out, _ = self.run_cli("--ascii")
        self.assertEqual(code, 0)
        self.assertTrue(out.isascii())

    def test_written_file_is_lf_only_utf8(self):
        self.run_cli("--out", str(self.out), "--quiet")
        raw = self.out.read_bytes()
        self.assertNotIn(b"\r\n", raw)
        self.assertTrue(raw.decode("utf-8"))

    def test_source_note_appears_in_header(self):
        code, out, _ = self.run_cli("--source-note", "fixture://unit-test")
        self.assertEqual(code, 0)
        self.assertIn("fixture://unit-test", out)

    def test_fast_is_opt_in_and_does_not_change_default_output(self):
        plain = self.run_cli("--quiet")[1]
        fast = self.run_cli("--fast", "--since", "3650d", "--quiet")[1]
        # --fast is a pre-filter, not a renderer: with a window that covers
        # everything it must reproduce the default output byte for byte.
        self.assertEqual(plain, fast)

    def test_fast_reports_how_many_it_skipped(self):
        # The fixture's records are dated 2026-10-05, but --since filters on the
        # timestamps inside the record while --fast filters on file mtime. Age the
        # files past the window so the pre-filter genuinely drops something.
        import os
        import time
        old = time.time() - 90 * 86400
        aged = 0
        for path in self.tmp.projects.rglob("*.jsonl"):
            os.utime(path, (old, old))
            aged += 1
        self.assertGreater(aged, 0, "fixture produced no transcripts to age")
        # the records are ~9 days old, so a 30d window covers them: only the
        # mtime pre-filter can exclude them, which is exactly what we assert.
        code, _, err = self.run_cli("--fast", "--since", "30d")
        self.assertIn("--fast", err)
        self.assertIn("approximate", err)

    def test_fast_stays_quiet_when_it_skips_nothing(self):
        # files are fresh here, so the pre-filter keeps everything and --fast
        # must not claim it skipped anything.
        code, _, err = self.run_cli("--fast", "--since", "3650d")
        self.assertEqual(code, 0)
        self.assertNotIn("--fast", err)


if __name__ == "__main__":
    unittest.main(verbosity=2)
