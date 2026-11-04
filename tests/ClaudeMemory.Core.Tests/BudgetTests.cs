using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Core.Tests;

/// <summary>Budget accounting vectors from claude_memory.py:539-573.</summary>
public sealed class BudgetTests
{
    [Fact]
    public void Take_ChargesTheFormattedLineCost()
    {
        var budget = new Budget(1000);

        Assert.Equal(1000, budget.Total);
        Assert.Equal(1000, budget.Requested);
        Assert.False(budget.RaisedToFloor);
        Assert.Equal(0, budget.Used);
        Assert.Equal(0, budget.Trimmed);
        Assert.Equal(1000, budget.Left);

        Assert.Equal(new[] { "- hello", "- world" }, budget.Take(new[] { "hello", "world" }));
        Assert.Equal(16, budget.Used);
        Assert.Equal(984, budget.Left);

        Assert.Equal(new[] { "  - x" }, budget.Take(new[] { "x" }, "- ", "  "));
        Assert.Equal(22, budget.Used);

        Assert.True(budget.Line("short line"));
        Assert.Equal(33, budget.Used);
    }

    [Fact]
    public void Take_WithNoBulletUsesIndentOnly()
        => Assert.Equal(new[] { "  plain" }, new Budget(1000).Take(new[] { "plain" }, bullet: "", indent: "  "));

    [Fact]
    public void Budget_RaisesToFloorAndCountsWhatDidNotFit()
    {
        var budget = new Budget(50);

        Assert.Equal(200, budget.Total);
        Assert.Equal(50, budget.Requested);
        Assert.True(budget.RaisedToFloor);

        Assert.Empty(budget.Take(new[] { new string('z', 500) }));
        Assert.Equal(1, budget.Trimmed);
        Assert.False(budget.Line(new string('y', 500)));
        Assert.Equal(2, budget.Trimmed);
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void Take_StopsAtTheExactBudgetBoundary()
    {
        var budget = new Budget(200);

        // "- abcd" costs 7 characters; 28 lines fill 196 and the 29th would need 203.
        var lines = Enumerable.Range(0, 30).Select(_ => "abcd").ToList();
        var kept = budget.Take(lines);

        Assert.Equal(28, kept.Count);
        Assert.Equal(2, budget.Trimmed);
        Assert.Equal(196, budget.Used);
    }
}