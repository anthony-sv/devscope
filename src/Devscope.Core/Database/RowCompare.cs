namespace Devscope.Core.Database;

/// <summary>The user's choices for comparing one table's rows.</summary>
public sealed record RowCompareSettings
{
    /// <summary>Columns that identify a row. Empty means detect: the primary key, else a unique constraint or index.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Columns left out of the comparison, e.g. audit timestamps. rowversion columns are always left out.</summary>
    public IReadOnlyList<string> IgnoredColumns { get; init; } = [];

    /// <summary>WHERE condition applied on both sides, as typed, e.g. <c>CreatedAt &gt;= '2026-01-01'</c>.</summary>
    public string? Filter { get; init; }

    /// <summary>Rows kept as examples per kind of difference.</summary>
    public int MaxSamples { get; init; } = 1000;

    /// <summary>Rows that may be held in memory when the rows cannot be streamed in key order.</summary>
    public int MaxInMemoryRows { get; init; } = 2_000_000;
}

/// <summary>How rows are matched.</summary>
public enum RowCompareMode
{
    /// <summary>Both sides are streamed in key order and merged, in constant memory.</summary>
    Merge,

    /// <summary>The left rows are held in memory by key; used when the two sides cannot be ordered the same way.</summary>
    KeyedInMemory,

    /// <summary>
    /// Whole rows are matched, without a key (none, or not unique). A changed row shows as one row only in left and
    /// one only in right.
    /// </summary>
    WholeRow,
}

/// <summary>What a data comparison of one table reads and how it matches rows; see <see cref="RowComparePlanner"/>.</summary>
/// <param name="Schema">Schema of the left table.</param>
/// <param name="Table">Name of the left table.</param>
/// <param name="LeftQuery">Rows read on the left; its columns are the compared columns, in order.</param>
/// <param name="RightQuery">The same columns, as the right table defines them.</param>
/// <param name="KeyColumns">Indexes into the compared columns that identify a row; empty without a key.</param>
/// <param name="Mode">How rows are matched.</param>
/// <param name="KeyDescription">Where the key comes from, e.g. "primary key [PK_Orders]".</param>
/// <param name="Notes">What the comparison leaves out, and why it reads the way it does.</param>
/// <param name="Settings">The settings the plan was made from.</param>
public sealed record RowComparePlan(
    string Schema,
    string Table,
    RowQuery LeftQuery,
    RowQuery RightQuery,
    IReadOnlyList<int> KeyColumns,
    RowCompareMode Mode,
    string KeyDescription,
    IReadOnlyList<string> Notes,
    RowCompareSettings Settings)
{
    /// <summary>Compared columns, as the left table defines them.</summary>
    public IReadOnlyList<ColumnDefinition> Columns => LeftQuery.Columns;

    public IReadOnlyList<string> KeyColumnNames => [.. KeyColumns.Select(i => Columns[i].Name)];
}

