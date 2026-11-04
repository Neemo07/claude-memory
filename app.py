#!/usr/bin/env python3
"""
app.py - claude-memory as a desktop window. No local web server.

  start-app.cmd        <- double-click this (installs the window backend once)
  python app.py        <- native window if pywebview is available
  python app.py --tk   <- force the plain-tkinter window (no extra packages)
  python app.py --browser  <- the old localhost page in your real browser

Front ends
  webview : the same HTML page as gui.py, loaded from a temp file over file://,
           talking to Python through the pywebview bridge. pywebview's
           webview.start() has http_server=False by default, so nothing listens
           on a port and no localhost URL is ever used.
  tkinter : plain native widgets, used when pywebview is not installed. Same
           logic underneath (gui.do_build / do_save), just a different shell.

All the real work lives in gui.py and claude_memory.py; this file is only the
window, the bridge and the launcher.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import claude_memory as cm  # noqa: E402
import gui  # noqa: E402

TITLE = "claude-memory"
LOG_NAME = "claude-memory-error.log"

MB_OK = 0x00000000
MB_ICONERROR = 0x00000010
MB_ICONWARNING = 0x00000030
MB_TOPMOST = 0x00040000


def have_console() -> bool:
    """False under pythonw.exe, which is how start-app.cmd launches the window."""
    return sys.stdout is not None and sys.stderr is not None


def message_box(title: str, text: str, icon: int = MB_ICONERROR) -> None:
    """Native dialog, because there may be no console to print to."""
    try:
        import ctypes
        ctypes.windll.user32.MessageBoxW(None, text, title, MB_OK | icon | MB_TOPMOST)
    except Exception:
        pass


def notify(text: str, icon: int = MB_ICONWARNING) -> None:
    """Tell the user, by console when there is one and by dialog when there is not."""
    if have_console():
        print(text)
    else:
        message_box(TITLE, text, icon)


def report_crash(exc: BaseException) -> Path:
    """Write the traceback next to the app and point the user at it."""
    log = HERE / LOG_NAME
    try:
        body = (f"{TITLE} could not start\n\n"
                f"Python {sys.version.split()[0]}\n\n"
                + "".join(traceback.format_exception(type(exc), exc, exc.__traceback__)))
        log.write_text(body, encoding="utf-8")
    except OSError:
        return log
    if have_console():
        print(body, file=sys.stderr)
    else:
        message_box(TITLE, f"{exc}\n\nA log was written to:\n{log}", MB_ICONERROR)
    return log


# ----------------------------------------------------------------------------- bridge


class Api:
    """Everything the page may ask Python to do, over the pywebview bridge."""

    def __init__(self, window_holder: dict) -> None:
        self._window_holder = window_holder
        self.last_text = ""

    def call(self, route: str, payload_json: str = "{}"):
        """One UI request. Never raises: the page expects {error: ...} on failure."""
        try:
            data = json.loads(payload_json or "{}")
            if not isinstance(data, dict):
                raise ValueError("expected a JSON object")
            result = gui.dispatch(route, data, choose=self.choose_path,
                                  open_file=self.choose_input)
            if route == "/api/build":
                self.last_text = result.get("text", "")
            return result
        except cm.ShareError as e:
            return {"error": str(e)}
        except Exception as e:
            return {"error": f"{type(e).__name__}: {e}"}

    def choose_path(self, name: str, start_dir: str = ""):
        """Native Save As dialog. Returns the chosen path, or None if cancelled."""
        win = self._window_holder.get("window")
        if win is None:
            return None
        # pywebview's FileDialog.SAVE, inlined so this method stays importable (and
        # testable) on an interpreter that has no pywebview installed
        save_dialog = 30
        try:
            import webview                                  # noqa: PLC0415
            save_dialog = int(webview.SAVE_DIALOG)
        except ImportError:
            pass
        # a failure here (no WebView2, dialog blocked) propagates to call(),
        # which turns it into a readable {error: ...} for the page
        result = win.create_file_dialog(
            save_dialog,
            directory=start_dir or "",
            save_filename=name or "MEMORY.md",
            file_types=("Markdown and text (*.md;*.markdown;*.txt)",
                        "All files (*.*)"),
        )
        if not result:
            return None
        first = result[0] if isinstance(result, (list, tuple)) else result
        return str(first)

    def choose_input(self, name: str = "", start_dir: str = "", file_types=None):
        """Native Open dialog, for the saved share payload or a Markdown preview.

        Same inlined-dialect trick as choose_path: pywebview is imported lazily so
        this method stays importable (and testable) without it installed.
        """
        win = self._window_holder.get("window")
        if win is None:
            return None
        open_dialog = 10
        try:
            import webview                                  # noqa: PLC0415
            open_dialog = int(webview.FileDialog.OPEN)
        except (ImportError, AttributeError):
            pass
        result = win.create_file_dialog(
            open_dialog,
            directory=start_dir or "",
            file_types=file_types or gui.PAYLOAD_TYPES,
        )
        if not result:
            return None
        first = result[0] if isinstance(result, (list, tuple)) else result
        return str(first)

    def copy(self, text: str) -> bool:
        return copy_to_clipboard(text or "")

    def quit(self) -> None:
        win = self._window_holder.get("window")
        if win is not None:
            win.destroy()


def copy_to_clipboard(text: str) -> bool:
    """A webview usually blocks navigator.clipboard, so do it natively."""
    if not text:
        return False
    if sys.platform == "win32":
        script = ("$t = [Console]::In.ReadToEnd(); "
                  "Set-Clipboard -Value $t")
        try:
            proc = subprocess.run(
                ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                input=text, text=True, capture_output=True, timeout=15)
            return proc.returncode == 0
        except (OSError, subprocess.SubprocessError):
            return False
    if sys.platform == "darwin":
        try:
            proc = subprocess.run(["pbcopy"], input=text, text=True,
                                  capture_output=True, timeout=15)
            return proc.returncode == 0
        except (OSError, subprocess.SubprocessError):
            return False
    for cmd in (["xclip", "-selection", "clipboard"], ["wl-copy"], ["xsel", "-b"]):
        try:
            proc = subprocess.run(cmd, input=text, text=True,
                                  capture_output=True, timeout=15)
            if proc.returncode == 0:
                return True
        except (OSError, subprocess.SubprocessError):
            continue
    return False


def _insert_markdown_spans(widget, spans, emphasis: bool = True) -> None:
    for span in spans or []:
        start = widget.index("end-1c")
        widget.insert("end", span.get("text", ""))
        end = widget.index("end-1c")
        if not emphasis:
            continue
        if span.get("bold"):
            widget.tag_add("md_bold", start, end)
        if span.get("italic"):
            widget.tag_add("md_italic", start, end)
        if span.get("code"):
            widget.tag_add("md_code", start, end)
        if span.get("strike"):
            widget.tag_add("md_strike", start, end)
        if span.get("href"):
            widget.tag_add("md_link", start, end)


def _markdown_span_text(spans) -> str:
    return "".join(span.get("text", "") for span in spans or [])


def _configure_markdown_tags(widget) -> None:
    widget.tag_configure("md_bold", font=("Segoe UI", 10, "bold"))
    widget.tag_configure("md_italic", font=("Segoe UI", 10, "italic"))
    widget.tag_configure("md_code", font=("Consolas", 9), background="#1e222b")
    widget.tag_configure("md_strike", overstrike=True)
    widget.tag_configure("md_link", foreground="#4493f8", underline=True)
    for level, size in ((1, 18), (2, 15), (3, 13), (4, 11), (5, 10), (6, 10)):
        widget.tag_configure(f"md_h{level}", font=("Segoe UI", size, "bold"),
                             spacing1=10, spacing3=5)
    widget.tag_configure("md_quote", foreground="#99a1b3", lmargin1=18)
    widget.tag_configure("md_codeblock", font=("Consolas", 9), background="#1e222b",
                         lmargin1=14, lmargin2=14)
    widget.tag_configure("md_rule", foreground="#2a2f3a")


def _render_markdown_heading(widget, block) -> None:
    start = widget.index("end-1c")
    _insert_markdown_spans(widget, block.get("spans"), emphasis=False)
    end = widget.index("end-1c")
    level = min(6, max(1, block.get("level", 1)))
    widget.tag_add(f"md_h{level}", start, end)
    widget.insert("end", "\n\n")


def _render_markdown_paragraph(widget, block) -> None:
    _insert_markdown_spans(widget, block.get("spans"))
    widget.insert("end", "\n\n")


def _render_markdown_list(widget, block) -> None:
    first_number = block.get("start", 1) if block.get("ordered") else 1
    for number, item in enumerate(block.get("items", []), start=first_number):
        checked = item.get("checked")
        if checked is True:
            marker = "[x]"
        elif checked is False:
            marker = "[ ]"
        else:
            marker = f"{number}." if block.get("ordered") else "•"
        widget.insert("end", marker + " ")
        _insert_markdown_spans(widget, item.get("spans"))
        widget.insert("end", "\n")
    widget.insert("end", "\n")


def _render_markdown_quote(widget, block) -> None:
    start = widget.index("end-1c")
    for quoted in block.get("blocks", []):
        _render_markdown_block(widget, quoted)
    widget.tag_add("md_quote", start, widget.index("end-1c"))


def _render_markdown_code(widget, block) -> None:
    start = widget.index("end-1c")
    widget.insert("end", block.get("text", ""))
    widget.tag_add("md_codeblock", start, widget.index("end-1c"))
    widget.insert("end", "\n\n")


def _render_markdown_table(widget, block) -> None:
    rows = [block.get("headers", [])] + block.get("rows", [])
    for row_index, row in enumerate(rows):
        cells = [_markdown_span_text(cell) for cell in row]
        widget.insert("end", " | ".join(cells) + "\n")
        if row_index == 0:
            widget.insert("end", "─" * 40 + "\n")
    widget.insert("end", "\n")


def _render_markdown_rule(widget, _block) -> None:
    start = widget.index("end-1c")
    widget.insert("end", "────────────────────────────\n\n")
    widget.tag_add("md_rule", start, widget.index("end-1c"))


def _render_markdown_block(widget, block) -> None:
    renderer = _MARKDOWN_RENDERERS.get(block.get("type"))
    if renderer is not None:
        renderer(widget, block)


def render_markdown_widget(widget, blocks) -> None:
    """Render parsed Markdown blocks into a Tk text widget."""
    _configure_markdown_tags(widget)
    for block in blocks or []:
        _render_markdown_block(widget, block)


_MARKDOWN_RENDERERS = {
    "heading": _render_markdown_heading,
    "paragraph": _render_markdown_paragraph,
    "list": _render_markdown_list,
    "quote": _render_markdown_quote,
    "code": _render_markdown_code,
    "table": _render_markdown_table,
    "rule": _render_markdown_rule,
}


# ----------------------------------------------------------------------------- webview


def write_page(transport: str) -> Path:
    """The page must be a real file: pywebview loads file:// and injects the bridge."""
    path = Path(tempfile.gettempdir()) / "claude-memory-window.html"
    path.write_text(gui.page_html(transport, ""), encoding="utf-8")
    return path


