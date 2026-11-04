using ClaudeMemory.App.Preview;

namespace ClaudeMemory.App.Tests;

/// <summary>
/// The WebView2 navigation guard. Regression: a flag set around <c>NavigateToString</c>
/// raced <c>NavigationStarting</c> (which fires after the call returns), so the host
/// cancelled its own document and the preview pane stayed blank.
/// </summary>
public sealed class PreviewNavigationPolicyTests
{
    [Theory]
    [InlineData("data:text/html;charset=utf-8,%3C%21DOCTYPE%20html%3E")]
    [InlineData("data:text/html;charset=utf-8;base64,PGh0bWw+")]
    [InlineData("data:text/html;charset=utf-8,%3Chtml%3E...%3C%2Fhtml%3E#section-1")]
    [InlineData("about:blank")]
    [InlineData("")]
    [InlineData(null)]
    public void The_host_s_own_navigations_stay_inside(string? uri)
    {
        Assert.True(PreviewNavigationPolicy.IsSelfNavigation(uri));
    }

    [Theory]
    [InlineData("https://example.test/x")]
    [InlineData("http://example.test/x")]
    [InlineData("mailto:someone@example.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hello")]
    [InlineData("data:application/json,{}")]
    [InlineData("file:///C:/notes.md")]
    [InlineData("vbscript:x")]
    public void Everything_else_is_treated_as_external(string uri)
    {
        Assert.False(PreviewNavigationPolicy.IsSelfNavigation(uri));
    }

    [Fact]
    public void The_data_html_match_is_case_insensitive()
    {
        Assert.True(PreviewNavigationPolicy.IsSelfNavigation("DATA:TEXT/HTML;charset=utf-8,x"));
    }
}
