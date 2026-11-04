namespace ClaudeMemory.App.Preview;

/// <summary>
/// Decides which WebView2 navigations belong to the hosted document and must stay inside
/// the host, versus everything else, which is cancelled and (for real web links) handed to
/// the default browser.
/// </summary>
/// <remarks>
/// <see cref="System.Windows.Controls.WebView2.NavigateToString"/> surfaces in
/// <c>NavigationStarting</c> as a <c>data:text/html</c> navigation — and so does clicking an
/// in-document anchor — but that event arrives <em>after</em> the call has returned, so the
/// decision cannot depend on a flag set around the call. A flag-based guard raced the event
/// and cancelled the preview's own document, leaving the pane blank. Content cannot start
/// its own <c>data:</c> navigation: the generated document's CSP disables scripts and the
/// sanitizer strips every href whose scheme is not http/https/mailto/anchor.
/// </remarks>
public static class PreviewNavigationPolicy
{
    public static bool IsSelfNavigation(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return true;

        // NavigateToString's document, in both percent-encoded and base64 form, plus
        // in-document anchors, which reuse the same data: URI with a fragment.
        if (uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) return true;

        // The host's initial blank state.
        if (uri.StartsWith("about:blank", StringComparison.Ordinal)) return true;

        return false;
    }
}