/// <summary>Decides which columns two versions of a table are compared on, and which key identifies a row.</summary>
public static class RowComparePlanner
{
    /// <summary>Plans a comparison. Throws <see cref="ArgumentException"/> for settings that cannot work, with a message for the user.</summary>
    public static RowComparePlan Plan(DbObject left, DbObject right, RowCompareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(settings);
        if (left is not { Type: DbObjectType.Table, Table: { } lt } || right is not { Type: DbObjectType.Table, Table: { } rt })
        {
            throw new ArgumentException("Only tables that exist on both sides can be compared row by row.");
        }

        var filter = string.IsNullOrWhiteSpace(settings.Filter) ? null : settings.Filter.Trim();
        if (filter is not null && filter.Contains(';', StringComparison.Ordinal))
        {
            throw new ArgumentException("The filter must be a single condition, without ';'.");
        }

        var ignored = new HashSet<string>(settings.IgnoredColumns.Select(c => c.Trim()).Where(c => c.Length > 0), DbNames.Comparer);
        foreach (var name in ignored.Where(n => lt.FindColumn(n) is null && rt.FindColumn(n) is null))
        {
            throw new ArgumentException($"There is no column {DbNames.Quote(name)} to ignore.");
        }

        if (settings.KeyColumns.FirstOrDefault(k => ignored.Contains(k.Trim())) is { } ignoredKey)
        {
            throw new ArgumentException($"Key column {DbNames.Quote(ignoredKey.Trim())} is also ignored.");
        }

        var notes = new List<string>();
        var onlyLeft = lt.Columns.Where(c => rt.FindColumn(c.Name) is null).Select(c => DbNames.Quote(c.Name)).ToList();
        var onlyRight = rt.Columns.Where(c => lt.FindColumn(c.Name) is null).Select(c => DbNames.Quote(c.Name)).ToList();
        if (onlyLeft.Count > 0)
        {
            notes.Add($"Not compared, only in left: {string.Join(", ", onlyLeft)}");
        }

        if (onlyRight.Count > 0)
        {
            notes.Add($"Not compared, only in right: {string.Join(", ", onlyRight)}");
        }

        var rowVersions = lt.Columns.Where(c => c.Kind == DbValueKind.RowVersion && !ignored.Contains(c.Name)).Select(c => DbNames.Quote(c.Name)).ToList();
        if (rowVersions.Count > 0)
        {
            notes.Add($"Not compared, rowversion (differs between databases by design): {string.Join(", ", rowVersions)}");
        }

        if (ignored.Count > 0)
        {
            notes.Add($"Ignored: {string.Join(", ", ignored.Order(DbNames.Comparer).Select(DbNames.Quote))}");
        }

        var leftColumns = lt.Columns
            .Where(c => rt.FindColumn(c.Name) is { } r && c.Kind != DbValueKind.RowVersion && r.Kind != DbValueKind.RowVersion && !ignored.Contains(c.Name))
            .ToList();
        if (leftColumns.Count == 0)
        {
            throw new ArgumentException("The two tables have no columns in common to compare.");
        }

        var rightColumns = leftColumns.ConvertAll(c => rt.FindColumn(c.Name)!);
        var (keyNames, keyDescription) = ChooseKey(settings, lt, rt, leftColumns);
        var keys = keyNames.Select(n => leftColumns.FindIndex(c => DbNames.Comparer.Equals(c.Name, n))).ToList();

        RowCompareMode mode;
        if (keys.Count == 0)
        {
            mode = RowCompareMode.WholeRow;
            notes.Add("No primary key or unique index: whole rows are matched, so a changed row shows as one row only in left and one only in right. Choose key columns to see changed values.");
        }
        else if (keys.All(i => SqlDataTypes.IsOrderable(leftColumns[i].Kind) && leftColumns[i].Kind == rightColumns[i].Kind))
        {
            mode = RowCompareMode.Merge;
        }
        else
        {
            mode = RowCompareMode.KeyedInMemory;
            notes.Add("The key columns cannot be ordered the same way on both servers (their types differ, or cannot be sorted), so the left rows are held in memory.");
        }

        IReadOnlyList<int> order = mode == RowCompareMode.Merge ? keys : [];
        return new RowComparePlan(
            left.Schema,
            left.Name,
            new RowQuery(left.Schema, left.Name, leftColumns, order, filter),
            new RowQuery(right.Schema, right.Name, rightColumns, order, filter),
            keys,
            mode,
            keyDescription,
            notes,
            settings);
    }

