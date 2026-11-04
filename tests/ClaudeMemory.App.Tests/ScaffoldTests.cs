namespace ClaudeMemory.App.Tests;

public sealed class ScaffoldTests
{
    [Fact]
    public void App_type_is_a_wpf_application()
    {
        Assert.True(typeof(System.Windows.Application).IsAssignableFrom(typeof(ClaudeMemory.App.App)));
    }

    [Fact]
    public void App_assembly_has_the_shipping_name()
    {
        Assert.Equal("ClaudeMemory", typeof(ClaudeMemory.App.App).Assembly.GetName().Name);
    }
}