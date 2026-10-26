using System.Globalization;
using System.Text;
using Devscope.Core.Ear;

namespace Devscope.Core.Database;

/// <summary>
/// One object of a database comparison. <see cref="DiffStatus.Removed"/> means only in the left database,
/// <see cref="DiffStatus.Added"/> only in the right one.
/// </summary>
public sealed record DbDiffEntry(DbObjectType Type, string Schema, string Name, DiffStatus Status, DbObject? Left, DbObject? Right)
{
    /// <summary>The left object scripted with the comparison's options; null when it only exists on the right.</summary>
    public string? LeftScript { get; init; }

    public string? RightScript { get; init; }

    /// <summary>What differs, one sentence per difference, e.g. <c>Column [Email] only in right: nvarchar(200) NULL</c>.</summary>
    public IReadOnlyList<string> Differences { get; init; } = [];

    public string FullName => $"{Schema}.{Name}";
}

/// <summary>Result of comparing two database schemas.</summary>
public sealed record DbComparison(DatabaseSchema Left, DatabaseSchema Right, DbCompareOptions Options, IReadOnlyList<DbDiffEntry> Entries)
{
    /// <summary>Database-level differences: collation, compatibility level, server version.</summary>
    public IReadOnlyList<string> DatabaseDifferences { get; } = SchemaComparer.DescribeDatabases(Left.Info, Right.Info);

    public int OnlyInLeft => Count(DiffStatus.Removed);

    public int OnlyInRight => Count(DiffStatus.Added);

    public int Different => Count(DiffStatus.Modified);

    public int Identical => Count(DiffStatus.Unchanged);

    private int Count(DiffStatus status) => Entries.Count(e => e.Status == status);
}

/// <summary>Compares two database schemas object by object.</summary>
public static class SchemaComparer
{
    /// <summary>Matches objects by type, schema and name (case-insensitively) and compares their scripts.</summary>
    public static DbComparison Compare(DatabaseSchema left, DatabaseSchema right, DbCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(options);

        var leftByKey = ByKey(left.Objects);
        var rightByKey = ByKey(right.Objects);
        var keys = new SortedSet<(DbObjectType Type, string Name)>(
            leftByKey.Keys.Concat(rightByKey.Keys),
            Comparer<(DbObjectType Type, string Name)>.Create((a, b) =>
                a.Type != b.Type ? a.Type.CompareTo(b.Type) : DbNames.Comparer.Compare(a.Name, b.Name)));

        var entries = new List<DbDiffEntry>(keys.Count);
        foreach (var key in keys)
        {
            var l = leftByKey.GetValueOrDefault(key);
            var r = rightByKey.GetValueOrDefault(key);
            entries.Add(CompareObject(l, r, options));
        }

        return new DbComparison(left, right, options, entries);
    }

    /// <summary>Compares one object; either side may be missing.</summary>
    public static DbDiffEntry CompareObject(DbObject? left, DbObject? right, DbCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var any = left ?? right ?? throw new ArgumentException("At least one side is needed.", nameof(left));
        var leftScript = left is null ? null : DbScripter.Script(left, options);
        var rightScript = right is null ? null : DbScripter.Script(right, options);
        var entry = new DbDiffEntry(any.Type, any.Schema, any.Name, DiffStatus.Unchanged, left, right)
        {
            LeftScript = leftScript,
            RightScript = rightScript,
        };

        if (left is null || right is null)
        {
            return entry with { Status = left is null ? DiffStatus.Added : DiffStatus.Removed };
        }

        if (string.Equals(leftScript, rightScript, StringComparison.Ordinal))
        {
            return entry;
        }

        if (options.IgnoreWhitespace && string.Equals(CollapseWhitespace(leftScript!), CollapseWhitespace(rightScript!), StringComparison.Ordinal))
        {
            return entry with { Differences = ["Only whitespace differs (ignored)"] };
        }

        var differences = left.Table is { } leftTable && right.Table is { } rightTable
            ? DescribeTable(leftTable, rightTable, options)
            : DescribeDefinition(left, right, options);
        if (!string.Equals(left.FullName, right.FullName, StringComparison.Ordinal))
        {
            differences.Insert(0, $"Name differs in case: {DbNames.Quote(left.Schema, left.Name)} → {DbNames.Quote(right.Schema, right.Name)}");
        }

        if (differences.Count == 0)
        {
            differences.Add("The scripts differ");
        }

        return entry with { Status = DiffStatus.Modified, Differences = differences };
    }

