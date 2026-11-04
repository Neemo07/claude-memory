#!/usr/bin/env python3
"""Tests for app.py (the desktop window). No window is opened.

    py -3.12 -m unittest test_app -v
"""
import io
import json
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import claude_memory as cm  # noqa: E402
import gui  # noqa: E402
import app  # noqa: E402


class SharePayload:
    UUID = "a683405f-3f2d-4050-8b77-1c4aef7ce550"

    @classmethod
    def build(cls):
        return {"uuid": cls.UUID, "conversation_uuid": cls.UUID,
                "created_at": "2026-10-06T08:00:00.000000Z",
                "updated_at": "2026-10-06T09:00:00.000000Z",
                "snapshot_name": "Shared chat",
                "chat_messages": [
                    {"uuid": "m0", "parent_message_uuid": None, "sender": "human",
                     "index": 0, "created_at": "2026-10-06T08:00:00.000000Z", "text": "",
                     "content": [{"type": "text", "text": "why does the build fail?"}]},
                    {"uuid": "m1", "parent_message_uuid": "m0", "sender": "assistant",
                     "index": 1, "created_at": "2026-10-06T08:01:00.000000Z", "text": "",
                     "content": [{"type": "text",
                                  "text": "Decision: pin the layout to 32 bytes."}]},
                ]}