def run_webview(width: int = 1240, height: int = 860, debug: bool = False) -> int:
    import webview                                  # noqa: PLC0415 - optional dependency

    holder: dict = {}
    page = write_page(gui.BRIDGE_TRANSPORT)
    try:
        window = webview.create_window(
            TITLE,
            url=str(page),
            js_api=Api(holder),
            width=width, height=height,
            min_size=(760, 520),
            background_color="#0f1115",
            text_select=True,
        )
        holder["window"] = window
        # http_server defaults to False in pywebview 6.x; pass it explicitly so the
        # "no local host" guarantee does not depend on a library default
        webview.start(http_server=False, debug=debug)
    finally:
        page.unlink(missing_ok=True)
    return 0


# ----------------------------------------------------------------------------- tkinter


def run_tk() -> int:
    """Native fallback: no third-party packages, still no local server."""
    import tkinter as tk
    from tkinter import filedialog, ttk

    root = tk.Tk()
    root.title(TITLE)
    root.geometry("1180x800")
    root.minsize(760, 520)
    root.configure(bg="#171a21")

    style = ttk.Style(root)
    try:
        style.theme_use("clam")
    except tk.TclError:
        pass
    style.configure("TFrame", background="#171a21")
    style.configure("TLabel", background="#171a21", foreground="#e6e9ef")
    style.configure("Head.TLabel", background="#171a21", foreground="#99a1b3",
                    font=("Segoe UI", 8, "bold"))
    style.configure("TButton", padding=6)
    style.configure("Accent.TButton", background="#d97757", foreground="#1b1207",
                    font=("Segoe UI", 9, "bold"))

    holder: dict = {"root": root, "text": "", "mode": "memory"}

    left = ttk.Frame(root, padding=12)
    left.pack(side=tk.LEFT, fill=tk.Y)
    right = ttk.Frame(root)
    right.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

    def section(parent, title):
        ttk.Label(parent, text=title.upper(), style="Head.TLabel").pack(
            anchor=tk.W, pady=(0, 6))

    # ---- source
    section(left, "Source")
    src = tk.StringVar(value="local")
    for label, value in (("Local Claude Code sessions", "local"),
                         ("Local + a shared link", "both"),
                         ("Only a shared link", "shareonly")):
        ttk.Radiobutton(left, text=label, value=value, variable=src).pack(anchor=tk.W)

    share_url = tk.StringVar()
    share_file = tk.StringVar()
    url_row = ttk.Frame(left)
    url_row.pack(fill=tk.X, pady=(6, 0))
    ttk.Label(url_row, text="share link / uuid - paste it here").pack(anchor=tk.W)
    ttk.Entry(url_row, textvariable=share_url, width=42).pack(fill=tk.X)

    def browse_payload() -> None:
        """Native Open dialog for the saved share payload (the Browse... button)."""
        chosen = filedialog.askopenfilename(
            parent=root, title="Pick a saved share payload",
            initialdir=out_dir.get() or str(gui.default_out_dir()),
            filetypes=[("Saved share payload", "*.json *.md *.markdown *.txt"),
                       ("All files", "*.*")])
        if chosen:
            share_file.set(chosen)

    file_row = ttk.Frame(left)
    file_row.pack(fill=tk.X, pady=(6, 0))
    ttk.Label(file_row, text="or saved payload file").pack(anchor=tk.W)
    ttk.Button(file_row, text="Browse...", command=browse_payload).pack(
        side=tk.RIGHT, padx=(6, 0))
    ttk.Entry(file_row, textvariable=share_file, width=32).pack(side=tk.LEFT,
                                                               fill=tk.X, expand=True)

    # ---- which sessions
    section(left, "Which sessions")
    here = tk.BooleanVar(value=False)
    ttk.Checkbutton(left, text="this folder only", variable=here).pack(anchor=tk.W)
    grid = ttk.Frame(left)
    grid.pack(fill=tk.X, pady=(6, 0))
    since = tk.StringVar(value="30d")
    max_sess = tk.StringVar(value="0")
    cwd = tk.StringVar()
    ttk.Label(grid, text="Window").grid(row=0, column=0, sticky=tk.W)
    ttk.Entry(grid, textvariable=since, width=12).grid(row=1, column=0, sticky=tk.W)
    ttk.Label(grid, text="Max sessions").grid(row=0, column=1, sticky=tk.W, padx=(8, 0))
    ttk.Entry(grid, textvariable=max_sess, width=8).grid(row=1, column=1, sticky=tk.W,
                                                          padx=(8, 0))
    ttk.Label(grid, text="Path contains").grid(row=2, column=0, sticky=tk.W, pady=(8, 0))
    ttk.Entry(grid, textvariable=cwd, width=22).grid(row=3, column=0, columnspan=2,
                                                     sticky=tk.EW)

    scan_lbl = ttk.Label(left, text="scanning...", style="Head.TLabel")
    scan_lbl.pack(anchor=tk.W, pady=(10, 0))

    # ---- output options
    section(left, "Output")
    mode = tk.StringVar(value="memory")
    fmt = tk.StringVar(value="plain")
    max_chars = tk.StringVar(value="14000")
    max_items = tk.StringVar(value="14")
    thinking = tk.BooleanVar(value=False)
    drop_side = tk.BooleanVar(value=False)
    ascii_only = tk.BooleanVar(value=False)
    keep_hist = tk.BooleanVar(value=True)
    combo = ttk.Frame(left)
    combo.pack(fill=tk.X)
    ttk.Combobox(combo, textvariable=mode, values=("memory", "full", "transcript", "json"),
                 state="readonly", width=14).pack(side=tk.LEFT, fill=tk.X, expand=True)
    ttk.Combobox(combo, textvariable=fmt, values=("plain", "claude-md"),
                 state="readonly", width=12).pack(side=tk.LEFT, fill=tk.X, expand=True,
                                                 padx=(6, 0))
    nums = ttk.Frame(left)
    nums.pack(fill=tk.X, pady=(6, 0))
    ttk.Label(nums, text="Max chars").pack(side=tk.LEFT)
    ttk.Entry(nums, textvariable=max_chars, width=9).pack(side=tk.LEFT, padx=(6, 0))
    ttk.Label(nums, text="Items/section").pack(side=tk.LEFT, padx=(10, 0))
    ttk.Entry(nums, textvariable=max_items, width=6).pack(side=tk.LEFT, padx=(6, 0))
    for text, var in (("include thinking blocks", thinking),
                      ("drop subagent turns", drop_side),
                      ("ASCII only", ascii_only)):
        ttk.Checkbutton(left, text=text, variable=var).pack(anchor=tk.W)

    # ---- save
    section(left, "Save to disk")
    out_dir = tk.StringVar(value=str(gui.default_out_dir()))
    out_name = tk.StringVar(value="MEMORY.md")
    ttk.Entry(left, textvariable=out_dir, width=44).pack(fill=tk.X)
    ttk.Entry(left, textvariable=out_name, width=44).pack(fill=tk.X, pady=(6, 0))
    ttk.Checkbutton(left, text="keep 5 previous versions", variable=keep_hist).pack(
        anchor=tk.W, pady=(4, 0))
    save_row = ttk.Frame(left)
    save_row.pack(fill=tk.X, pady=(6, 0))
    ttk.Button(save_row, text="Save", command=lambda: save()).pack(side=tk.LEFT,
                                                                    fill=tk.X,
                                                                    expand=True)
    ttk.Button(save_row, text="Folder",
               command=lambda: safe(lambda: gui.do_open(
                   {"dir": out_dir.get(), "name": out_name.get()}))).pack(
        side=tk.LEFT, fill=tk.X, expand=True, padx=(6, 0))

    # ---- right side
    tools = ttk.Frame(right, padding=(0, 8, 0, 0))
    tools.pack(fill=tk.X)
    ttk.Button(tools, text="Generate", style="Accent.TButton",
               command=lambda: generate()).pack(side=tk.LEFT)
    ttk.Button(tools, text="Preview MD", command=lambda: preview_md()).pack(
        side=tk.LEFT, padx=(6, 0))
    ttk.Button(tools, text="Open MD...", command=lambda: open_md_file()).pack(
        side=tk.LEFT, padx=(6, 0))
    ttk.Button(tools, text="Copy", command=lambda: copy_out()).pack(side=tk.LEFT,
                                                                    padx=(6, 0))
    ttk.Button(tools, text="Copy for CLAUDE.md",
               command=lambda: copy_claude_md()).pack(side=tk.LEFT, padx=(6, 0))
    status = ttk.Label(right, text="", style="Head.TLabel")
    status.pack(anchor=tk.W, padx=2)
    text = tk.Text(right, wrap=tk.WORD, bg="#0f1115", fg="#e6e9ef", insertbackground="#e6e9ef",
                   font=("Consolas", 9), relief=tk.FLAT, padx=12, pady=10)
    text.pack(fill=tk.BOTH, expand=True, padx=12, pady=(4, 12))
    text.insert("1.0", "Press Generate to build a memory file from your Claude Code "
                        "sessions and any shared claude.ai link.")
    text.configure(state=tk.DISABLED)

    def say(msg: str) -> None:
        status.configure(text=msg)

    def payload() -> dict:
        return {
            "src": src.get(),
            "share": share_url.get().strip(),
            "share_file": share_file.get().strip(),
            "here": here.get(),
            "since": since.get().strip(),
            "sessions": max_sess.get(),
            "cwd": cwd.get().strip(),
            "mode": mode.get(),
            "fmt": fmt.get(),
            "max_chars": max_chars.get(),
            "max_items": max_items.get(),
            "include_thinking": thinking.get(),
            "sidechains": "drop" if drop_side.get() else "keep",
            "ascii_only": ascii_only.get(),
            "history": 5 if keep_hist.get() else 0,
        }

    def show(value: str) -> None:
        text.configure(state=tk.NORMAL)
        text.delete("1.0", tk.END)
        text.insert("1.0", value)
        text.configure(state=tk.DISABLED)

    def safe(fn):
        try:
            fn()
        except Exception as e:
            say(f"{type(e).__name__}: {e}")

    def generate() -> None:
        say("working...")
        root.update_idletasks()
        try:
            request = payload()
            result = gui.do_build(request)
            holder["text"] = result["text"]
            holder["mode"] = request["mode"]
            show(result["text"])
            say("done: " + result["stats"])
        except Exception as e:
            say(f"{type(e).__name__}: {e}")

    def show_preview_window(title: str, blocks) -> None:
        preview = tk.Toplevel(root)
        preview.title(title)
        preview.geometry("900x700")
        preview.minsize(520, 360)
        view = tk.Text(preview, wrap=tk.WORD, bg="#0f1115", fg="#e6e9ef",
                       insertbackground="#e6e9ef", font=("Segoe UI", 10),
                       relief=tk.FLAT, padx=18, pady=14)
        view.pack(fill=tk.BOTH, expand=True)
        render_markdown_widget(view, blocks)
        view.configure(state=tk.DISABLED)

    def open_md_file() -> None:
        chosen = filedialog.askopenfilename(
            parent=root, title="Open Markdown file",
            initialdir=out_dir.get() or str(gui.default_out_dir()),
            filetypes=[("Markdown", "*.md *.markdown"), ("All files", "*.*")])
        if not chosen:
            say("no file chosen - nothing changed")
            return
        try:
            result = gui.do_open_markdown({"path": chosen})
            show_preview_window(f"{result['name']} - Markdown preview",
                                result["blocks"])
            say("reading " + result["path"])
        except Exception as e:
            say(f"{type(e).__name__}: {e}")

    def preview_md() -> None:
        # Hybrid: preview the generated output when there is one, otherwise open
        # a Markdown file so the window is useful before anything is generated.
        if not holder["text"]:
            open_md_file()
            return
        try:
            result = gui.do_preview({"text": holder["text"], "mode": holder["mode"]})
            show_preview_window("Markdown preview", result["blocks"])
            say("Markdown preview opened")
        except Exception as e:
            say(f"{type(e).__name__}: {e}")

    def save() -> None:
        if not holder["text"]:
            say("generate first")
            return

        def go():
            result = gui.do_save({"text": holder["text"], "dir": out_dir.get(),
                                  "name": out_name.get(),
                                  "history": 5 if keep_hist.get() else 0})
            say("saved " + result["path"])
        safe(go)

    def copy_out() -> None:
        if not holder["text"]:
            say("generate first")
            return
        say("copied to clipboard" if copy_to_clipboard(holder["text"])
            else "copy blocked - select the text and press Ctrl+C")

    def copy_claude_md() -> None:
        if not holder["text"]:
            say("generate first")
            return
        body = holder["text"].strip()
        if not body.startswith(gui.BEGIN):
            body = f"{gui.BEGIN}\n{body}\n{gui.END}"
        say("CLAUDE.md block copied" if copy_to_clipboard(body)
            else "copy blocked - select the text and press Ctrl+C")

    def refresh_scan() -> None:
        try:
            found = gui.do_scan()["sessions"]
            if found:
                newest = found[-1]
                scan_lbl.configure(text=f"{len(found)} session(s) found · newest "
                                        f"{newest['start']} · {newest['cwd']}")
            else:
                scan_lbl.configure(text="no local sessions yet - share links work now")
        except Exception as e:
            scan_lbl.configure(text=f"scan failed: {e}")

    refresh_scan()
    root.mainloop()
    return 0


