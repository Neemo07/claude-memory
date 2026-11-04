using System.IO;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Parsing;
using ClaudeMemory.Core.Services;
using ClaudeMemory.App.ViewModels;

namespace ClaudeMemory.App.Services;

/// <summary>What the "found on this machine" panel needs (gui.py:933-952, <c>do_scan</c>).</summary>
public sealed class ScanResult
{
    public required IReadOnlyList<SessionRow> Sessions { get; init; }

    /// <summary>The transcript root, shown in the header (gui.py:951).</summary>
    public required string Root { get; init; }

    public required string ProjectsDirectory { get; init; }

    public required string DefaultOutputDirectory { get; init; }
}

/// <summary>Lists the local transcripts without applying any filter, split out for tests.</summary>
public interface ISessionScanner
{
    ScanResult Scan();
}

/// <summary>
/// Read-only local inventory, ported from <c>gui.py do_scan</c> (gui.py:933-952). Discovery
/// order is preserved, which is what the reference table reverses for display.
/// </summary>
public sealed class SessionScanner : ISessionScanner
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _projectsDir;

    public SessionScanner(string? home = null, string? root = null, string? projectsDir = null)
    {
        _home = home ?? MemoryOptions.DefaultHome();
        _root = root ?? System.IO.Path.Combine(_home, ".claude");
        _projectsDir = projectsDir;
    }

    public ScanResult Scan()
    {
        var root = _root;
        var projects = _projectsDir ?? System.IO.Path.Combine(root, "projects");
        var rows = new List<SessionRow>();

        if (Directory.Exists(projects))
        {
            var options = new MemoryOptions { Home = _home, Root = root, Quiet = true };
            foreach (var path in SessionDiscovery.Discover(root, _projectsDir))
            {
                var session = JsonlSessionParser.ParseSession(path, options);
                if (session.Turns.Count == 0 && session.Summaries.Count == 0) continue;
                rows.Add(SessionRow.From(session, options));
            }
        }

        return new ScanResult
        {
            Sessions = rows,
            Root = root,
            ProjectsDirectory = projects,
            DefaultOutputDirectory = PathPolicy.DefaultOutputDirectory(),
        };
    }
}
