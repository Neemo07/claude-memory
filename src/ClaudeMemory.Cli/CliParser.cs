using System.Text.RegularExpressions;
using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Cli;

internal enum CliAction
{
    Run,
    Help,
    Version,
}

internal sealed record CliParseResult(CliAction Action, MemoryOptions Options);

/// <summary>
/// argparse-compatible argument parsing for the reference option table
/// (claude_memory.py:1795-1872).
/// </summary>
/// <remarks>
/// Reproduced behaviours: <c>--name value</c> and <c>--name=value</c>, unambiguous long-option
/// prefixes, <c>--</c> and unknown tokens collected as "unrecognized arguments", negative
/// numbers accepted as values, and the reference error wording and exit code (2). Anything
/// after <c>-h</c> or <c>--version</c> is ignored, exactly like argparse's immediate actions.
/// </remarks>
internal static class CliParser
{
    /// <summary>argparse's <c>_negative_number_matcher</c>: a value may look like this.</summary>
    private static readonly Regex NegativeNumber = new(@"^-\d+$|^-\d*\.\d+$", RegexOptions.Compiled);

    /// <summary>Parses <paramref name="args"/> into <paramref name="options"/>.</summary>
    /// <exception cref="CliUsageException">The arguments are invalid (exit code 2).</exception>
    public static CliParseResult Parse(
        string[] args, IReadOnlyList<OptionSpec> specs, MemoryOptions options)
    {
        var extras = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];

            if (token == "--")
            {
                // argparse 3.12 keeps the marker itself in the unrecognised list and stops
                // option processing at it.
                for (var rest = index; rest < args.Length; rest++) extras.Add(args[rest]);
                break;
            }

            if (token.Length > 2 && token.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = token.IndexOf('=');
                var name = equals < 0 ? token : token[..equals];
                var inline = equals < 0 ? null : token[(equals + 1)..];
                var spec = Resolve(specs, name);
                if (spec is null)
                {
                    extras.Add(token);
                    continue;
                }

                if (spec.Kind != OptionKind.Value)
                {
                    if (inline is not null)
                    {
                        throw new CliUsageException(
                            $"argument {ActionName(spec)}: ignored explicit argument '{inline}'");
                    }

                    if (spec.Kind == OptionKind.Help) return new CliParseResult(CliAction.Help, options);
                    if (spec.Kind == OptionKind.Version) return new CliParseResult(CliAction.Version, options);

                    spec.Assign!(options, "");
                    continue;
                }

                string value;
                if (inline is not null)
                {
                    value = inline;
                }
                else
                {
                    if (index + 1 >= args.Length || !CanBeValue(args[index + 1]))
                    {
                        throw new CliUsageException(
                            $"argument {ActionName(spec)}: expected one argument");
                    }

                    value = args[++index];
                }

                Apply(spec, options, value);
                continue;
            }

            if (token.Length > 1 && token[0] == '-')
            {
                // The reference defines only -h as a short option; -h wins immediately and
                // every other short token is an unrecognised argument.
                if (token[1] == 'h') return new CliParseResult(CliAction.Help, options);
                extras.Add(token);
                continue;
            }

            extras.Add(token);
        }

        if (extras.Count > 0)
        {
            throw new CliUsageException("unrecognized arguments: " + string.Join(" ", extras));
        }

        return new CliParseResult(CliAction.Run, options);
    }

    private static void Apply(OptionSpec spec, MemoryOptions options, string value)
    {
        try
        {
            spec.Assign!(options, value);
        }
        catch (CliValueException error)
        {
            throw new CliUsageException($"argument {ActionName(spec)}: {error.Message}");
        }
    }

    private static bool CanBeValue(string token)
        => token.Length <= 1 || token[0] != '-' || NegativeNumber.IsMatch(token);

    private static OptionSpec? Resolve(IReadOnlyList<OptionSpec> specs, string name)
    {
        OptionSpec? exact = null;
        var candidates = new List<OptionSpec>();

        foreach (var spec in specs)
        {
            foreach (var candidate in spec.Names)
            {
                if (!candidate.StartsWith("--", StringComparison.Ordinal)) continue;

                if (candidate == name)
                {
                    exact = spec;
                }
                else if (candidate.StartsWith(name, StringComparison.Ordinal) && !candidates.Contains(spec))
                {
                    candidates.Add(spec);
                }
            }
        }

        if (exact is not null) return exact;
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var matches = candidates
            .SelectMany(spec => spec.Names)
            .Where(candidate => candidate.StartsWith(name, StringComparison.Ordinal));

        throw new CliUsageException(
            $"ambiguous option: {name} could match {string.Join(", ", matches)}");
    }

    private static string ActionName(OptionSpec spec) => string.Join("/", spec.Names);
}