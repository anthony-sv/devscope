using System.Buffers.Binary;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Devscope.Core.Ear;

/// <summary>Kind of a line in a side-by-side diff.</summary>
public enum DiffLineKind
{
    Unchanged,
    Inserted,
    Deleted,
    Modified,
    Imaginary,
}

/// <summary>One row of a side-by-side text diff. Either side may be padding (<see cref="DiffLineKind.Imaginary"/>).</summary>
public sealed record SideBySideLine(
    int? LeftNumber, string LeftText, DiffLineKind LeftKind,
    int? RightNumber, string RightText, DiffLineKind RightKind)
{
    public bool IsChange => LeftKind != DiffLineKind.Unchanged || RightKind != DiffLineKind.Unchanged;
}

/// <summary>Content comparison of one entry, either as text lines or as a binary summary.</summary>
public sealed record ContentDiffResult(bool IsText, IReadOnlyList<SideBySideLine> Lines, string Summary);

/// <summary>Builds a readable comparison of two versions of a file.</summary>
public static class ContentDiff
{
    /// <summary>Text files larger than this are summarized instead of line-diffed.</summary>
    public const int MaxTextBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".properties", ".mf", ".txt", ".jsp", ".jspf", ".jspx", ".tag", ".tld", ".html", ".htm", ".xhtml",
        ".css", ".js", ".json", ".yml", ".yaml", ".sql", ".sh", ".bat", ".cmd", ".java", ".kt", ".groovy", ".xsd",
        ".xsl", ".xslt", ".wsdl", ".conf", ".cfg", ".ini", ".csv", ".md", ".vm", ".ftl", ".sf", ".list", ".policy",
    };

    /// <summary>Compares two byte arrays; either may be null when the file exists on one side only.</summary>
    public static ContentDiffResult Build(string path, byte[]? left, byte[]? right)
    {
        var largest = Math.Max(left?.Length ?? 0, right?.Length ?? 0);
        if (largest > MaxTextBytes || !IsText(path, left) || !IsText(path, right))
        {
            return new ContentDiffResult(false, [], BinarySummary(path, left, right));
        }

        return BuildText(Decode(left), Decode(right), summaryPrefix: null);
    }

    /// <summary>Line diff of two texts (e.g. decompiled sources). Null means the side does not exist.</summary>
    /// <param name="left">Old text.</param>
    /// <param name="right">New text.</param>
    /// <param name="summaryPrefix">Optional text put in front of the change count, e.g. which tool produced the text.</param>
    public static ContentDiffResult BuildText(string? left, string? right, string? summaryPrefix)
    {
        var model = SideBySideDiffBuilder.Diff(new Differ(), left ?? string.Empty, right ?? string.Empty, ignoreWhiteSpace: false);
        var lines = new List<SideBySideLine>(model.OldText.Lines.Count);
        for (var i = 0; i < model.OldText.Lines.Count; i++)
        {
            var o = model.OldText.Lines[i];
            var n = model.NewText.Lines[i];
            lines.Add(new SideBySideLine(
                o.Position, o.Text ?? string.Empty, Map(o.Type),
                n.Position, n.Text ?? string.Empty, Map(n.Type)));
        }

        var changes = lines.Count(l => l.IsChange);
        var summary = changes == 0 ? "Identical text" : $"{changes:N0} changed line(s)";
        return new ContentDiffResult(true, lines, summaryPrefix is null ? summary : $"{summaryPrefix} · {summary}");
    }

    /// <summary>Describes a Java version change between two class files, or null when unchanged/unknown.</summary>
    public static string? DescribeClassVersionChange(byte[]? left, byte[]? right)
    {
        if (left is null || right is null
            || !TryGetClassVersion(left, out var leftJava, out _)
            || !TryGetClassVersion(right, out var rightJava, out _))
        {
            return null;
        }

        return leftJava != rightJava ? $"Java {leftJava} → Java {rightJava}" : null;
    }

    /// <summary>Heuristic: known text extension, or no NUL bytes in the first 8 KB.</summary>
    public static bool IsText(string path, byte[]? content)
    {
        if (content is null || TextExtensions.Contains(Path.GetExtension(path)))
        {
            return true;
        }

        return !content.AsSpan(0, Math.Min(content.Length, 8192)).Contains((byte)0);
    }

    /// <summary>Reads the Java version a .class file targets from its header.</summary>
    public static bool TryGetClassVersion(ReadOnlySpan<byte> content, out int javaVersion, out int majorVersion)
    {
        javaVersion = majorVersion = 0;
        if (content.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(content) != 0xCAFEBABE)
        {
            return false;
        }

        majorVersion = BinaryPrimitives.ReadUInt16BigEndian(content[6..]);
        javaVersion = majorVersion >= 49 ? majorVersion - 44 : 1;
        return true;
    }

    private static string Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string BinarySummary(string path, byte[]? left, byte[]? right)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Describe("Left", path, left));
        sb.AppendLine(Describe("Right", path, right));
        if (left is not null && right is not null)
        {
            sb.AppendLine(left.AsSpan().SequenceEqual(right) ? "Content is identical." : "Binary content differs.");
        }

        return sb.ToString();
    }

    private static string Describe(string side, string path, byte[]? content)
    {
        if (content is null)
        {
            return $"{side}: (not present)";
        }

        var text = $"{side}: {content.Length:N0} bytes";
        if (path.EndsWith(".class", StringComparison.OrdinalIgnoreCase)
            && TryGetClassVersion(content, out var java, out var major))
        {
            text += $", compiled for Java {java} (class file version {major})";
        }

        return text;
    }

    private static DiffLineKind Map(ChangeType type) => type switch
    {
        ChangeType.Inserted => DiffLineKind.Inserted,
        ChangeType.Deleted => DiffLineKind.Deleted,
        ChangeType.Modified => DiffLineKind.Modified,
        ChangeType.Imaginary => DiffLineKind.Imaginary,
        _ => DiffLineKind.Unchanged,
    };
}
