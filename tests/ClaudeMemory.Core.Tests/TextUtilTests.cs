using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Vectors are copied from the Python reference output (claude_memory.py 1.1.0) so any
/// behaviour drift shows up as a failure here.
/// </summary>
public sealed class TextUtilTests
{
    [Fact]
    public void OneLine_CollapsesWhitespace()
        => Assert.Equal("a b c", TextUtil.OneLine("  a\n b\t\tc  "));

    [Fact]
    public void OneLine_ShortTextIsUnchanged()
        => Assert.Equal("hello", TextUtil.OneLine("hello", 10));

    [Fact]
    public void OneLine_LongTextIsClippedWithEllipsis()
    {
        var result = TextUtil.OneLine(string.Concat(Enumerable.Repeat("word ", 200)));
        Assert.Equal(400, result.Length);
        Assert.EndsWith("\u2026", result);
        Assert.DoesNotContain("  ", result);
    }

    [Fact]
    public void OneLine_ClipsALongWordToTheLimit()
        => Assert.Equal(new string('a', 399) + "\u2026", TextUtil.OneLine(new string('a', 500), 400));

    [Fact]
    public void Sentences_SplitsProseKeepsBulletsAndStripsMarkers()
    {
        const string text = "Decision: use X. Convention: always Y.\n- bullet one\n- bullet two\n\n## Heading line";
        Assert.Equal(
            new[] { "Decision: use X.", "Convention: always Y.", "bullet one", "bullet two", "Heading line" },
            TextUtil.Sentences(text));
    }

    [Fact]
    public void Sentences_SplitsArabicOnArabicPunctuation()
        => Assert.Equal(
            new[] { "قررنا استخدام Markdown.", "يجب ألا نرسل النصوص." },
            TextUtil.Sentences("قررنا استخدام Markdown. يجب ألا نرسل النصوص."));

    [Fact]
    public void Sentences_KeepsAnArabicListLineWhole()
        => Assert.Equal(new[] { "الخطوات التالية: اختبر الواجهة ثم احفظ الملف." },
            TextUtil.Sentences("الخطوات التالية: اختبر الواجهة ثم احفظ الملف."));

    [Fact]
    public void Dedupe_KeepsFirstOccurrenceAndStopsAtLimit()
    {
        var (items, dropped) = TextUtil.Dedupe(new[] { "a b", "A-B", "", "c", "c d" }, 5);
        Assert.Equal(new[] { "a b", "c", "c d" }, items);
        Assert.Equal(2, dropped);

        var (limited, limitedDropped) = TextUtil.Dedupe(new[] { "a", "b", "c", "d" }, 2);
        Assert.Equal(new[] { "a", "b" }, limited);
        Assert.Equal(0, limitedDropped);
    }

    [Fact]
    public void DedupeRecent_NewestWins()
    {
        var (items, dropped) = TextUtil.DedupeRecent(new[] { "old thing", "mid", "OLD THING", "new thing" }, 5);
        Assert.Equal(new[] { "mid", "OLD THING", "new thing" }, items);
        Assert.Equal(1, dropped);

        var (limited, limitedDropped) = TextUtil.DedupeRecent(new[] { "a", "b", "c", "d" }, 2);
        Assert.Equal(new[] { "c", "d" }, limited);
        Assert.Equal(0, limitedDropped);
    }

    [Fact]
    public void NormKey_PreservesArabicAndDropsPunctuation()
    {
        Assert.Equal("helloworld123", TextUtil.NormKey("Hello, World! -- 123"));
        Assert.Equal("القراراتالمتخذة", TextUtil.NormKey("القرارات: المتخذة!"));
    }

    [Fact]
    public void FmtCwd_UsesHomeRelativeFormWhenUnderHome()
    {
        const string home = @"C:\Users\ImPro";
        Assert.Equal("~/Desktop/x", TextUtil.FmtCwd(@"C:\Users\ImPro\Desktop\x", home));
        Assert.Equal("~/Desktop/x", TextUtil.FmtCwd("C:/Users/ImPro/Desktop/x", home));
        Assert.Equal("~/Desktop/x", TextUtil.FmtCwd(@"c:\users\impro\Desktop\x", home));
        Assert.Equal("~/.", TextUtil.FmtCwd(home, home));
    }

    [Fact]
    public void FmtCwd_FallsBackToForwardSlashedText()
    {
        const string home = @"C:\Users\ImPro";
        Assert.Equal("C:/work/demo", TextUtil.FmtCwd("C:/work/demo", home));
        Assert.Equal("C:/Users/ImProX/y", TextUtil.FmtCwd(home + @"X\y", home));
        Assert.Equal("(unknown cwd)", TextUtil.FmtCwd("", home));
        Assert.Equal("(unknown cwd)", TextUtil.FmtCwd(null, home));
    }

    [Fact]
    public void FmtCwd_ClipsToLastSixtyNineCharacters()
        => Assert.Equal("\u2026" + new string('x', 69), TextUtil.FmtCwd("c:/" + new string('x', 100), null));

    [Theory]
    [InlineData(0.9, "0s")]
    [InlineData(59.9, "59s")]
    [InlineData(60, "1m 0s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(3600, "1h 0m")]
    [InlineData(7325, "2h 2m")]
    public void FmtDur_MatchesReference(double seconds, string expected)
        => Assert.Equal(expected, TextUtil.FmtDur(seconds));

    [Fact]
    public void ClipMiddle_KeepsStartAndEnd()
    {
        var (shortText, shortClipped) = TextUtil.ClipMiddle("abc", 10);
        Assert.Equal("abc", shortText);
        Assert.False(shortClipped);

        var (text, clipped) = TextUtil.ClipMiddle(new string('A', 100), 20);
        Assert.True(clipped);
        Assert.Equal("AAAAAAAAAAA\n\n\u2026 [80 chars omitted] \u2026\n\nAAAAAAAAA", text);
    }
}