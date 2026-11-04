using System.Text.RegularExpressions;
using ClaudeMemory.App.Preview;

namespace ClaudeMemory.App.Tests;

/// <summary>
/// Security tests for the preview renderer. The reference never interprets raw HTML
/// (markdown_preview.py:1-9); the desktop host must not either (PRD §8, AGENTS.md).
/// </summary>
public sealed class PreviewHtmlBuilderTests
{
    private static readonly string[] UnsafeSchemes =
    {
        "javascript:", "JavaScript:", "data:", "file:", "vbscript:",
    };

    [Fact]
    public void Raw_html_is_escaped_never_rendered()
    {
        var body = PreviewHtmlBuilder.BuildBody(
            "<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n<iframe src=\"x\"></iframe>");

        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", body, StringComparison.Ordinal);
        Assert.Contains("&lt;iframe", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsafe_link_targets_become_plain_text()
    {
        foreach (var scheme in UnsafeSchemes)
        {
            var markdown = $"[click]({scheme}alert(1))";
            var body = PreviewHtmlBuilder.BuildBody(markdown);

            Assert.DoesNotContain("href", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alert(1)\"", body, StringComparison.Ordinal);
            Assert.Contains("[click](" + scheme + "alert(1))", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Relative_targets_are_refused()
    {
        var body = PreviewHtmlBuilder.BuildBody(
            "[a](./other.md) [b](../up.md) [c](/root.md) [d](other.md)");

        Assert.DoesNotContain("href", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[a](./other.md)", body, StringComparison.Ordinal);
        Assert.Contains("[b](../up.md)", body, StringComparison.Ordinal);
        Assert.Contains("[c](/root.md)", body, StringComparison.Ordinal);
        Assert.Contains("[d](other.md)", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.test/a")]
    [InlineData("http://example.test/a")]
    [InlineData("HTTPS://EXAMPLE.TEST/A")]
    [InlineData("mailto:a@b.test")]
    public void Allowed_schemes_survive(string url)
    {
        var body = PreviewHtmlBuilder.BuildBody($"[link]({url})");

        Assert.Contains("href=\"" + url + "\"", body, StringComparison.Ordinal);
        Assert.Contains(">link</a>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void In_document_anchors_survive()
    {
        var body = PreviewHtmlBuilder.BuildBody("## Heading\n\n[top](#heading)");

        Assert.Contains("href=\"#heading\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_are_reduced_to_their_alt_text()
    {
        var body = PreviewHtmlBuilder.BuildBody(
            "![remote](https://example.test/i.png) and ![data](data:image/png;base64,AAAA)");

        Assert.DoesNotContain("<img", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test/i.png", body, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", body, StringComparison.Ordinal);
        Assert.Contains("remote and data", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_alt_text_keeps_its_word_without_markup()
    {
        var body = PreviewHtmlBuilder.BuildBody("![b **bold** alt](https://example.test/j.png)");

        Assert.DoesNotContain("<img", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<strong>", body, StringComparison.Ordinal);
        Assert.Contains("b bold alt", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_document_carries_the_locked_down_csp_and_no_scripts()
    {
        var document = PreviewHtmlBuilder.BuildDocument(
            "# Title\n\n<script>alert(1)</script>\n\n[a](javascript:alert(1))");

        Assert.Contains(
            "<meta http-equiv=\"Content-Security-Policy\" content=\""
            + PreviewHtmlBuilder.ContentSecurityPolicy + "\">",
            document,
            StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", document, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror=", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript:", document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gfm_blocks_render_tables_task_lists_and_strikethrough()
    {
        var body = PreviewHtmlBuilder.BuildBody(
            "| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n- [ ] todo\n\n~~gone~~");

        Assert.Contains("<table>", body, StringComparison.Ordinal);
        Assert.Contains("<th>a</th>", body, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\"", body, StringComparison.Ordinal);
        Assert.Contains("checked=\"checked\"", body, StringComparison.Ordinal);
        Assert.Contains("<del>gone</del>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Fenced_code_survives_and_stays_escaped()
    {
        var body = PreviewHtmlBuilder.BuildBody("```html\n<script>alert(1)</script>\n```");

        Assert.Contains("<pre><code", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Hello", "ltr")]
    [InlineData("مرحبا بالعالم", "rtl")]
    [InlineData("mixed English and عربى text", "rtl")]
    [InlineData("", "ltr")]
    [InlineData(null, "ltr")]
    public void Direction_follows_arabic_script(string? text, string expected)
        => Assert.Equal(expected, PreviewHtmlBuilder.DetectDirection(text));

    [Fact]
    public void Full_document_applies_the_direction_to_the_body()
    {
        Assert.Contains(
            "<body dir=\"rtl\">",
            PreviewHtmlBuilder.BuildDocument("مرحبا"),
            StringComparison.Ordinal);
        Assert.Contains(
            "<body dir=\"ltr\">",
            PreviewHtmlBuilder.BuildDocument("hello"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_input_produces_a_valid_document_without_throwing()
    {
        var document = PreviewHtmlBuilder.BuildDocument("");

        Assert.StartsWith("<!DOCTYPE html>", document, StringComparison.Ordinal);
        Assert.Contains("<body dir=\"ltr\">", document, StringComparison.Ordinal);
        Assert.EndsWith("</html>\n", document, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_input_is_treated_as_empty()
    {
        var document = PreviewHtmlBuilder.BuildDocument(null!);

        Assert.Contains("<body dir=\"ltr\">", document, StringComparison.Ordinal);
    }
}
