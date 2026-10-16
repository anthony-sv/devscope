namespace Devscope.Core.Ear;

/// <summary>
/// Helpers for the virtual path format used to address files inside nested archives,
/// e.g. <c>web.war!/WEB-INF/lib/util.jar!/com/acme/Util.class</c>.
/// </summary>
public static class ArchivePath
{
    /// <summary>Separator between an archive and a path inside it.</summary>
    public const string NestedSeparator = "!/";

    private static readonly string[] ArchiveExtensions = [".ear", ".war", ".jar", ".rar", ".sar", ".zip"];

    /// <summary>Returns <see langword="true"/> when the path names a zip-based Java archive we should descend into.</summary>
    public static bool IsArchive(string path) =>
        ArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>Combines a container path and a child path.</summary>
    public static string Nest(string container, string child) =>
        string.IsNullOrEmpty(container) ? child : container + NestedSeparator + child;

    /// <summary>Splits a virtual path into its archive segments.</summary>
    public static string[] Split(string path) => path.Split(NestedSeparator);

    /// <summary>Normalizes separators to forward slashes and removes leading slashes.</summary>
    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Returns the containing archive path, or an empty string for top-level entries.</summary>
    public static string GetContainer(string path)
    {
        var index = path.LastIndexOf(NestedSeparator, StringComparison.Ordinal);
        return index < 0 ? string.Empty : path[..index];
    }
}
