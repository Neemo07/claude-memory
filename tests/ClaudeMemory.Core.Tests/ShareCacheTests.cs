using System.Text.Json;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// The <c>--share-save</c> cache, ported from claude_memory.py:1929-1933:
/// <c>json.dumps(payload, indent=1, ensure_ascii=False) + "\n"</c> written atomically.
/// </summary>
public sealed class ShareCacheTests
{
    [Fact]
    public void Cache_bytes_match_the_reference_json_dumps_output()
    {
        using var folder = new TempFolder();
        var target = folder.File("cache.json");
        var payload = Json(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "share-branching.json")));

        Assert.True(ShareCache.TryWrite(target, payload));

        var expected = File.ReadAllText(Path.Combine(TestPaths.Golden, "share-cache.json"));
        var actual = File.ReadAllText(target);
        Assert.True(
            expected == actual,
            Golden.Diff("share-cache.json", expected, actual));
    }

    [Fact]
    public void Uses_one_space_indent_and_keeps_native_values()
    {
        using var folder = new TempFolder();
        var target = folder.File("small.json");
        var payload = Json("""{"uuid":"u","chat_messages":[{"x":1.5,"y":true,"z":null}]}""");

        Assert.True(ShareCache.TryWrite(target, payload));

        var expected = string.Join(
            "\n",
            "{",
            " \"uuid\": \"u\",",
            " \"chat_messages\": [",
            "  {",
            "   \"x\": 1.5,",
            "   \"y\": true,",
            "   \"z\": null",
            "  }",
            " ]",
            "}",
            "");
        Assert.Equal(expected, File.ReadAllText(target));
    }

    [Fact]
    public void Cache_failure_is_ignored()
    {
        using var folder = new TempFolder();
        var blocker = folder.File("blocker");
        File.WriteAllText(blocker, "a file, not a folder");

        Assert.False(ShareCache.TryWrite(Path.Combine(blocker, "cache.json"), Json("{}")));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}