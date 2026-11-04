using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ClaudeMemory.App.Preview;

/// <summary>
/// Removes the parts of a Markdig document the preview must never render: unsafe link
/// targets, images, and any leftover raw HTML.
/// </summary>
/// <remarks>
/// Markdig escapes raw HTML once <c>DisableHtml()</c> is on, but it still emits
/// <c>&lt;a href="javascript:..."&gt;</c> for an unsafe link and an <c>&lt;img&gt;</c> for an
/// image, so this pass is load-bearing (verified against the corpus in
/// <c>PreviewHtmlBuilderTests</c>). An unsafe target is turned back into plain source text,
/// which is what markdown_preview.py:256-260 does when it declines to consume the link.
/// </remarks>
internal static class PreviewSanitizer
{
    public static void Sanitize(ContainerBlock document)
    {
        foreach (var block in document)
        {
            switch (block)
            {
                case LeafBlock { Inline: not null } leaf:
                    SanitizeInlines(leaf.Inline);
                    break;
                case ContainerBlock nested:
                    Sanitize(nested);
                    break;
            }
        }
    }

    private static void SanitizeInlines(ContainerInline container)
    {
        // Snapshot first: replacing a link detaches its own children, so a live walk would
        // visit nodes that are no longer part of the tree.
        foreach (var node in container.FindDescendants<Inline>().ToList())
        {
            if (node.Parent is null) continue;

            switch (node)
            {
                case LinkInline { IsImage: true } image:
                    // No remote images: keep the alt text, drop the element (Phase 7 scope).
                    Replace(image, LabelText(image));
                    break;

                case LinkInline link:
                    if (!PreviewUrlPolicy.IsAllowed(link.Url))
                    {
                        Replace(link, "[" + LabelText(link) + "](" + (link.Url ?? "") + ")");
                    }

                    break;

                case AutolinkInline autolink:
                    if (!PreviewUrlPolicy.IsAllowed(autolink.Url))
                    {
                        Replace(autolink, autolink.Url ?? "");
                    }

                    break;

                case HtmlInline html:
                    Replace(html, html.Tag ?? "");
                    break;
            }
        }
    }

    private static void Replace(Inline node, string text)
        => node.ReplaceBy(new LiteralInline(text), copyChildren: false);

    private static string LabelText(ContainerInline container)
    {
        var text = new StringBuilder();
        AppendText(container, text);
        return text.ToString();
    }

    private static void AppendText(ContainerInline container, StringBuilder text)
    {
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case AutolinkInline autolink:
                    text.Append(autolink.Url);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
                case ContainerInline nested:
                    AppendText(nested, text);
                    break;
            }
        }
    }
}