    /// <summary>Explains how two versions of a table differ, one sentence per difference.</summary>
    public static List<string> DescribeTable(TableDefinition left, TableDefinition right, DbCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(options);

        var lines = new List<string>();
        if (left.Columns.Count != right.Columns.Count)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{left.Columns.Count} {Plural(left.Columns.Count, "column")} → {right.Columns.Count} {Plural(right.Columns.Count, "column")}"));
        }

        foreach (var column in left.Columns.Where(c => right.FindColumn(c.Name) is null))
        {
            lines.Add($"Column {DbNames.Quote(column.Name)} only in left: {DescribeColumn(column, options)}");
        }

        foreach (var column in right.Columns.Where(c => left.FindColumn(c.Name) is null))
        {
            lines.Add($"Column {DbNames.Quote(column.Name)} only in right: {DescribeColumn(column, options)}");
        }

        foreach (var l in left.Columns)
        {
            if (right.FindColumn(l.Name) is { } r && DescribeColumnChanges(l, r, options) is { Count: > 0 } changes)
            {
                lines.Add($"Column {DbNames.Quote(l.Name)}: {string.Join("; ", changes)}");
            }
        }

        if (!options.IgnoreColumnOrder)
        {
            var leftOrder = left.Columns.Where(c => right.FindColumn(c.Name) is not null).Select(c => c.Name).ToList();
            var rightOrder = right.Columns.Where(c => left.FindColumn(c.Name) is not null).Select(c => c.Name).ToList();
            var first = Enumerable.Range(0, leftOrder.Count).FirstOrDefault(i => !DbNames.Comparer.Equals(leftOrder[i], rightOrder[i]), -1);
            if (first >= 0)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Column order differs from position {first + 1}: {Abbreviate(leftOrder, first)} → {Abbreviate(rightOrder, first)}"));
            }
        }

        var leftPk = left.PrimaryKey is { } lpk ? ConstraintText(lpk.Name, lpk.IsSystemNamed, DbScripter.KeyText(lpk, true), options) : null;
        var rightPk = right.PrimaryKey is { } rpk ? ConstraintText(rpk.Name, rpk.IsSystemNamed, DbScripter.KeyText(rpk, true), options) : null;
        if (!string.Equals(leftPk, rightPk, StringComparison.Ordinal))
        {
            lines.Add((leftPk, rightPk) switch
            {
                (null, _) => $"Primary key only in right: {rightPk}",
                (_, null) => $"Primary key only in left: {leftPk}",
                _ => $"Primary key: {leftPk} → {rightPk}",
            });
        }

        CompareItems(lines, "Unique constraint", left.UniqueConstraints, right.UniqueConstraints,
            u => u.Name, u => u.IsSystemNamed, u => DbScripter.KeyText(u, false), options);
        CompareItems(lines, "Index", left.Indexes, right.Indexes,
            i => i.Name, _ => false, i => DbScripter.IndexText(i) + (i.IsDisabled ? " (disabled)" : string.Empty), options);
        CompareItems(lines, "Foreign key", left.ForeignKeys, right.ForeignKeys,
            fk => fk.Name, fk => fk.IsSystemNamed, fk => DbScripter.ForeignKeyText(fk) + (fk.IsDisabled ? " (disabled)" : string.Empty), options);
        CompareItems(lines, "Check constraint", left.CheckConstraints, right.CheckConstraints,
            c => c.Name, c => c.IsSystemNamed, c => DbScripter.CheckText(c) + (c.IsDisabled ? " (disabled)" : string.Empty), options);
        return lines;
    }

    /// <summary>Database-level differences worth knowing before reading the object list.</summary>
    public static IReadOnlyList<string> DescribeDatabases(DatabaseInfo left, DatabaseInfo right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var lines = new List<string>();
        if (!string.Equals(left.Collation, right.Collation, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Default collation: {left.Collation} → {right.Collation}");
        }

        if (left.CompatibilityLevel != right.CompatibilityLevel)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Compatibility level: {left.CompatibilityLevel} → {right.CompatibilityLevel}"));
        }

        if (!string.Equals(left.ProductVersion, right.ProductVersion, StringComparison.Ordinal))
        {
            lines.Add($"SQL Server version: {left.ProductVersion} → {right.ProductVersion}");
        }

        return lines;
    }

    /// <summary>A column as listed in a difference, e.g. <c>nvarchar(200) NULL</c>.</summary>
    private static string DescribeColumn(ColumnDefinition column, DbCompareOptions options)
    {
        var text = DbScripter.ColumnText(column, options);
        return text[(DbNames.Quote(column.Name).Length + 1)..];
    }

    private static List<string> DescribeColumnChanges(ColumnDefinition l, ColumnDefinition r, DbCompareOptions options)
    {
        var changes = new List<string>();
        if (l.IsComputed || r.IsComputed)
        {
            if (!string.Equals(l.ComputedExpression, r.ComputedExpression, StringComparison.Ordinal) || l.IsPersisted != r.IsPersisted)
            {
                changes.Add($"{DescribeColumn(l, options)} → {DescribeColumn(r, options)}");
            }

            return changes;
        }

        if (!string.Equals(l.DataType, r.DataType, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"{l.DataType} → {r.DataType}");
        }

        if (l.IsNullable != r.IsNullable)
        {
            changes.Add(l.IsNullable ? "NULL → NOT NULL" : "NOT NULL → NULL");
        }

        if (!options.IgnoreCollation && !string.Equals(l.Collation, r.Collation, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"collation {l.Collation ?? "(none)"} → {r.Collation ?? "(none)"}");
        }

        if (l.Identity != r.Identity)
        {
            changes.Add($"{IdentityText(l.Identity)} → {IdentityText(r.Identity)}");
        }

        var leftDefault = l.Default is { } ld ? ConstraintText(ld.Name, ld.IsSystemNamed, "DEFAULT " + ld.Expression, options) : "no default";
        var rightDefault = r.Default is { } rd ? ConstraintText(rd.Name, rd.IsSystemNamed, "DEFAULT " + rd.Expression, options) : "no default";
        if (!string.Equals(leftDefault, rightDefault, StringComparison.Ordinal))
        {
            changes.Add($"{leftDefault} → {rightDefault}");
        }

        if (l.IsSparse != r.IsSparse)
        {
            changes.Add(l.IsSparse ? "SPARSE → not sparse" : "not sparse → SPARSE");
        }

        if (l.IsRowGuid != r.IsRowGuid)
        {
            changes.Add(l.IsRowGuid ? "ROWGUIDCOL → not ROWGUIDCOL" : "not ROWGUIDCOL → ROWGUIDCOL");
        }

        return changes;
    }

    private static string IdentityText(IdentityDefinition? identity) =>
        identity is null ? "no identity" : $"IDENTITY({identity.Seed}, {identity.Increment})";

    /// <summary>
    /// Matches named items (indexes, constraints) by name, and items whose name is ignored by their definition, then
    /// lists what is only on one side or defined differently. Leftovers with the same definition are reported as a rename.
    /// </summary>
    private static void CompareItems<T>(
        List<string> lines,
        string label,
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        Func<T, string> name,
        Func<T, bool> isSystemNamed,
        Func<T, string> definition,
        DbCompareOptions options)
    {
        string Identity(T item) => DbScripter.ShowName(isSystemNamed(item), options) ? "n:" + name(item) : "d:" + definition(item);
        string Describe(T item) => DbScripter.ShowName(isSystemNamed(item), options)
            ? $"{label} {DbNames.Quote(name(item))}"
            : $"{label} {definition(item)}";

        var unmatchedRight = right.ToList();
        var onlyLeft = new List<T>();
        foreach (var l in left)
        {
            var index = unmatchedRight.FindIndex(r => DbNames.Comparer.Equals(Identity(l), Identity(r)));
            if (index < 0)
            {
                onlyLeft.Add(l);
                continue;
            }

            var r = unmatchedRight[index];
            unmatchedRight.RemoveAt(index);
            if (!string.Equals(definition(l), definition(r), StringComparison.Ordinal))
            {
                lines.Add($"{Describe(l)}: {definition(l)} → {definition(r)}");
            }
        }

        foreach (var l in onlyLeft)
        {
            // Same definition under another name (or a generated name on one side only).
            var index = unmatchedRight.FindIndex(r => string.Equals(definition(l), definition(r), StringComparison.Ordinal));
            if (index >= 0)
            {
                var r = unmatchedRight[index];
                unmatchedRight.RemoveAt(index);
                lines.Add($"{label} {definition(l)}: name {DisplayName(l)} → {DisplayName(r)}");
                continue;
            }

            lines.Add(DbScripter.ShowName(isSystemNamed(l), options)
                ? $"{Describe(l)} only in left: {definition(l)}"
                : $"{Describe(l)} only in left");
        }

        foreach (var r in unmatchedRight)
        {
            lines.Add(DbScripter.ShowName(isSystemNamed(r), options)
                ? $"{Describe(r)} only in right: {definition(r)}"
                : $"{Describe(r)} only in right");
        }

        string DisplayName(T item) => isSystemNamed(item) ? $"{DbNames.Quote(name(item))} (generated)" : DbNames.Quote(name(item));
    }

    /// <summary>Differences of objects compared by their script: changed lines, e.g. <c>INCREMENT BY 1 → INCREMENT BY 10</c>.</summary>
    private static List<string> DescribeDefinition(DbObject left, DbObject right, DbCompareOptions options)
    {
        var lines = new List<string>();
        if (left.IsDisabled != right.IsDisabled)
        {
            lines.Add(left.IsDisabled ? "Disabled → enabled" : "Enabled → disabled");
        }

        if (left.Definition is null || right.Definition is null)
        {
            lines.Add("The definition is not visible on " + (left.Definition is null && right.Definition is null ? "either side"
                : left.Definition is null ? "the left side" : "the right side") + " (encrypted, or no VIEW DEFINITION permission)");
            return lines;
        }

        // The object's own text, without what the scripts add (such as DISABLE TRIGGER).
        if (string.Equals(left.Definition, right.Definition, StringComparison.Ordinal)
            || (options.IgnoreWhitespace && string.Equals(CollapseWhitespace(left.Definition), CollapseWhitespace(right.Definition), StringComparison.Ordinal)))
        {
            return lines;
        }

        var diff = ContentDiff.BuildText(left.Definition, right.Definition, summaryPrefix: null);
        if (left.Type is DbObjectType.Sequence or DbObjectType.Synonym or DbObjectType.Type)
        {
            // Short scripts with one property per line: name the lines.
            foreach (var line in diff.Lines.Where(l => l.IsChange))
            {
                lines.Add((line.LeftKind, line.RightKind) switch
                {
                    (DiffLineKind.Modified, _) => $"{line.LeftText.Trim()} → {line.RightText.Trim()}",
                    (DiffLineKind.Deleted, _) => $"Only in left: {line.LeftText.Trim()}",
                    (_, DiffLineKind.Inserted) => $"Only in right: {line.RightText.Trim()}",
                    _ => $"{line.LeftText.Trim()} → {line.RightText.Trim()}",
                });
            }
        }
        else
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Definition differs: +{diff.Stats.Additions:N0} −{diff.Stats.Deletions:N0} lines"));
        }

        return lines;
    }

    private static string ConstraintText(string name, bool isSystemNamed, string definition, DbCompareOptions options) =>
        DbScripter.ShowName(isSystemNamed, options) ? $"CONSTRAINT {DbNames.Quote(name)} {definition}" : definition;

    // Keyed by type and "schema\u0001name": sorts by schema, then name.
    private static Dictionary<(DbObjectType Type, string Name), DbObject> ByKey(IEnumerable<DbObject> objects)
    {
        var result = new Dictionary<(DbObjectType Type, string Name), DbObject>(KeyComparer.Instance);
        foreach (var obj in objects)
        {
            result.TryAdd((obj.Type, obj.Schema + "\u0001" + obj.Name), obj);
        }

        return result;
    }

    /// <summary>Whitespace runs become one space, so indentation, blank lines and line endings do not count.</summary>
    internal static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Abbreviate(List<string> names, int from)
    {
        const int max = 6;
        var shown = string.Join(", ", names.Skip(from).Take(max).Select(DbNames.Quote));
        return (from > 0 ? "…, " : string.Empty) + shown + (names.Count > from + max ? ", …" : string.Empty);
    }

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

    private sealed class KeyComparer : IEqualityComparer<(DbObjectType Type, string Name)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((DbObjectType Type, string Name) x, (DbObjectType Type, string Name) y) =>
            x.Type == y.Type && DbNames.Comparer.Equals(x.Name, y.Name);

        public int GetHashCode((DbObjectType Type, string Name) obj) => HashCode.Combine(obj.Type, DbNames.Comparer.GetHashCode(obj.Name));
    }
}
