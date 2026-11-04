using Markdig;
using Markdig.Extensions.EmphasisExtras;

namespace ClaudeMemory.App.Preview;

/// <summary>
/// Turns generated or opened Markdown into the self-contained HTML document the preview
/// host shows (IMPLEMENTATION_PLAN.md Phase 7).
/// </summary>
/// <remarks>
/// <para>
/// The reference parses Markdown into structured blocks with a hand-written parser and lets
/// the page build the DOM from plain text (markdown_preview.py, gui.py:499-639). The desktop
/// uses Markdig instead, so the equivalent guarantee is made twice: <c>DisableHtml()</c>
/// keeps raw HTML out of the AST, and <see cref="PreviewSanitizer"/> strips unsafe links and
/// images. The result is a document with no scripts, no remote subresources, and a CSP that
/// blocks anything the AST pass might have missed.
/// </para>
/// <para>
/// Extensions are limited to the GFM set the PRD asks for (tables, task lists,
/// strikethrough, autolinks); <c>UseAdvancedExtensions()</c> is deliberately not used because
/// it would add footnotes, abbreviations and containers the reference never rendered.
/// </para>
/// </remarks>
public static class PreviewHtmlBuilder
{
    /// <summary>Locked-down policy for the preview page (AGENTS.md, PRD §8).</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .Build();

    /// <summary>The rendered fragment, without the document shell. Used by the tests.</summary>
    public static string BuildBody(string markdown)
    {
        var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        PreviewSanitizer.Sanitize(document);
        return Markdown.ToHtml(document, Pipeline);
    }

    /// <summary>The complete page handed to <c>WebView2.NavigateToString</c>.</summary>
    public static string BuildDocument(string markdown)
    {
        var body = BuildBody(markdown);
        var direction = DetectDirection(markdown);
        return DocumentHead
               + "\n<body dir=\"" + direction + "\">\n<div class=\"markdown-body\">\n"
               + body
               + "</div>\n</body>\n</html>\n";
    }

    /// <summary>
    /// "rtl" when the text contains Arabic script, otherwise "ltr"
    /// (gui.py:494-497 <c>detectDirection</c>).
    /// </summary>
    public static string DetectDirection(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "ltr";

        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x0600 and <= 0x06FF
                or >= 0x0750 and <= 0x077F
                or >= 0x08A0 and <= 0x08FF
                or >= 0x0870 and <= 0x089F)
            {
                return "rtl";
            }
        }

        return "ltr";
    }

    // The shell is a raw string literal so the CSS braces stay literal. The dark palette
    // matches App.xaml and the reference's GitHub-like preview rules (gui.py:209-250).
    private const string DocumentHead = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:">
<meta name="color-scheme" content="dark">
<title>claude-memory preview</title>
<style>
:root { color-scheme: dark; }
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; }
body {
  background: #0f1115; color: #e6e8ee;
  font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
  font-size: 15px; line-height: 1.6;
}
.markdown-body { max-width: 1012px; margin: 0 auto; padding: 24px 32px 64px; }
h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.4em 0 .6em; font-weight: 600; }
h1, h2 { border-bottom: 1px solid #262b36; padding-bottom: .3em; }
h1 { font-size: 2em; } h2 { font-size: 1.5em; }
h3 { font-size: 1.25em; } h4 { font-size: 1em; }
p { margin: .8em 0; }
a { color: #4493f8; text-decoration: none; }
a:hover { text-decoration: underline; }
del { color: #9aa3b2; }
ul, ol { padding-left: 2em; margin: .8em 0; }
li { margin: .3em 0; }
li.task-list-item { list-style: none; margin-left: -1.45em; }
li.task-list-item input { margin: 0 .5em 0 0; vertical-align: middle; }
blockquote { margin: 1em 0; padding: .2em 1em; color: #9aa3b2; border-left: 4px solid #262b36; }
pre { margin: 1em 0; padding: 16px; overflow: auto; background: #0b0d11;
      border: 1px solid #262b36; border-radius: 6px; }
code { background: #161920; padding: .2em .4em; border-radius: 4px;
       font-family: "Cascadia Mono", Consolas, "Courier New", monospace; font-size: 85%; }
pre code { padding: 0; background: transparent; font-size: 100%; }
table { margin: 1em 0; border-collapse: collapse; width: auto; display: block; overflow: auto; }
th, td { padding: 8px 13px; border: 1px solid #262b36; }
th { background: #161920; font-weight: 600; }
tbody tr:nth-child(2n) { background: #13161c; }
hr { margin: 1.6em 0; border: 0; border-top: 1px solid #262b36; }
body[dir="rtl"], body[dir="rtl"] * { direction: rtl; unicode-bidi: embed; }
</style>
</head>
""";
}
