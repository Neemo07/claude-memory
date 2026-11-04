using System.Text;

namespace ClaudeMemory.Cli.Tests;

/// <summary>
/// The stdio contract of the real executable (claude_memory.py:328-346 <c>configure_stdio</c>):
/// UTF-8 without a BOM and LF newlines on stdout. Only a spawned process can prove this, so it
/// is not covered by the in-process runner tests.
/// </summary>
public sealed class CliProcessTests
{
    [Fact]
    public void Stdout_is_utf8_without_a_bom_and_lf_only()
    {
        var (code, bytes, stderr) = CliProcess.Run("--root", TestPaths.FixtureRoot);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);

        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "stdout must not start with a UTF-8 BOM");
        Assert.DoesNotContain((byte)'\r', bytes);

        var text = CliProcess.DecodeUtf8(bytes);
        Assert.Equal(Golden.Normalise(Golden.ReadGolden("memory-plain.md")), Golden.Normalise(text));
    }

    [Fact]
    public void Help_on_a_real_console_uses_the_frozen_layout()
    {
        var (code, bytes, _) = CliProcess.Run("--help");

        Assert.Equal(0, code);
        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "stdout must not start with a UTF-8 BOM");

        var text = CliProcess.DecodeUtf8(bytes);
        var expected = Golden.ReadGolden("cli-help.txt")
            .Replace("%DEFAULT_ROOT%", ClaudeMemory.Core.Model.MemoryOptions.DefaultRoot(), StringComparison.Ordinal);

        // COLUMNS=80 is forced by the harness, which is how the golden was captured.
        Assert.Equal(expected, text);
    }

    [Fact]
    public void A_usage_error_goes_to_stderr_and_stdout_stays_empty()
    {
        var (code, bytes, stderr) = CliProcess.Run("--bogus");

        Assert.Equal(2, code);
        Assert.Empty(bytes);
        Assert.EndsWith(
            "ClaudeMemory.Cli: error: unrecognized arguments: --bogus\n", stderr, StringComparison.Ordinal);
        Assert.StartsWith("usage: ClaudeMemory.Cli", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_identity_is_exact()
    {
        var (code, bytes, _) = CliProcess.Run("--version");

        Assert.Equal(0, code);
        Assert.Equal("ClaudeMemory.Cli 1.1.0\n", CliProcess.DecodeUtf8(bytes));
    }

    [Fact]
    public void Non_ascii_output_survives_the_round_trip()
    {
        // The fixture corpus contains Arabic text; a cp1252 fallback would corrupt or throw.
        var (code, bytes, _) = CliProcess.Run("--root", TestPaths.FixtureRoot, "--mode", "json");

        Assert.Equal(0, code);
        var text = CliProcess.DecodeUtf8(bytes);
        Assert.Contains("\u0645", text, StringComparison.Ordinal); // ARABIC LETTER MEEM
        Assert.DoesNotContain("\\u0645", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lf_only_output_has_no_carriage_returns()
    {
        var (_, bytes, _) = CliProcess.Run("--root", TestPaths.FixtureRoot, "--list");

        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Contains((byte)'\n', bytes);
        Assert.True(new UTF8Encoding(false, true).GetString(bytes).Length > 0);
    }
}
