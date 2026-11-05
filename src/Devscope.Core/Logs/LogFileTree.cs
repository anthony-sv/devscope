namespace Devscope.Core.Logs;

/// <summary>A folder or file in the tree of files a log session reads.</summary>
public sealed class LogFileTreeNode
{
    internal LogFileTreeNode(string name, string fullPath, string environment, bool isFile)
    {
        Name = name;
        FullPath = fullPath;
        Environment = environment;
        IsFile = isFile;
    }

    /// <summary>Display name: the file or folder name, or the environment or source path for top-level nodes.</summary>
    public string Name { get; internal set; }

    /// <summary>Full path of the file or folder; empty for an environment node.</summary>
    public string FullPath { get; internal set; }

    public string Environment { get; }

    public bool IsFile { get; }

    public List<LogFileTreeNode> Children { get; } = [];

    /// <summary>Every file at or below this node.</summary>
    public IEnumerable<LogFileTreeNode> Files() => IsFile ? [this] : Children.SelectMany(c => c.Files());
}

/// <summary>Builds the environment → source → folder → file tree shown next to the log list.</summary>
public static class LogFileTree
{
    /// <summary>
    /// Groups the files of every source by folder, relative to the source's root. The environment level is left out
    /// when there is only one environment. Folders holding a single folder are merged (<c>a/b/c</c>), as in an IDE.
    /// </summary>
    public static IReadOnlyList<LogFileTreeNode> Build(IReadOnlyList<SourceStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var environments = new List<LogFileTreeNode>();
        foreach (var group in statuses.GroupBy(s => s.Environment, StringComparer.Ordinal))
        {
            var environmentNode = new LogFileTreeNode(group.Key, string.Empty, group.Key, isFile: false);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var status in group)
            {
                var files = status.Files.Where(seen.Add).ToList();
                if (files.Count == 0)
                {
                    continue;
                }

                if (files.Count == 1 && string.Equals(files[0], status.Root, StringComparison.OrdinalIgnoreCase))
                {
                    // The source is a single file.
                    environmentNode.Children.Add(new LogFileTreeNode(Path.GetFileName(files[0]), files[0], group.Key, isFile: true));
                    continue;
                }

                var root = new LogFileTreeNode(status.Root, status.Root, group.Key, isFile: false);
                foreach (var file in files)
                {
                    AddFile(root, status.Root, file, group.Key);
                }

                Sort(root);
                foreach (var child in root.Children)
                {
                    Compact(child);
                }

                environmentNode.Children.Add(root);
            }

            environments.Add(environmentNode);
        }

        return environments.Count == 1 ? environments[0].Children : environments;
    }

    private static void AddFile(LogFileTreeNode root, string rootPath, string file, string environment)
    {
        var relative = Path.GetRelativePath(rootPath, file);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            root.Children.Add(new LogFileTreeNode(file, file, environment, isFile: true));
            return;
        }

        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var node = root;
        var path = rootPath;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            path = Path.Combine(path, parts[i]);
            var folder = node.Children.Find(c => !c.IsFile && string.Equals(c.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                folder = new LogFileTreeNode(parts[i], path, environment, isFile: false);
                node.Children.Add(folder);
            }

            node = folder;
        }

        node.Children.Add(new LogFileTreeNode(parts[^1], file, environment, isFile: true));
    }

    private static void Sort(LogFileTreeNode node)
    {
        // Folders first, then by name.
        node.Children.Sort((a, b) => a.IsFile != b.IsFile ? (a.IsFile ? 1 : -1) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        foreach (var child in node.Children)
        {
            Sort(child);
        }
    }

    private static void Compact(LogFileTreeNode node)
    {
        while (!node.IsFile && node.Children is [{ IsFile: false } only])
        {
            node.Name = node.Name + Path.DirectorySeparatorChar + only.Name;
            node.FullPath = only.FullPath;
            node.Children.Clear();
            node.Children.AddRange(only.Children);
        }

        foreach (var child in node.Children)
        {
            Compact(child);
        }
    }
}