    private static (IReadOnlyList<string> Names, string Description) ChooseKey(
        RowCompareSettings settings, TableDefinition left, TableDefinition right, List<ColumnDefinition> compared)
    {
        var chosen = settings.KeyColumns.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        if (chosen.Count > 0)
        {
            foreach (var name in chosen)
            {
                if (!compared.Exists(c => DbNames.Comparer.Equals(c.Name, name)))
                {
                    throw new ArgumentException($"Key column {DbNames.Quote(name)} is not a column both tables have (rowversion columns cannot be keys).");
                }
            }

            if (chosen.Distinct(DbNames.Comparer).Count() != chosen.Count)
            {
                throw new ArgumentException("A key column is listed twice.");
            }

            return (chosen, "chosen columns");
        }

        // Prefer a key that is unique on both sides; otherwise one side's, which the comparison checks as it reads.
        var leftKeys = UniqueKeys(left).ToList();
        var rightKeys = UniqueKeys(right).ToList();
        bool Usable(IReadOnlyList<string> columns) => columns.All(n => compared.Exists(c => DbNames.Comparer.Equals(c.Name, n)));
        bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.Count == b.Count && a.All(n => b.Contains(n, DbNames.Comparer));

        var best = leftKeys.FirstOrDefault(l => Usable(l.Columns) && rightKeys.Exists(r => SameColumns(l.Columns, r.Columns)))
            ?? leftKeys.FirstOrDefault(l => Usable(l.Columns))
            ?? rightKeys.FirstOrDefault(r => Usable(r.Columns));
        return best is null ? ([], "none") : (best.Columns, best.Description);
    }

    /// <summary>Unique keys, best first: the primary key, then unique constraints and indexes with the fewest columns.</summary>
    private static IEnumerable<KeyCandidate> UniqueKeys(TableDefinition table)
    {
        if (table.PrimaryKey is { } pk)
        {
            yield return new KeyCandidate([.. pk.Columns.Select(c => c.Name)], $"primary key {DbNames.Quote(pk.Name)}");
        }

        var others = table.UniqueConstraints
            .Select(u => new KeyCandidate([.. u.Columns.Select(c => c.Name)], $"unique constraint {DbNames.Quote(u.Name)}"))
            .Concat(table.Indexes
                .Where(i => i is { IsUnique: true, Filter: null, IsDisabled: false } && i.Columns.Count > 0)
                .Select(i => new KeyCandidate([.. i.Columns.Select(c => c.Name)], $"unique index {DbNames.Quote(i.Name)}")))
            .OrderBy(k => k.Columns.Count);
        foreach (var candidate in others)
        {
            yield return candidate;
        }
    }

    private sealed record KeyCandidate(IReadOnlyList<string> Columns, string Description);
}

/// <summary>Kind of a row difference.</summary>
public enum RowDiffKind
{
    OnlyInLeft,
    OnlyInRight,
    Different,
}

/// <summary>One differing row, kept as an example.</summary>
/// <param name="Kind">How the row differs.</param>
/// <param name="Left">The left row (compared columns, in plan order); null when only in right.</param>
/// <param name="Right">The right row; null when only in left.</param>
public sealed record RowDifference(RowDiffKind Kind, IReadOnlyList<object?>? Left, IReadOnlyList<object?>? Right)
{
    /// <summary>For a changed row, the indexes of the columns whose values differ.</summary>
    public IReadOnlyList<int> ChangedColumns { get; init; } = [];
}

/// <summary>Result of comparing one table's rows.</summary>
public sealed record RowComparison(RowComparePlan Plan, RowCompareMode Mode)
{
    public long LeftRows { get; init; }

    public long RightRows { get; init; }

    public long OnlyInLeft { get; init; }

    public long OnlyInRight { get; init; }

    public long Different { get; init; }

    public long Identical { get; init; }

    /// <summary>Changed rows per compared column, indexed like <see cref="RowComparePlan.Columns"/>.</summary>
    public IReadOnlyList<long> DifferentByColumn { get; init; } = [];

    /// <summary>Examples, at most <see cref="RowCompareSettings.MaxSamples"/> per kind, in key order when merged.</summary>
    public IReadOnlyList<RowDifference> Samples { get; init; } = [];

    /// <summary>The plan's notes plus anything learned while reading, e.g. why the mode changed.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public TimeSpan Elapsed { get; init; }

    public long DifferenceCount => OnlyInLeft + OnlyInRight + Different;

    public bool HasDifferences => DifferenceCount > 0;

    /// <summary>Whether <see cref="Samples"/> holds every differing row.</summary>
    public bool SamplesAreComplete => Samples.Count == DifferenceCount;
}

/// <summary>Rows read so far on each side.</summary>
public readonly record struct RowCompareProgress(long LeftRows, long RightRows);
