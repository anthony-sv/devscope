using System.IO.Enumeration;
using Devscope.Core.Workspaces;

namespace Devscope.Core.Logs;

/// <summary>Expands a <see cref="WorkspaceSource"/> (file or folder) into concrete log files.</summary>
public static class LogSourceResolver
{
    private static readonly string[] CompressedExtensions = [".gz", ".zip", ".7z", ".bz2", ".xz", ".tar"];

    /// <summary>Lists matching files, newest first, capped at <paramref name="maxFiles"/>.</summary>
    /// <exception cref="DirectoryNotFoundException">The path is neither an existing file nor a folder.</exception>
    public static IReadOnlyList<string> Resolve(WorkspaceSource source, int maxFiles)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = Environment.ExpandEnvironmentVariables(source.Path.Trim().Trim('"'));

        if (File.Exists(path))
        {
            return [Path.GetFullPath(path)];
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"'{path}' is not reachable.");
        }

        var patterns = (string.IsNullOrWhiteSpace(source.IncludePatterns) ? WorkspaceSource.DefaultIncludePatterns : source.IncludePatterns)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = source.Recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        // FileSystemEnumerable gives us the last-write time without an extra round trip per file (matters on SMB).
        var files = new FileSystemEnumerable<(string Path, DateTimeOffset Written)>(
            path,
            (ref FileSystemEntry e) => (e.ToFullPath(), e.LastWriteTimeUtc),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && Matches(e.FileName, patterns),
        };

        return files
            .OrderByDescending(f => f.Written)
            .Take(maxFiles)
            .Select(f => f.Path)
            .ToList();
    }

    private static bool Matches(ReadOnlySpan<char> fileName, string[] patterns)
    {
        foreach (var ext in CompressedExtensions)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }
}
