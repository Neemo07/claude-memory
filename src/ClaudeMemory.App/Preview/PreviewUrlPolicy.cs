namespace ClaudeMemory.App.Preview;

/// <summary>
/// The link allowlist shared by the preview sanitizer and the WebView2 navigation guard.
/// </summary>
/// <remarks>
/// Ported from markdown_preview.py:267-272 (<c>_safe_href</c>), which permits a scheme-less
/// target and only rejects a known-bad scheme. The desktop host is stricter on purpose: it
/// accepts <c>http</c>, <c>https</c>, <c>mailto</c> and in-document <c>#</c> anchors, and
/// renders everything else as plain text (IMPLEMENTATION_PLAN.md Phase 7). A relative target
/// would resolve against the <c>about:blank</c> document and can never be fetched anyway.
/// </remarks>
public static class PreviewUrlPolicy
{
    /// <summary>Schemes the preview may navigate to (PRD §8).</summary>
    public static readonly string[] AllowedSchemes = { "http", "https", "mailto" };

    /// <summary>True for a URL the rendered document may carry as a link target.</summary>
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var target = url.Trim();
        if (target[0] == '#') return true;

        var scheme = Scheme(target);
        if (scheme is null) return false;

        foreach (var allowed in AllowedSchemes)
        {
            if (string.Equals(scheme, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>True for an absolute URL the shell may hand to the default browser.</summary>
    public static bool IsExternal(string? url)
        => IsAllowed(url) && (url ?? "").TrimStart()[0] != '#';

    /// <summary>
    /// Extracts a URL scheme, or null when the target is relative or the scheme is malformed.
    /// A scheme is <c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"</c> (RFC 3986).
    /// </summary>
    private static string? Scheme(string target)
    {
        var colon = target.IndexOf(':');
        if (colon <= 0) return null;

        if (!char.IsAsciiLetter(target[0])) return null;
        for (var index = 1; index < colon; index++)
        {
            var character = target[index];
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('+' or '-' or '.'))
            {
                return null;
            }
        }

        return target[..colon];
    }
}
