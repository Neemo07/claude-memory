#!/usr/bin/env python3
"""Tests for the gui.py layer. No sockets: transport is stubbed, logic is real.

    py -3.12 -m unittest test_gui -v
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
import gui  # noqa: E402
import markdown_preview  # noqa: E402


class FakeRoot:
    def __init__(self):
        self.dir = Path(tempfile.mkdtemp(prefix="cm-gui-"))
        self.projects = self.dir / "projects"
        self.projects.mkdir(parents=True)
        (self.projects / "-C-proj").mkdir()

    def session(self, name, sid, cwd, prompt):
        rows = [
            {"type": "user", "cwd": cwd, "sessionId": sid, "gitBranch": "main",
             "timestamp": "2026-10-05T09:00:00.000Z",
             "message": {"role": "user", "content": prompt}},
            {"type": "assistant", "cwd": cwd, "sessionId": sid, "gitBranch": "main",
             "timestamp": "2026-10-05T09:05:00.000Z",
             "message": {"role": "assistant", "model": "claude-x", "content": [
                 {"type": "text", "text": "Decision: pin the layout to 32 bytes and never "
                                           "change it. That is the root cause."},
                 {"type": "tool_use", "id": "t1", "name": "Edit",
                  "input": {"file_path": cwd + "/a.cpp"}},
             ]}},
        ]
        (self.projects / "-C-proj" / name).write_text(
            "\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")

    def cleanup(self):
        shutil.rmtree(self.dir, ignore_errors=True)


class SharePayload:
    UUID = "a683405f-3f2d-4050-8b77-1c4aef7ce550"

    @classmethod
    def build(cls):
        return {
            "uuid": cls.UUID, "conversation_uuid": cls.UUID,
            "created_at": "2026-10-06T08:00:00.000000Z",
            "updated_at": "2026-10-06T09:00:00.000000Z",
            "snapshot_name": "Shared reasoning chat",
            "chat_messages": [
                {"uuid": "m0", "parent_message_uuid": None, "sender": "human", "index": 0,
                 "created_at": "2026-10-06T08:00:00.000000Z", "text": "",
                 "content": [{"type": "text", "text": "Why does the parser truncate packets?"}]},
                {"uuid": "m1", "parent_message_uuid": "m0", "sender": "assistant", "index": 1,
                 "created_at": "2026-10-06T08:01:00.000000Z", "text": "",
                 "content": [
                     {"type": "text", "text": "Decision: read the length as big-endian uint32_t."},
                     {"type": "tool_use", "id": "t1", "name": "bash_tool",
                      "input": {"command": "build_all.py"}}]},
            ],
        }


class TestBuildOptions(unittest.TestCase):
    def test_maps_ui_payload_to_options(self):
        opts = gui.build_options({
            "mode": "transcript", "fmt": "claude-md", "sessions": 3, "here": True,
            "max_chars": 5000, "max_items": 7, "include_thinking": True,
            "sidechains": "drop", "ascii_only": True, "history": 0,
            "since": "7d", "cwd": "alpha",
        })
        self.assertEqual(opts.mode, "transcript")
        self.assertEqual(opts.fmt, "claude-md")
        self.assertEqual(opts.sessions, 3)
        self.assertTrue(opts.here)
        self.assertEqual(opts.max_chars, 5000)
        self.assertEqual(opts.max_items, 7)
        self.assertTrue(opts.include_thinking)
        self.assertEqual(opts.sidechains, "drop")
        self.assertTrue(opts.ascii_only)
        self.assertEqual(opts.history, 0)
        self.assertEqual(opts.cwd, "alpha")
        self.assertIsNotNone(opts.since)

    def test_defaults_when_ui_sends_nothing(self):
        opts = gui.build_options({})
        self.assertEqual(opts.mode, "memory")
        self.assertEqual(opts.max_chars, 14000)
        self.assertIsNone(opts.since)
        self.assertEqual(opts.sidechains, "keep")

    def test_bad_time_window_is_reported_not_crashed(self):
        with self.assertRaises(ValueError) as ctx:
            gui.build_options({"since": "last tuesday"})
        self.assertIn("30d", str(ctx.exception))

    def test_numeric_fields_tolerate_garbage(self):
        opts = gui.build_options({"max_chars": "abc", "sessions": None, "max_items": ""})
        self.assertEqual(opts.max_chars, 14000)
        self.assertEqual(opts.sessions, 0)
        self.assertEqual(opts.max_items, 14)


class TestMarkdownPreview(unittest.TestCase):
    def test_parses_memory_markdown_shapes(self):
        blocks = markdown_preview.parse_markdown(
            "# Title\n\n"
            "**bold** and *italic* and `code`\n\n"
            "- one\n- two\n\n"
            "| Name | Value |\n"
            "|---|---|\n"
            "| x | 1 |\n\n"
            "> quoted\n\n"
            "```py\nprint('x')\n```"
        )
        kinds = [block["type"] for block in blocks]
        self.assertEqual(kinds, ["heading", "paragraph", "list", "table",
                                 "quote", "code"])
        self.assertEqual(blocks[0]["level"], 1)
        self.assertTrue(blocks[1]["spans"][0]["bold"])
        self.assertTrue(blocks[1]["spans"][2]["italic"])
        self.assertTrue(blocks[1]["spans"][4]["code"])
        self.assertEqual(len(blocks[2]["items"]), 2)
        self.assertEqual(len(blocks[3]["rows"]), 1)
        self.assertEqual(blocks[4]["blocks"][0]["type"], "paragraph")
        self.assertEqual(blocks[5]["text"], "print('x')")

    def test_indented_quote_parses_without_stalling(self):
        blocks = markdown_preview.parse_markdown("   > quoted")
        self.assertEqual(blocks[0]["type"], "quote")
        self.assertEqual(blocks[0]["blocks"][0]["type"], "paragraph")

    def test_ordered_list_keeps_starting_number(self):
        blocks = markdown_preview.parse_markdown("3. first\n4. second")
        self.assertEqual(blocks[0]["start"], 3)
        self.assertEqual(len(blocks[0]["items"]), 2)

    def test_raw_html_stays_plain_text(self):
        blocks = markdown_preview.parse_markdown("<script>alert('x')</script>")
        text = "".join(span["text"] for span in blocks[0]["spans"])
        self.assertEqual(text, "<script>alert('x')</script>")
        self.assertNotIn("html", blocks[0])

    def test_preview_rejects_empty_output(self):
        with self.assertRaises(ValueError):
            gui.do_preview({"text": "  "})

    def test_preview_rejects_json_output(self):
        with self.assertRaises(ValueError) as ctx:
            gui.do_preview({"text": "{}", "mode": "json"})
        self.assertIn("not Markdown", str(ctx.exception))

    def test_preview_route_returns_structured_blocks(self):
        result = gui.dispatch("/api/preview", {"text": "# Hello"})
        self.assertEqual(result["blocks"][0]["type"], "heading")

    def test_links_become_safe_anchors(self):
        blocks = markdown_preview.parse_markdown(
            "[site](https://example.com) [mail](mailto:a@b.c) [bad](javascript:alert(1))")
        spans = blocks[0]["spans"]
        self.assertEqual(spans[0]["href"], "https://example.com")
        self.assertEqual(spans[2]["href"], "mailto:a@b.c")
        bad = [span for span in spans if "bad" in span.get("text", "")]
        self.assertTrue(bad)
        self.assertNotIn("href", bad[0])
        self.assertIn("javascript:", bad[0]["text"])

    def test_strikethrough_and_task_lists_parse(self):
        blocks = markdown_preview.parse_markdown("~~old~~\n\n- [x] done\n- [ ] todo")
        self.assertTrue(blocks[0]["spans"][0]["strike"])
        self.assertTrue(blocks[1]["items"][0]["checked"])
        self.assertFalse(blocks[1]["items"][1]["checked"])

    def test_table_alignment_comes_from_the_separator(self):
        blocks = markdown_preview.parse_markdown(
            "| A | B | C |\n|:---|:---:|---:|\n| 1 | 2 | 3 |")
        self.assertEqual(blocks[0]["align"], ["left", "center", "right"])


class TestOpenMarkdownFile(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="cm-md-"))
        self.file = self.tmp / "NOTES.md"
        self.file.write_text("# Notes\n\n- [x] shipped\n", encoding="utf-8")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_reads_and_parses_the_chosen_file(self):
        got = gui.do_open_markdown({"path": str(self.file)})
        self.assertEqual(got["name"], "NOTES.md")
        self.assertEqual(got["blocks"][0]["type"], "heading")
        self.assertTrue(got["blocks"][1]["items"][0]["checked"])

    def test_native_dialog_result_is_used(self):
        calls = []

        def opener(name, start_dir, file_types):
            calls.append((name, start_dir, file_types))
            return str(self.file)

        got = gui.do_open_markdown({"dir": str(self.tmp)}, open_file=opener)
        self.assertEqual(got["path"], str(self.file))
        self.assertTrue(calls[0][0].endswith(".md"))
        self.assertEqual(calls[0][1], str(self.tmp))
        self.assertTrue(any("Markdown" in t for t in calls[0][2]))

    def test_cancel_returns_no_path(self):
        got = gui.do_open_markdown({}, open_file=lambda n, d, t: None)
        self.assertTrue(got["cancelled"])
        self.assertNotIn("path", got)

    def test_non_markdown_and_empty_files_are_refused(self):
        txt = self.tmp / "notes.txt"
        txt.write_text("# no", encoding="utf-8")
        with self.assertRaises(ValueError):
            gui.do_open_markdown({"path": str(txt)})
        empty = self.tmp / "empty.md"
        empty.write_text("   \n", encoding="utf-8")
        with self.assertRaises(ValueError):
            gui.do_open_markdown({"path": str(empty)})

    def test_missing_file_is_reported(self):
        with self.assertRaises(ValueError) as ctx:
            gui.do_open_markdown({"path": str(self.tmp / "gone.md")})
        self.assertIn("No such file", str(ctx.exception))

    def test_dispatch_reaches_the_file_preview_route(self):
        got = gui.dispatch("/api/open_markdown", {"path": str(self.file)})
        self.assertEqual(got["blocks"][0]["type"], "heading")


class TestBuild(unittest.TestCase):
    def setUp(self):
        self.root = FakeRoot()
        self.root.session("s1.jsonl", "sid-1", "C:/proj",
                          "Audit the parser and fix the length bug please.")
        self.orig_root = cm.DEFAULT_ROOT
        cm.DEFAULT_ROOT = self.root.dir
        self.orig_home = gui.default_out_dir
        gui.default_out_dir = lambda: self.root.dir
        self.out = io.StringIO()
        self.err = io.StringIO()
        self._so, self._se = sys.stdout, sys.stderr
        sys.stdout, sys.stderr = self.out, self.err

    def tearDown(self):
        sys.stdout, sys.stderr = self._so, self._se
        gui.default_out_dir = self.orig_home
        cm.DEFAULT_ROOT = self.orig_root
        cm.QUIET = False
        self.root.cleanup()

    def test_scan_reports_sessions_and_paths(self):
        d = gui.do_scan()
        self.assertEqual(len(d["sessions"]), 1)
        self.assertEqual(d["sessions"][0]["turns"], 2)
        self.assertEqual(d["root"], str(self.root.dir))
        self.assertTrue(d["default_out_dir"])

    def test_scan_survives_empty_root(self):
        shutil.rmtree(self.root.projects)
        d = gui.do_scan()
        self.assertEqual(d["sessions"], [])

    def test_build_local_session(self):
        d = gui.do_build({"src": "local"})
        self.assertIn("## Decisions taken", d["text"])
        self.assertIn("pin the layout to 32 bytes", d["text"])
        self.assertIn("1 session(s)", d["stats"])
        self.assertIn("tokens", d["stats"])

    def test_stats_token_count_is_script_aware(self):
        # the reported token count must come from cm.estimate_tokens, not len//4
        d = gui.do_build({"src": "local"})
        shown = int(d["stats"].split("~")[-1].split("tokens")[0].strip())
        self.assertEqual(shown, cm.estimate_tokens(d["text"]))

    def test_build_modes(self):
        self.assertIn("### assistant", gui.do_build({"src": "local", "mode": "transcript"})["text"])
        data = json.loads(gui.do_build({"src": "local", "mode": "json"})["text"])
        self.assertEqual(data["sessions"][0]["id"], "sid-1")

    def test_build_claude_md_format_adds_markers(self):
        text = gui.do_build({"src": "local", "fmt": "claude-md"})["text"]
        self.assertTrue(text.startswith(cm.BEGIN))
        self.assertTrue(text.rstrip().endswith(cm.END))

    def test_build_ascii_flag(self):
        self.assertTrue(gui.do_build({"src": "local", "ascii_only": True})["text"].isascii())

    def test_build_share_only_requires_a_link(self):
        with self.assertRaises(ValueError) as ctx:
            gui.do_build({"src": "shareonly"})
        self.assertIn("share link", str(ctx.exception))

    def test_build_share_from_file(self):
        path = self.root.dir / "payload.json"
        path.write_text(json.dumps(SharePayload.build()), encoding="utf-8")
        d = gui.do_build({"src": "shareonly", "share_file": str(path)})
        self.assertIn("Why does the parser truncate packets?", d["text"])
        self.assertIn("claude.ai shared chat", d["text"])

    def test_build_share_file_missing_path_is_explained(self):
        with self.assertRaises(ValueError) as ctx:
            gui.do_build({"src": "shareonly",
                          "share_file": str(self.root.dir / "nope.json")})
        self.assertIn("No such file", str(ctx.exception))

    def test_build_share_network_cached_for_offline_rerun(self):
        calls = []

        def fake_fetch(url):
            calls.append(url)
            return SharePayload.build()

        orig = cm.fetch_share
        cm.fetch_share = fake_fetch
        try:
            d = gui.do_build({"src": "shareonly",
                              "share": f"https://claude.ai/share/{SharePayload.UUID}"})
        finally:
            cm.fetch_share = orig
        self.assertEqual(len(calls), 1)
        self.assertIn("big-endian uint32_t", d["text"])
        self.assertIn("claude.ai share link", d["text"])
        cached = list(self.root.dir.glob("claude-share-*.json"))
        self.assertEqual(len(cached), 1, "payload should be cached for offline re-runs")
        # and the cache must be importable without touching the network
        again = gui.do_build({"src": "shareonly", "share_file": str(cached[0])})
        self.assertIn("big-endian uint32_t", again["text"])

    def test_build_merges_local_and_share(self):
        path = self.root.dir / "payload.json"
        path.write_text(json.dumps(SharePayload.build()), encoding="utf-8")
        d = gui.do_build({"src": "both", "share_file": str(path)})
        self.assertIn("2 session(s)", d["stats"])
        self.assertIn("pin the layout to 32 bytes", d["text"])
        self.assertIn("Why does the parser truncate packets?", d["text"])

    def test_build_here_filter_can_exclude_everything(self):
        with self.assertRaises(ValueError) as ctx:
            gui.do_build({"src": "local", "cwd": "no-such-folder"})
        self.assertIn("Nothing to summarise", str(ctx.exception))

    def test_build_is_quiet_on_stderr(self):
        gui.do_build({"src": "local"})
        self.assertEqual(self.err.getvalue(), "")


class TestSafePath(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="cm-path-"))

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def test_accepts_plain_name_in_absolute_dir(self):
        got = gui.safe_out_path({"dir": str(self.dir), "name": "MEMORY.md"})
        self.assertEqual(got, self.dir / "MEMORY.md")

    def test_defaults_to_documents_when_no_dir(self):
        got = gui.safe_out_path({"name": "MEMORY.md"})
        self.assertTrue(got.is_absolute())
        self.assertEqual(got.name, "MEMORY.md")

    def test_rejects_relative_dir(self):
        with self.assertRaises(ValueError):
            gui.safe_out_path({"dir": "relative/dir", "name": "MEMORY.md"})

    def test_rejects_traversal_in_name(self):
        for bad in ("../escape.md", "sub/dir.md", "C:\\evil.md", "..\\evil.md"):
            with self.assertRaises(ValueError, msg=bad):
                gui.safe_out_path({"dir": str(self.dir), "name": bad})

    def test_rejects_bad_extension(self):
        for bad in ("notes.exe", "run.bat", "x.ps1", "noext"):
            with self.assertRaises(ValueError, msg=bad):
                gui.safe_out_path({"dir": str(self.dir), "name": bad})

    def test_allows_documented_extensions(self):
        for good in ("a.md", "a.markdown", "a.json", "a.txt", "A.MD"):
            gui.safe_out_path({"dir": str(self.dir), "name": good})

    def test_empty_name_falls_back(self):
        self.assertEqual(gui.safe_out_path({"dir": str(self.dir), "name": "  "}).name,
                         "MEMORY.md")


class TestSaveAndOpen(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="cm-save-"))

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def test_save_writes_utf8(self):
        d = gui.do_save({"text": "# hé\n\nvalue\n", "dir": str(self.dir), "name": "M.md"})
        written = Path(d["path"]).read_bytes()
        self.assertEqual(written.decode("utf-8"), "# hé\n\nvalue\n")
        self.assertNotIn(b"\r\n", written)

    def test_save_creates_missing_folders(self):
        target = self.dir / "nested" / "deep"
        d = gui.do_save({"text": "x", "dir": str(target), "name": "M.md"})
        self.assertTrue(Path(d["path"]).exists())

    def test_save_rejects_empty_text(self):
        with self.assertRaises(ValueError):
            gui.do_save({"text": "   ", "dir": str(self.dir), "name": "M.md"})
        with self.assertRaises(ValueError):
            gui.do_save({"text": None, "dir": str(self.dir), "name": "M.md"})

    def test_save_archives_previous_version(self):
        gui.do_save({"text": "first", "dir": str(self.dir), "name": "M.md"})
        gui.do_save({"text": "second", "dir": str(self.dir), "name": "M.md", "history": 5})
        hist = list((self.dir / "M.history").glob("*.md"))
        self.assertEqual(len(hist), 1)
        self.assertEqual(hist[0].read_text(encoding="utf-8"), "first")

    def test_save_without_history_overwrites_silently(self):
        gui.do_save({"text": "first", "dir": str(self.dir), "name": "M.md"})
        gui.do_save({"text": "second", "dir": str(self.dir), "name": "M.md", "history": 0})
        self.assertFalse((self.dir / "M.history").exists())
        self.assertEqual((self.dir / "M.md").read_text(encoding="utf-8"), "second")

    def test_open_creates_the_folder(self):
        target = self.dir / "not-yet"
        gui.do_open({"dir": str(target), "name": "M.md"})
        self.assertTrue(target.is_dir())

    def test_open_works_when_the_file_already_exists(self):
        """Regression: it used to mkdir the file itself and raise."""
        (self.dir / "M.md").write_text("x", encoding="utf-8")
        d = gui.do_open({"dir": str(self.dir), "name": "M.md"})
        self.assertEqual(Path(d["path"]), self.dir)
        self.assertTrue((self.dir / "M.md").is_file())


class TestBodyDecoding(unittest.TestCase):
    def test_utf8_body(self):
        got = gui.Handler._decode_body(b'{"a": "caf\xc3\xa9 \xc2\xb7 ok"}')
        self.assertEqual(got["a"], "café · ok")

    def test_utf8_bom_is_tolerated(self):
        self.assertEqual(gui.Handler._decode_body(b'\xef\xbb\xbf{"a": 1}'), {"a": 1})

    def test_legacy_single_byte_body_does_not_crash(self):
        """PowerShell and old scripts send cp1252; it must still work."""
        raw = '{"a": "café · ok"}'.encode("cp1252")
        got = gui.Handler._decode_body(raw)
        self.assertEqual(got["a"], "café · ok")

    def test_empty_body_is_empty_object(self):
        self.assertEqual(gui.Handler._decode_body(b""), {})

    def test_non_object_rejected(self):
        with self.assertRaises(ValueError):
            gui.Handler._decode_body(b"[1,2,3]")

    def test_invalid_json_rejected(self):
        with self.assertRaises(ValueError):
            gui.Handler._decode_body(b"{nope")


class TestSaveAsDialog(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="cm-saveas-"))
        self.calls = []

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def chooser(self, name, start_dir):
        self.calls.append((name, start_dir))
        return str(self.tmp / name)

    def test_writes_to_the_chosen_path(self):
        got = gui.do_save_as({"text": "hello", "name": "MEMORY.md"},
                             choose=self.chooser)
        self.assertEqual(got["path"], str(self.tmp / "MEMORY.md"))
        self.assertEqual(Path(got["path"]).read_text(encoding="utf-8"), "hello")
        self.assertEqual(self.calls[0][0], "MEMORY.md")
        self.assertTrue(Path(self.calls[0][1]).is_absolute())

    def test_cancelled_writes_nothing(self):
        got = gui.do_save_as({"text": "hello", "name": "MEMORY.md"},
                             choose=lambda n, d: None)
        self.assertTrue(got["cancelled"])
        self.assertNotIn("path", got)
        self.assertEqual(list(self.tmp.iterdir()), [])

    def test_rename_is_honoured(self):
        gui.do_save_as({"text": "x", "name": "notes.md"}, choose=self.chooser)
        self.assertTrue((self.tmp / "notes.md").exists())

    def test_missing_extension_gets_md_appended(self):
        def chooser(name, start):
            return str(self.tmp / "noext")

        got = gui.do_save_as({"text": "x", "name": "noext"}, choose=chooser)
        self.assertTrue(got["path"].endswith("noext.md"))

    def test_start_dir_defaults_to_documents(self):
        gui.do_save_as({"text": "x", "name": "M.md", "dir": ""}, choose=self.chooser)
        self.assertEqual(self.calls[0][1], str(gui.default_out_dir()))

    def test_empty_text_is_refused_before_the_dialog(self):
        opened = []
        with self.assertRaises(ValueError):
            gui.do_save_as({"text": "  "}, choose=lambda n, d: opened.append(1))
        self.assertEqual(opened, [], "must not open a dialog with nothing to save")

    def test_without_a_chooser_it_explains_the_alternatives(self):
        with self.assertRaises(ValueError) as ctx:
            gui.do_save_as({"text": "x", "name": "M.md"}, choose=None)
        self.assertIn("Save", str(ctx.exception))
        self.assertIn("Download", str(ctx.exception))

    def test_archives_previous_version_when_asked(self):
        gui.do_save_as({"text": "first", "name": "M.md"}, choose=self.chooser)
        gui.do_save_as({"text": "second", "name": "M.md", "history": 5},
                       choose=self.chooser)
        hist = list((self.tmp / "M.history").glob("*.md"))
        self.assertEqual(len(hist), 1)
        self.assertEqual(hist[0].read_text(encoding="utf-8"), "first")

    def test_name_cannot_escape_via_the_chooser_argument(self):
        gui.do_save_as({"text": "x", "name": "../evil.md"}, choose=self.chooser)
        self.assertEqual(self.calls[0][0], "evil.md")   # basename only

    def test_download_button_uses_the_dialog(self):
        self.assertTrue('this.call("/api/save_as"' in gui.BRIDGE_TRANSPORT)
        self.assertTrue('d.cancelled ? "cancelled"' in gui.BRIDGE_TRANSPORT)
        self.assertTrue('save cancelled - nothing written' in gui.PAGE)
        # the browser front end must keep the blob download it actually supports
        self.assertTrue("new Blob(" in gui.HTTP_TRANSPORT)
        self.assertTrue("/api/save_as" not in gui.HTTP_TRANSPORT)

    def test_dispatch_passes_the_chooser_through(self):
        got = gui.dispatch("/api/save_as", {"text": "x", "name": "M.md"},
                           choose=self.chooser)
        self.assertTrue(Path(got["path"]).exists())


class TestShareSection(unittest.TestCase):
    """'Local + a shared link' must expose both things it needs to do its job."""

    def box_html(self) -> str:
        start = gui.PAGE.index('id="shareBox"')
        return gui.PAGE[start:gui.PAGE.index("<section>", start)]

    def test_local_plus_share_reveals_the_share_box(self):
        """Regression: the box was shown for 'shareonly' only, so choosing
        'Local + a shared link' hid the only inputs that mode accepts - and
        do_build() refuses that mode when both of them are empty."""
        import re
        m = re.search(r"function syncSrc\(\) \{(.*?)\n\}", gui.PAGE, re.S)
        self.assertTrue(m, "syncSrc not found")
        body = m.group(1)
        self.assertTrue('mode !== "local"' in body,
                        "the share box must stay up for 'Local + a shared link' too")
        self.assertTrue('style.display = showShare' in body)
        self.assertNotIn('srcMode() === "shareonly"', body,
                         "'shareonly' alone must no longer decide the box")

    def test_only_share_still_disables_the_local_filters(self):
        """The two modes hide different things: the box vs the local filters."""
        import re
        body = re.search(r"function syncSrc\(\) \{(.*?)\n\}", gui.PAGE, re.S).group(1)
        self.assertIn('const onlyShare = mode === "shareonly"', body)
        self.assertIn('$("hereOnly").disabled = onlyShare', body)

    def test_box_holds_a_paste_field_and_a_file_picker(self):
        box = self.box_html()
        for control in ("shareUrl", "shareFile", "btnShareFile"):
            self.assertIn(f'id="{control}"', box, control)
        self.assertIn("https://claude.ai/share/", box, "no example link to paste")
        self.assertIn("share-payload.json", box, "no example payload path")

    def test_browse_button_fills_the_payload_field(self):
        self.assertTrue('$("btnShareFile").onclick = pickShareFile' in gui.PAGE)
        self.assertTrue("async function pickShareFile()" in gui.PAGE)
        self.assertTrue("TRANSPORT.pick(" in gui.PAGE)
        self.assertTrue('$("shareFile").value = d.path' in gui.PAGE)
        # a cancelled dialog must not blank or overwrite the typed path
        self.assertIn("d.cancelled", gui.PAGE)
        self.assertIn("nothing changed", gui.PAGE)

    def test_browse_reports_failure_instead_of_silently_doing_nothing(self):
        import re
        m = re.search(r"async function pickShareFile\(\) \{(.*?)\n\}", gui.PAGE, re.S)
        self.assertTrue(m, "pickShareFile not found")
        self.assertIn("catch", m.group(1))
        self.assertIn('say("cannot pick a file: "', m.group(1))

    def test_the_both_mode_really_merges_the_two_sources(self):
        """The UI fix is pointless unless the payload reaches the build."""
        import inspect
        self.assertIn('src in ("share", "both", "shareonly")',
                      inspect.getsource(gui.do_build))

    def test_route_is_reachable_from_the_bridge_transport(self):
        self.assertIn("/api/pick_file", gui.BRIDGE_TRANSPORT)
        self.assertIn("/api/pick_file", gui.ROUTES)
        # the browser front end cannot open a dialog, so it must not pretend to
        self.assertNotIn("/api/pick_file", gui.HTTP_TRANSPORT)
        self.assertIn("cannot open a file dialog", gui.HTTP_TRANSPORT)


class TestPickFileDialog(unittest.TestCase):
    def setUp(self):
        self.calls = []

    def opener(self, name, start_dir):
        self.calls.append((name, start_dir))
        return r"C:\somewhere\share-payload.json"

    def test_returns_the_chosen_path(self):
        got = gui.do_pick_file({"kind": "share_file"}, open_file=self.opener)
        self.assertEqual(got["path"], r"C:\somewhere\share-payload.json")
        self.assertFalse(got.get("cancelled"))

    def test_start_dir_defaults_to_documents(self):
        gui.do_pick_file({}, open_file=self.opener)
        self.assertEqual(self.calls[0][1], str(gui.default_out_dir()))

    def test_supplied_dir_is_used_as_the_start_dir(self):
        gui.do_pick_file({"dir": r"C:\start"}, open_file=self.opener)
        self.assertEqual(self.calls[0][1], r"C:\start")

    def test_asks_for_a_share_payload_name_by_default(self):
        gui.do_pick_file({}, open_file=self.opener)
        self.assertTrue(self.calls[0][0].endswith(".json"))

    def test_cancelled_dialog_returns_no_path(self):
        got = gui.do_pick_file({}, open_file=lambda n, d: None)
        self.assertTrue(got["cancelled"])
        self.assertNotIn("path", got)

    def test_picking_does_not_read_or_validate_the_file(self):
        """The path goes back for the user to review; do_build() does the reading."""
        got = gui.do_pick_file({}, open_file=lambda n, d: r"C:\no\such\file.json")
        self.assertEqual(got["path"], r"C:\no\such\file.json")

    def test_without_a_dialog_it_explains_the_alternative(self):
        with self.assertRaises(ValueError) as ctx:
            gui.do_pick_file({}, open_file=None)
        self.assertIn("path", str(ctx.exception))

    def test_dispatch_passes_the_opener_through(self):
        got = gui.dispatch("/api/pick_file", {}, open_file=self.opener)
        self.assertEqual(got["path"], r"C:\somewhere\share-payload.json")

    def test_dispatch_without_an_opener_explains_instead_of_crashing(self):
        with self.assertRaises(ValueError):
            gui.dispatch("/api/pick_file", {})


class TestPageAndServer(unittest.TestCase):
    def test_page_is_self_contained(self):
        # no CDN / remote asset: the UI must work fully offline
        for transport in (gui.HTTP_TRANSPORT, gui.BRIDGE_TRANSPORT):
            html = gui.page_html(transport, "TOK")
            stripped = html.replace("http://127.0.0.1", "")
            stripped = stripped.replace("https://claude.ai/share", "")
            self.assertTrue("http://" not in stripped, "page references a remote URL")
            self.assertTrue("https://" not in stripped, "page references a remote URL")

    def test_page_controls_exist_for_every_api_field(self):
        missing = [f for f in ("shareUrl", "shareFile", "btnShareFile", "since", "maxSess",
                               "maxChars", "maxItems", "mode", "fmt", "outDir", "outName",
                               "hereOnly", "thinking", "dropSide", "ascii", "keepHist",
                               "btnPreview")
                   if f'id="{f}"' not in gui.PAGE]
        self.assertEqual(missing, [], f"controls missing from the page: {missing}")

    def test_layout_does_not_guess_the_header_height(self):
        """Regression: calc(100vh - 53px) left the page scrollable behind the
        sidebar's own scrollbar whenever the header was taller than 53px."""
        import re
        css = re.sub(r"/\*.*?\*/", "", gui.PAGE, flags=re.S)   # ignore comments
        self.assertTrue("calc(100vh" not in css,
                        "layout must not hardcode a header height")
        self.assertTrue("flex-direction: column; height: 100vh" in css)
        self.assertTrue("min-height: 0" in css)
        self.assertTrue(re.search(r"body\s*\{[^}]*overflow:\s*hidden", css))

    def test_feedback_lives_next_to_the_buttons(self):
        """Regression: status was in the left column, which scrolls out of view,
        so Copy/Download looked like they did nothing."""
        import re
        toolbar = re.search(r'<div id="toolbar">(.*?)</div>', gui.PAGE, re.S)
        self.assertTrue(toolbar, "toolbar not found")
        self.assertTrue('id="status"' in toolbar.group(1),
                        "the status line must sit in the toolbar beside the buttons")
        for btn in ("btnPreview", "btnCopy", "btnDl", "btnClaude"):
            self.assertTrue(f'id="{btn}"' in toolbar.group(1), btn)
        # exactly one status element
        self.assertEqual(gui.PAGE.count('id="status"'), 1)
        self.assertTrue('aria-live="polite"' in gui.PAGE)

    def test_generate_button_is_pinned_and_keeps_scroll(self):
        import re
        self.assertTrue('id="actions"' in gui.PAGE)
        actions = re.search(r'<div id="actions">(.*?)</div>\s*</div>', gui.PAGE, re.S)
        self.assertTrue(actions and 'id="btnGen"' in actions.group(1),
                        "Generate must live in the pinned #actions block")
        self.assertTrue(re.search(r"#actions\s*\{[^}]*position:\s*sticky", gui.PAGE))
        self.assertTrue(re.search(r"#actions\s*\{[^}]*bottom:\s*-", gui.PAGE))
        self.assertTrue("const keepScroll = side.scrollTop" in gui.PAGE)
        self.assertTrue("side.scrollTop = keepScroll" in gui.PAGE)
        # pinned block must be the last thing in the sidebar
        self.assertTrue(gui.PAGE.index('id="actions"') > gui.PAGE.index('id="keepHist"'))

    def test_buttons_never_fail_silently(self):
        """Every action must say something, including the no-content case."""
        self.assertTrue("function nothingYet(what)" in gui.PAGE)
        for action in ("copy", "download", "save"):
            self.assertTrue(f'nothingYet("{action}")' in gui.PAGE, action)
        # Preview is hybrid: with no output it opens a Markdown file and says so
        self.assertIn("nothing generated yet - choose a Markdown file", gui.PAGE)
        self.assertIn("return openMarkdownFile()", gui.PAGE)
        # no bare `currentText &&` short-circuit left anywhere
        self.assertTrue("currentText &&" not in gui.PAGE,
                        "a bare short-circuit gives the user no feedback")

    def test_preview_button_uses_safe_text_nodes(self):
        self.assertIn('id="btnPreview"', gui.PAGE)
        self.assertIn('$("btnPreview").onclick = togglePreview', gui.PAGE)
        self.assertIn('api("/api/preview"', gui.PAGE)
        self.assertIn("document.createTextNode", gui.PAGE)
        self.assertIn('preview.setAttribute("dir", detectDirection(currentText))', gui.PAGE)
        self.assertNotIn(".innerHTML", gui.PAGE)

    def test_open_md_uses_a_native_picker_or_a_browser_file_input(self):
        self.assertIn('id="btnOpenMd"', gui.PAGE)
        self.assertIn('$("btnOpenMd").onclick = openMarkdownFile', gui.PAGE)
        self.assertIn('api("/api/open_markdown"', gui.PAGE)
        self.assertIn("nativeOpen: true", gui.BRIDGE_TRANSPORT)
        self.assertIn("nativeOpen: false", gui.HTTP_TRANSPORT)
        # browser mode reads the chosen file locally; no server path access
        self.assertIn('input.type = "file"', gui.PAGE)
        self.assertIn('input.accept = ".md,.markdown,text/markdown,text/plain"', gui.PAGE)

    def test_status_line_is_also_hoverable_when_long(self):
        self.assertTrue("el.title = msg" in gui.PAGE)

    def test_output_pane_cannot_stretch_the_grid_row(self):
        """Regression: without min-height:0 on #right, a long <pre> grew the grid
        row, the sidebar stretched to match and stopped scrolling."""
        import re
        rules = re.findall(r"#right\s*\{([^}]*)\}", gui.PAGE)
        self.assertTrue(rules, "#right rule not found")
        # the top-level rule must allow shrinking; the media query may set 60vh
        self.assertTrue(any("min-height: 0" in r for r in rules),
                        f"#right needs min-height: 0, got: {rules}")
        self.assertTrue(any("overflow: hidden" in r for r in rules))
        # the scrollable children must be the ones that scroll
        self.assertTrue(re.search(r"#out\s*\{[^}]*overflow:\s*auto", gui.PAGE))
        self.assertTrue(re.search(r"#side\s*\{[^}]*overflow-y:\s*auto", gui.PAGE))

    def test_scroll_restore_outlasts_browser_scroll_anchoring(self):
        """The click's focus scroll and Chromium's paint-time scroll anchoring
        both land after a plain synchronous restore."""
        self.assertTrue("requestAnimationFrame(restore)" in gui.PAGE,
                        "restore must also happen on the next frame")
        self.assertTrue("setTimeout(restore, 60)" in gui.PAGE,
                        "restore must also happen after scroll anchoring settles")
        self.assertTrue("const keepScroll = side.scrollTop" in gui.PAGE)

    def test_generate_button_never_takes_focus(self):
        """Focusing a control scrolls it into view, which is what made the
        options above jump away."""
        self.assertTrue('addEventListener("mousedown"' in gui.PAGE)
        self.assertTrue("e.preventDefault()" in gui.PAGE)

    def test_source_toggle_keeps_the_scroll_position(self):
        """Showing the share box inserts content above the fold."""
        import re
        m = re.search(r"function syncSrc\(\) \{(.*?)\n\}", gui.PAGE, re.S)
        self.assertTrue(m, "syncSrc not found")
        body = m.group(1)
        self.assertTrue("const keep = side.scrollTop" in body)
        self.assertTrue("side.scrollTop = keep" in body)
        # the restore has to come after the DOM change, not before
        self.assertGreater(body.index("side.scrollTop = keep"),
                           body.index("style.display = showShare"),
                           "scroll must be restored after the DOM change")

    def test_status_does_not_duplicate_the_stats_line(self):
        """#status is transient feedback; #meta carries the persistent stats."""
        self.assertTrue('say("done", "ok")' in gui.PAGE)
        self.assertTrue('say("done: " + d.stats' not in gui.PAGE)
        self.assertTrue('$("meta").textContent = d.stats' in gui.PAGE)

    def test_full_mode_is_offered_with_a_size_warning(self):
        import re
        self.assertIn('value="full"', gui.PAGE)
        self.assertIn('id="modeHint"', gui.PAGE)
        self.assertTrue(re.search(r"function syncModeHint\(\)", gui.PAGE))
        self.assertIn("thousands of tokens", gui.PAGE)
        self.assertTrue('Max chars (summary parts)' in gui.PAGE,
                        "the budget label should say it only applies to the summary")
        self.assertTrue('$("mode").addEventListener("change", syncModeHint)' in gui.PAGE)

    def test_token_is_long_and_random_per_run(self):
        self.assertGreaterEqual(len(gui.TOKEN), 24)
        self.assertNotEqual(gui.TOKEN, "TOK")

    def test_token_comparison_is_exact(self):
        import hmac as _h
        self.assertTrue(_h.compare_digest(gui.TOKEN, gui.TOKEN))
        self.assertFalse(_h.compare_digest(gui.TOKEN, gui.TOKEN + "x"))
        self.assertFalse(_h.compare_digest(gui.TOKEN, ""))

    def test_free_port_skips_busy_ports(self):
        import socket as s
        with s.socket() as taken:
            taken.bind(("127.0.0.1", 0))
            taken.listen(1)
            busy = taken.getsockname()[1]
            got = gui.free_port(busy)
            self.assertNotEqual(got, busy)

    def test_as_int_falls_back(self):
        self.assertEqual(gui.as_int("abc", 7), 7)
        self.assertEqual(gui.as_int("", 7), 7)
        self.assertEqual(gui.as_int(None, 7), 7)
        self.assertEqual(gui.as_int("12", 7), 12)

    def test_bad_mode_and_format_fall_back(self):
        opts = gui.build_options({"mode": "wat", "fmt": "xml"})
        self.assertEqual(opts.mode, "memory")
        self.assertEqual(opts.fmt, "plain")

    def test_handler_routes(self):
        import inspect
        src = inspect.getsource(gui)
        self.assertEqual(gui.Handler.server_version, "claude-memory-gui")
        self.assertIn("ROUTES", src)

    def test_server_only_binds_loopback(self):
        import inspect
        self.assertIn('("127.0.0.1", port)', inspect.getsource(gui.serve))
        self.assertNotIn("0.0.0.0", inspect.getsource(gui))

    def test_page_html_replaces_every_placeholder(self):
        import re
        for transport in ("", gui.HTTP_TRANSPORT, gui.BRIDGE_TRANSPORT):
            html = gui.page_html(transport, "TOK")
            # assertTrue + short message: assertIn on a whole page dumps 15k chars
            self.assertTrue("__TOKEN__" not in html, "token placeholder left")
            self.assertTrue("__TRANSPORT__" not in html, "transport placeholder left")
        http_page = gui.page_html(gui.HTTP_TRANSPORT, "TOK")
        bridge_page = gui.page_html(gui.BRIDGE_TRANSPORT, "TOK")
        self.assertTrue("X-Token" in http_page)
        self.assertTrue("window.pywebview.api" in bridge_page)

    def test_default_transport_is_http(self):
        self.assertTrue("fetch(" in gui.page_html("", "TOK"))

    def test_transports_implement_the_same_interface(self):
        for name, t in (("http", gui.HTTP_TRANSPORT), ("bridge", gui.BRIDGE_TRANSPORT)):
            for member in ("call", "quit", "copy", "download", "pick", "quitLabel",
                           "ready", "quitButton"):
                self.assertTrue(member in t, f"{name} transport missing {member}")

    def test_window_does_not_claim_to_stop_a_server(self):
        """There is no server in window mode; the button must say so."""
        bridge = gui.page_html(gui.BRIDGE_TRANSPORT, "TOK")
        self.assertTrue('quitButton: "Close window"' in gui.BRIDGE_TRANSPORT)
        self.assertTrue('quitButton: "Stop server"' in gui.HTTP_TRANSPORT)
        self.assertTrue('TRANSPORT.quitButton || "Close"' in bridge)

    def test_initial_scan_waits_for_the_bridge(self):
        """Regression: scan() used to run before pywebview injected the api."""
        bridge_page = gui.page_html(gui.BRIDGE_TRANSPORT, "TOK")
        self.assertTrue("TRANSPORT.ready().then(scan)" in bridge_page,
                        "the first scan must wait for TRANSPORT.ready()")
        self.assertTrue("window.pywebview && window.pywebview.api" in gui.BRIDGE_TRANSPORT)
        self.assertTrue("await this.ready()" in gui.BRIDGE_TRANSPORT,
                        "bridge calls should await readiness too")
        http_page = gui.page_html(gui.HTTP_TRANSPORT, "TOK")
        self.assertTrue("async ready()" in gui.HTTP_TRANSPORT)

    def test_bridge_transport_never_fetches(self):
        """The window talks over the pywebview bridge, never over http."""
        self.assertTrue("fetch(" not in gui.BRIDGE_TRANSPORT)
        self.assertTrue("127.0.0.1" not in gui.BRIDGE_TRANSPORT)

    def test_every_route_is_reachable_from_a_transport(self):
        """Each route must appear in the page body or in one of the transports."""
        assembled = gui.PAGE + gui.HTTP_TRANSPORT + gui.BRIDGE_TRANSPORT
        for route in gui.ROUTES:
            self.assertTrue(route in assembled, f"route {route} unreachable from the page")

    def test_injected_transport_is_a_bare_object_literal(self):
        """Regression: injecting `const X = {...}` produced a syntax error."""
        import re
        for transport in (gui.HTTP_TRANSPORT, gui.BRIDGE_TRANSPORT):
            html = gui.page_html(transport, "TOK")
            self.assertTrue(re.search(r"const TRANSPORT\s*=\s*\{", html),
                            "transport must be injected as a bare object literal")
            self.assertTrue("const TRANSPORT = const" not in html)
            self.assertTrue("const TRANSPORT = function" not in html)
            self.assertEqual(html.count("const TRANSPORT ="), 1)

    def test_transport_literals_are_balanced(self):
        for name, t in (("http", gui.HTTP_TRANSPORT), ("bridge", gui.BRIDGE_TRANSPORT)):
            self.assertTrue(t.strip().startswith("{"), name)
            self.assertTrue(t.strip().endswith("}"), name)
            self.assertEqual(t.count("{"), t.count("}"), f"{name} braces unbalanced")

    def test_dispatch_covers_every_route(self):
        tmp = Path(tempfile.mkdtemp(prefix="cm-disp-"))
        try:
            self.assertIn("sessions", gui.dispatch("/api/scan", {}))
            self.assertEqual(gui.dispatch("/api/stop", {}), {"ok": True})
            saved = gui.dispatch("/api/save", {"text": "x", "dir": str(tmp), "name": "M.md"})
            self.assertTrue(Path(saved["path"]).exists())
            self.assertTrue(gui.dispatch("/api/open", {"dir": str(tmp), "name": "M.md"}))
            with self.assertRaises(ValueError):
                gui.dispatch("/api/nope", {})
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_save_as_dialog_is_the_only_save_as_path(self):
        """The old ~/Downloads fallback was removed once the native dialog landed."""
        self.assertFalse(hasattr(gui, "do_save_downloads"))
        self.assertFalse(hasattr(gui, "downloads_dir"))
        self.assertFalse("/api/save_downloads" in gui.ROUTES)
        self.assertFalse("/api/save_downloads" in gui.PAGE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