# ----------------------------------------------------------------------------- launcher


def have_webview() -> bool:
    try:
        import webview                                  # noqa: F401, PLC0415
        return True
    except Exception:
        return False


def install_hint() -> str:
    return (f"py -m pip install pywebview\n"
            f"  (or double-click setup-webview.cmd, which installs it into "
            f"{HERE / '.venv'})")


def run_browser(port: int) -> int:
    notify("falling back to the local browser page (this one does use localhost)")
    return gui.main(["--port", str(port)])


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="claude-memory desktop window")
    ap.add_argument("--tk", action="store_true", help="force the tkinter window")
    ap.add_argument("--browser", action="store_true",
                    help="use the localhost browser page instead of a window")
    ap.add_argument("--debug", action="store_true",
                    help="keep pywebview's developer tools available")
    ap.add_argument("--port", type=int, default=8756)
    args = ap.parse_args(argv)

    cm.configure_stdio()
    if args.browser:
        return run_browser(args.port)
    if not args.tk and have_webview():
        try:
            return run_webview(debug=args.debug)
        except ImportError:
            pass
        except Exception as e:
            notify(f"the window backend failed to start ({type(e).__name__}: {e})\n"
                   "falling back to the plain tkinter window\n")
    elif not args.tk:
        notify("pywebview is not installed, so the webview window is unavailable.\n\n"
               "Install it with:\n  " + install_hint() +
               "\n\n(or run with --tk for the plain window, --browser for the "
               "browser page)")

    try:
        import tkinter                                  # noqa: F401, PLC0415
    except ImportError as e:
        notify(f"tkinter is unavailable too ({e}).\n\n"
               "Try the browser version instead:\n  py app.py --browser")
        return 2
    return run_tk()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SystemExit:
        raise
    except BaseException as _exc:                       # noqa: BLE001 - last resort
        report_crash(_exc)
        sys.exit(1)
