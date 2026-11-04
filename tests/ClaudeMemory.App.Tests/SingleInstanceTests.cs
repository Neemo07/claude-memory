using System.IO;
using ClaudeMemory.App.Services;

namespace ClaudeMemory.App.Tests;

/// <summary>
/// Single-instance hand-off (PRD FR-23). Every test uses its own loopback pipe name, so it can
/// never talk to a real running window.
/// </summary>
public sealed class SingleInstanceTests
{
    private static string NewName() => "claude-memory-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task The_first_launch_receives_the_path_a_second_launch_sends()
    {
        var name = NewName();
        using var first = new SingleInstance(name);
        Assert.True(first.IsFirstInstance);

        var received = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        first.StartListening(path => received.TrySetResult(path));

        using var second = new SingleInstance(name);
        Assert.False(second.IsFirstInstance);

        const string path = @"C:\notes\README.md";
        Assert.True(second.TrySend(path));

        var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(path, delivered);
    }

    [Fact]
    public async Task Several_hand_offs_arrive_one_at_a_time()
    {
        var name = NewName();
        using var first = new SingleInstance(name);
        var received = new List<string>();
        var gate = new SemaphoreSlim(0);

        first.StartListening(path =>
        {
            lock (received) received.Add(path);
            gate.Release();
        });

        using var second = new SingleInstance(name);
        Assert.True(second.TrySend(@"C:\a.md"));
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(second.TrySend(@"D:\b.markdown"));
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(10)));

        lock (received)
        {
            Assert.Equal(new[] { @"C:\a.md", @"D:\b.markdown" }, received);
        }
    }

    [Fact]
    public void Sending_without_a_running_instance_returns_false()
    {
        using var lonely = new SingleInstance(NewName());

        Assert.True(lonely.IsFirstInstance);
        Assert.False(lonely.TrySend(@"C:\a.md", timeoutMilliseconds: 300));
    }

    [Fact]
    public void The_owner_flag_is_false_for_every_later_instance()
    {
        var name = NewName();
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);
        using var third = new SingleInstance(name);

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
        Assert.False(third.IsFirstInstance);
    }

    [Fact]
    public void Disposing_the_owner_lets_the_next_launch_take_over()
    {
        var name = NewName();

        var first = new SingleInstance(name);
        Assert.True(first.IsFirstInstance);
        first.Dispose();

        using var second = new SingleInstance(name);
        Assert.True(second.IsFirstInstance);
    }

    [Fact]
    public void Disposing_a_listener_stops_it_without_throwing()
    {
        var name = NewName();
        var instance = new SingleInstance(name);
        instance.StartListening(_ => { });

        instance.Dispose();

        // A second dispose must also be harmless.
        instance.Dispose();
    }

    [Fact]
    public async Task An_empty_request_is_ignored()
    {
        var name = NewName();
        using var first = new SingleInstance(name);
        var calls = 0;
        var gate = new SemaphoreSlim(0);

        first.StartListening(_ =>
        {
            Interlocked.Increment(ref calls);
            gate.Release();
        });

        using var second = new SingleInstance(name);
        Assert.True(second.TrySend("   "));

        // Give the listener a moment; a whitespace-only line must not reach the window.
        await Task.Delay(700);
        Assert.Equal(0, Volatile.Read(ref calls));
    }
}