class TestApiBridge(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="cm-app-"))
        self.payload = self.tmp / "payload.json"
        self.payload.write_text(json.dumps(SharePayload.build()), encoding="utf-8")
        self.api = app.Api({})
        cm.QUIET = True

    def tearDown(self):
        cm.QUIET = False
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_build_through_the_bridge(self):
        got = self.api.call("/api/build", json.dumps({
            "src": "shareonly", "share_file": str(self.payload), "history": 0}))
        self.assertNotIn("error", got)
        self.assertIn("pin the layout to 32 bytes", got["text"])
        self.assertIn("1 session(s)", got["stats"])

    def test_build_remembers_the_text_for_copy(self):
        self.api.call("/api/build", json.dumps({
            "src": "shareonly", "share_file": str(self.payload), "history": 0}))
        self.assertIn("pin the layout", self.api.last_text)

    def test_errors_come_back_as_data_not_exceptions(self):
        """The page expects {error: ...}; raising would surface as a JS rejection."""
        got = self.api.call("/api/build", json.dumps({"src": "shareonly"}))
        self.assertIn("share link", got["error"])
        self.assertEqual(len(got), 1)

    def test_share_errors_are_reported_readably(self):
        got = self.api.call("/api/build", json.dumps(
            {"src": "shareonly", "share_file": str(self.tmp / "nope.json")}))
        self.assertIn("error", got)

    def test_malformed_payload_is_handled(self):
        self.assertIn("error", self.api.call("/api/build", "{not json"))
        self.assertIn("error", self.api.call("/api/build", "[1,2,3]"))
        self.assertIn("error", self.api.call("/api/nope", "{}"))

    def test_save_through_the_bridge(self):
        got = self.api.call("/api/save", json.dumps(
            {"text": "hello", "dir": str(self.tmp / "out"), "name": "M.md"}))
        self.assertTrue(Path(got["path"]).exists())
        self.assertEqual(Path(got["path"]).read_text(encoding="utf-8"), "hello")

    def test_preview_through_the_bridge(self):
        got = self.api.call("/api/preview", json.dumps({
            "text": "# Hello\n\n**bold**", "mode": "memory"}))
        self.assertNotIn("error", got)
        self.assertEqual(got["blocks"][0]["type"], "heading")

    def test_open_markdown_through_the_bridge(self):
        md = self.tmp / "README.md"
        md.write_text("# Read me\n\n- [x] done\n", encoding="utf-8")
        asked = {}

        class FakeWindow:
            def create_file_dialog(self, kind, directory="", allow_multiple=False,
                                   save_filename="", file_types=()):
                asked.update(kind=kind, types=list(file_types))
                return (str(md),)

        api = app.Api({"window": FakeWindow()})
        got = api.call("/api/open_markdown", json.dumps({"dir": str(self.tmp)}))
        self.assertNotIn("error", got, got)
        self.assertEqual(got["name"], "README.md")
        self.assertEqual(got["blocks"][0]["type"], "heading")
        self.assertTrue(got["blocks"][1]["items"][0]["checked"])
        self.assertEqual(asked["kind"], 10)
        self.assertTrue(any("Markdown" in t for t in asked["types"]))

    def test_save_rejects_traversal_through_the_bridge(self):
        got = self.api.call("/api/save", json.dumps(
            {"text": "x", "dir": str(self.tmp), "name": "../escape.md"}))
        self.assertIn("error", got)

    def test_scan_and_stop(self):
        self.assertIn("sessions", self.api.call("/api/scan", "{}"))
        self.assertEqual(self.api.call("/api/stop", "{}"), {"ok": True})

    def test_quit_without_a_window_is_harmless(self):
        self.api.quit()          # no window registered: must not raise

    def test_choose_path_uses_the_native_dialog(self):
        asked = {}

        class FakeWindow:
            def create_file_dialog(self, kind, directory="", allow_multiple=False,
                                   save_filename="", file_types=()):
                asked.update(kind=kind, directory=directory, name=save_filename,
                             types=list(file_types))
                return (r"C:\somewhere\chosen.md",)

        api = app.Api({"window": FakeWindow()})
        got = api.call("/api/save_as", json.dumps(
            {"text": "hello", "name": "MEMORY.md", "dir": r"C:\start"}))
        self.assertNotIn("error", got, got)
        self.assertTrue(got["path"].endswith("chosen.md"))
        self.assertEqual(asked["name"], "MEMORY.md")
        self.assertEqual(asked["directory"], r"C:\start")
        self.assertTrue(asked["types"], "the dialog should offer a file type filter")

    def test_choose_path_reports_cancellation(self):
        class FakeWindow:
            def create_file_dialog(self, *a, **k):
                return None

        api = app.Api({"window": FakeWindow()})
        got = api.call("/api/save_as", json.dumps({"text": "x", "name": "M.md"}))
        self.assertTrue(got.get("cancelled"))

    def test_choose_path_without_a_window_is_safe(self):
        self.assertIsNone(app.Api({}).choose_path("M.md", ""))

    def test_choose_input_uses_the_native_open_dialog(self):
        """The Browse... button behind the share-payload field needs a real picker."""
        asked = {}

        class FakeWindow:
            def create_file_dialog(self, kind, directory="", allow_multiple=False,
                                   save_filename="", file_types=()):
                asked.update(kind=kind, directory=directory, types=list(file_types))
                return (r"C:\somewhere\share-payload.json",)

        api = app.Api({"window": FakeWindow()})
        got = api.call("/api/pick_file", json.dumps({"dir": r"C:\start"}))
        self.assertNotIn("error", got, got)
        self.assertEqual(got["path"], r"C:\somewhere\share-payload.json")
        # OPEN, not SAVE: a save dialog would offer to overwrite the payload
        self.assertEqual(asked["kind"], 10)
        self.assertEqual(asked["directory"], r"C:\start")
        self.assertTrue(asked["types"], "the dialog should offer a payload file filter")

    def test_choose_input_reports_cancellation(self):
        class FakeWindow:
            def create_file_dialog(self, *a, **k):
                return None

        api = app.Api({"window": FakeWindow()})
        got = api.call("/api/pick_file", "{}")
        self.assertTrue(got.get("cancelled"))
        self.assertNotIn("path", got)

    def test_choose_input_without_a_window_is_safe(self):
        self.assertIsNone(app.Api({}).choose_input("share-payload.json", ""))

    def test_dialog_failure_becomes_a_readable_error(self):
        class AngryWindow:
            def create_file_dialog(self, *a, **k):
                raise RuntimeError("no WebView2 runtime")

        api = app.Api({"window": AngryWindow()})
        got = api.call("/api/save_as", json.dumps({"text": "x", "name": "M.md"}))
        self.assertIn("error", got)
        self.assertIn("no WebView2 runtime", got["error"])

    def test_clipboard_of_empty_text_is_false(self):
        self.assertFalse(self.api.copy(""))


