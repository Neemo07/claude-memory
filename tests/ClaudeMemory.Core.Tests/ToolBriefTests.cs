using System.Text.Json;
using ClaudeMemory.Core.Parsing;

namespace ClaudeMemory.Core.Tests;

/// <summary>Vectors copy the Python reference output for claude_memory.py:463-510.</summary>
public sealed class ToolBriefTests
{
    private static JsonElement? Json(string? text)
    {
        if (text is null) return null;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("Read", """{"file_path":"C:\\testproj\\src\\net\\packet.cpp"}""", @"file_path=C:\testproj\src\net\packet.cpp")]
    [InlineData("Bash", """{"command":"python build_full.py --workers 4","description":"Build the project"}""", "command=python build_full.py --workers 4")]
    [InlineData("Task", """{"subagent_type":"explore","description":"inspect preview code","prompt":"read Preview.cs"}""", "explore: inspect preview code")]
    [InlineData("Task", """{"description":"no type here"}""", "agent: no type here")]
    [InlineData("Edit", """{"file_path":"a.cs","old_string":"x","new_string":"y"}""", "file_path=a.cs")]
    [InlineData("Glob", """{"pattern":"**/build*.py"}""", "pattern=**/build*.py")]
    [InlineData("PresentFiles", """{"filepaths":["a.md","b.md","c.md","d.md","e.md"]}""", "filepaths=a.md, b.md, c.md, d.md (+1)")]
    [InlineData("PresentFiles", """{"filepaths":["a",5,true,null,"z"]}""", "filepaths=a, 5, True (+2)")]
    [InlineData("MultiEdit", """{"edits":[1,2]}""", "edits")]
    [InlineData("Weird", """{"zz":1,"aa":2}""", "aa, zz")]
    [InlineData("Empty", "{}", "(no args)")]
    [InlineData("Write", "{}", "")]
    [InlineData("Read", """{"file_path":"   "}""", "file_path")]
    public void Brief_MatchesReference(string name, string input, string expected)
        => Assert.Equal(expected, ToolBrief.Brief(name, Json(input)));

    [Fact]
    public void Brief_AcceptsNonObjectInputs()
    {
        Assert.Equal("just a string", ToolBrief.Brief("NotDict", Json("\"just a string\"")));
        Assert.Equal("", ToolBrief.Brief("NoneInput", null));
    }

    [Fact]
    public void Brief_ClipsToTheLimit()
    {
        var brief = ToolBrief.Brief("Read", Json("""{"file_path":"%s"}""".Replace("%s", new string('p', 300))), 200);
        Assert.Equal(200, brief.Length);
        Assert.StartsWith("file_path=", brief);
        Assert.EndsWith("\u2026", brief);

        var shortBrief = ToolBrief.Brief("Read", Json("""{"file_path":"%s"}""".Replace("%s", new string('q', 300))), 120);
        Assert.Equal(120, shortBrief.Length);
    }

    [Fact]
    public void Brief_ClipsATaskDescriptionAroundTheAgentName()
    {
        var payload = $$"""{"subagent_type":"{{new string('s', 60)}}","description":"{{new string('d', 300)}}"}""";
        var brief = ToolBrief.Brief("Task", Json(payload));

        // who is clipped to 40 characters (39 + ellipsis), then the description gets 160-40-3.
        Assert.Equal("sssssssssssssssssssssssssssssssssssssss\u2026: ", brief[..42]);
        Assert.Equal(159, brief.Length);
    }

    [Fact]
    public void BodyQuotes_ReadsStringsAndTextBlocks()
    {
        Assert.Equal("plain text", ToolBrief.BodyQuotes(Json("""{"content":"plain text"}""")!.Value));
        Assert.Equal("a\nb", ToolBrief.BodyQuotes(Json("""{"content":[{"type":"text","text":"a"},{"type":"image"},{"type":"text","text":"b"}]}""")!.Value));
        Assert.Equal("", ToolBrief.BodyQuotes(Json("{}")!.Value));
        Assert.Equal("", ToolBrief.BodyQuotes(Json("""{"content":5}""")!.Value));
    }
}