namespace Devscope.Core.Workspaces;

/// <summary>
/// A named set of environments for one application, e.g. "Billing" with local, test, beta (2 nodes) and prod (2 nodes).
/// Persisted locally as JSON; nothing is sent anywhere.
/// </summary>
public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New workspace";

    public List<WorkspaceEnvironment> Environments { get; set; } = [];

    /// <summary>Custom colouring rules applied in the log viewer.</summary>
    public List<HighlightRule> Highlights { get; set; } = [];
}

/// <summary>One environment (or cluster node) of an application.</summary>
public sealed class WorkspaceEnvironment
{
    public string Name { get; set; } = "env";

    public List<WorkspaceSource> Sources { get; set; } = [];
}

/// <summary>A file or folder to read logs from. Local paths and UNC paths (<c>\\server\share\logs</c>) are both fine.</summary>
public sealed class WorkspaceSource
{
    /// <summary>Default file patterns for folders.</summary>
    public const string DefaultIncludePatterns = "*.log;*.txt;*.out";

    public string Path { get; set; } = string.Empty;

    /// <summary>Semicolon-separated wildcard patterns, used when <see cref="Path"/> is a folder.</summary>
    public string IncludePatterns { get; set; } = DefaultIncludePatterns;

    /// <summary>Whether sub-folders are searched.</summary>
    public bool Recursive { get; set; } = true;
}

/// <summary>Colours matching text in the log viewer.</summary>
public sealed class HighlightRule
{
    public string Pattern { get; set; } = string.Empty;

    public bool IsRegex { get; set; }

    /// <summary>Colour as <c>#RRGGBB</c>.</summary>
    public string Color { get; set; } = "#E3B341";
}