class TestTkMarkdownPreview(unittest.TestCase):
    def test_tk_fallback_renders_preview_blocks(self):
        class FakeText:
            def __init__(self):
                self.value = ""
                self.tags = []

            def index(self, _where):
                return len(self.value)

            def insert(self, _where, value):
                self.value += value

            def tag_add(self, *args):
                self.tags.append(args)

            def tag_configure(self, *args, **kwargs):
                pass

        widget = FakeText()
        blocks = gui.parse_markdown("# Title\n\n**bold**\n\n- one")
        app.render_markdown_widget(widget, blocks)
        self.assertIn("Title", widget.value)
        self.assertIn("bold", widget.value)
        self.assertIn("• one", widget.value)
        self.assertTrue(widget.tags)

    def test_tk_fallback_has_preview_button(self):
        import inspect
        source = inspect.getsource(app.run_tk)
        self.assertIn("Preview MD", source)
        self.assertIn("gui.do_preview", source)
        self.assertIn("Open MD...", source)
        self.assertIn("gui.do_open_markdown", source)
        self.assertIn("askopenfilename", source)


class TestPageFile(unittest.TestCase):
    def test_write_page_uses_the_bridge_transport(self):
        path = app.write_page(gui.BRIDGE_TRANSPORT)
        try:
            html = path.read_text(encoding="utf-8")
            self.assertTrue("window.pywebview.api" in html)
            self.assertTrue("__TRANSPORT__" not in html)
            self.assertTrue("__TOKEN__" not in html)
            self.assertTrue("fetch(" not in gui.BRIDGE_TRANSPORT)
        finally:
            path.unlink(missing_ok=True)

    def test_page_file_is_written_to_a_temp_location(self):
        path = app.write_page(gui.BRIDGE_TRANSPORT)
        try:
            self.assertIn("claude-memory-window", path.name)
        finally:
            path.unlink(missing_ok=True)


class TestConsoleLessStartup(unittest.TestCase):
    """start-app.cmd launches with pythonw.exe, where stdout/stderr are None."""

    def setUp(self):
        self.so, self.se = sys.stdout, sys.stderr
        self.tmps = []

    def tearDown(self):
        sys.stdout, sys.stderr = self.so, self.se
        for p in self.tmps:
            p.unlink(missing_ok=True)

    def test_configure_stdio_survives_none_streams(self):
        sys.stdout = None
        sys.stderr = None
        cm.configure_stdio()
        self.assertIsNotNone(sys.stdout)
        self.assertIsNotNone(sys.stderr)
        cm.eprint("this must not raise under pythonw")   # the actual crash risk
        self.assertEqual(sys.stdout.write(""), 0)

    def test_have_console_is_false_without_streams(self):
        sys.stdout = None
        sys.stderr = None
        self.assertFalse(app.have_console())

    def test_have_console_is_true_with_streams(self):
        self.assertTrue(app.have_console())

    def test_notify_uses_the_console_when_there_is_one(self):
        calls = []
        orig = app.message_box
        app.message_box = lambda *a, **k: calls.append(a)
        try:
            buf = io.StringIO()
            with redirect_stdout(buf):
                app.notify("hello there")
            self.assertIn("hello there", buf.getvalue())
            self.assertEqual(calls, [])
        finally:
            app.message_box = orig

    def test_notify_uses_a_dialog_when_there_is_not(self):
        calls = []
        orig = app.message_box
        app.message_box = lambda *a, **k: calls.append(a)
        so, se = sys.stdout, sys.stderr
        try:
            sys.stdout = None
            sys.stderr = None
            app.notify("no console here")
            self.assertEqual(len(calls), 1)
            self.assertIn("no console here", calls[0][1])
        finally:
            app.message_box = orig
            sys.stdout, sys.stderr = so, se

    def test_report_crash_writes_a_log(self):
        log = HERE / ("claude-memory-error.log")
        had = log.exists()
        previous = log.read_text(encoding="utf-8") if had else None
        calls = []
        orig = app.message_box
        app.message_box = lambda *a, **k: calls.append(a)
        try:
            err = io.StringIO()
            with redirect_stderr(err):
                try:
                    raise RuntimeError("boom on startup")
                except RuntimeError as e:
                    written = app.report_crash(e)
            self.assertEqual(written, log)
            body = log.read_text(encoding="utf-8")
            self.assertIn("boom on startup", body)
            self.assertIn("Traceback", body)
            self.assertIn("could not start", body)
            # with a console it prints instead of popping a dialog
            self.assertIn("boom on startup", err.getvalue())
            self.assertEqual(calls, [])
        finally:
            app.message_box = orig
            if not had:
                log.unlink(missing_ok=True)
            else:
                log.write_text(previous, encoding="utf-8")

    def test_report_crash_without_console_shows_a_dialog(self):
        log = HERE / ("claude-memory-error.log")
        had = log.exists()
        calls = []
        orig = app.message_box
        app.message_box = lambda *a, **k: calls.append(a)
        so, se = sys.stdout, sys.stderr
        try:
            sys.stdout = None
            sys.stderr = None
            try:
                raise RuntimeError("silent boom")
            except RuntimeError as e:
                app.report_crash(e)
            self.assertEqual(len(calls), 1)
            self.assertIn("silent boom", calls[0][1])
        finally:
            app.message_box = orig
            sys.stdout, sys.stderr = so, se
            if not had:
                log.unlink(missing_ok=True)

    def test_error_log_path_is_next_to_the_app(self):
        self.assertEqual(app.LOG_NAME, "claude-memory-error.log")
        self.assertEqual(app.HERE, Path(app.__file__).resolve().parent)

    def test_message_box_never_raises(self):
        app.message_box("t", "m")          # must swallow its own failures

    def test_debug_flag_is_wired_to_the_window(self):
        import inspect
        src = inspect.getsource(app.main)
        self.assertTrue('"--debug"' in src)
        self.assertTrue("debug=args.debug" in src)
        self.assertIn("debug", inspect.signature(app.run_webview).parameters)


class TestLauncher(unittest.TestCase):
    def test_bridge_page_mentions_no_localhost(self):
        html = gui.page_html(gui.BRIDGE_TRANSPORT, "")
        # the window path must not reference a local server at all
        for needle in ("127.0.0.1", "localhost", "/api/stop", "X-Token"):
            self.assertTrue(needle not in gui.BRIDGE_TRANSPORT,
                            f"bridge transport references {needle}")

    def test_webview_is_started_without_http_server(self):
        import inspect
        self.assertTrue("http_server=False" in inspect.getsource(app.run_webview))

    def test_launcher_offers_all_three_front_ends(self):
        import inspect
        src = inspect.getsource(app.main)
        for flag in ("--tk", "--browser", "have_webview"):
            self.assertTrue(flag in src, f"main() lost {flag}")

    def test_install_hint_names_the_real_command(self):
        hint = app.install_hint()
        self.assertTrue("pywebview" in hint)
        self.assertTrue("setup-webview.cmd" in hint)

    def test_clipboard_helper_returns_a_bool(self):
        self.assertIsInstance(app.copy_to_clipboard(""), bool)
        self.assertFalse(app.copy_to_clipboard(""))


if __name__ == "__main__":
    unittest.main(verbosity=2)
