using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

namespace Devscope.Core.Database;

/// <summary>
/// Compares the rows of two versions of a table. Rows are streamed in key order and merged in constant memory; when
/// that turns out not to work (the servers order the keys differently, or the key is not unique) it starts over in
/// a mode that does, and says why in <see cref="RowComparison.Notes"/>.
/// </summary>
public static class RowComparer
{
    private const int ProgressInterval = 5000;

    /// <summary>Compares the rows <paramref name="plan"/> describes.</summary>
    /// <exception cref="InvalidOperationException">The rows have to be held in memory and there are too many.</exception>
    public static async Task<RowComparison> CompareAsync(
        IRowSource left, IRowSource right, RowComparePlan plan, IProgress<RowCompareProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(plan);

        var stopwatch = Stopwatch.StartNew();
        var notes = new List<string>(plan.Notes);
        var mode = plan.Mode;
        while (true)
        {
            var run = new Run(plan, mode, progress);
            try
            {
                switch (mode)
                {
                    case RowCompareMode.Merge:
                        await MergeAsync(left, right, run, cancellationToken).ConfigureAwait(false);
                        break;
                    case RowCompareMode.KeyedInMemory:
                        await KeyedInMemoryAsync(left, right, run, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        await WholeRowAsync(left, right, run, cancellationToken).ConfigureAwait(false);
                        break;
                }

                return run.ToResult(notes, stopwatch.Elapsed);
            }
            catch (RestartException restart)
            {
                notes.Add(restart.Message);
                mode = restart.Mode;
            }
        }
    }

    private static async Task MergeAsync(IRowSource left, IRowSource right, Run run, CancellationToken ct)
    {
        var keys = run.Plan.KeyColumns;
        await using var l = left.ReadRowsAsync(run.Plan.LeftQuery, ct).GetAsyncEnumerator(ct);
        await using var r = right.ReadRowsAsync(run.Plan.RightQuery, ct).GetAsyncEnumerator(ct);
        var hasLeft = await NextAsync(l, isLeft: true).ConfigureAwait(false);
        var hasRight = await NextAsync(r, isLeft: false).ConfigureAwait(false);
        while (hasLeft || hasRight)
        {
            var order = !hasLeft ? 1 : !hasRight ? -1 : CompareKey(l.Current, r.Current, keys);
            if (order < 0)
            {
                run.OnlyInLeft(l.Current);
                hasLeft = await NextAsync(l, isLeft: true).ConfigureAwait(false);
            }
            else if (order > 0)
            {
                run.OnlyInRight(r.Current);
                hasRight = await NextAsync(r, isLeft: false).ConfigureAwait(false);
            }
            else
            {
                run.Match(l.Current, r.Current);
                hasLeft = await NextAsync(l, isLeft: true).ConfigureAwait(false);
                hasRight = await NextAsync(r, isLeft: false).ConfigureAwait(false);
            }
        }

        // Moves to the next row, checking it sorts after the previous one: the merge is only right when both sides
        // arrive in exactly the order CompareKey defines, with each key once.
        async ValueTask<bool> NextAsync(IAsyncEnumerator<object?[]> rows, bool isLeft)
        {
            var previous = run.RowsRead(isLeft) > 0 ? rows.Current : null;
            if (!await rows.MoveNextAsync().ConfigureAwait(false))
            {
                return false;
            }

            run.CountRow(isLeft);
            if (previous is not null)
            {
                var order = CompareKey(previous, rows.Current, keys);
                if (order == 0)
                {
                    throw DuplicateKey(run.Plan, rows.Current, isLeft);
                }

                if (order > 0)
                {
                    throw new RestartException(
                        RowCompareMode.KeyedInMemory,
                        $"The {Side(isLeft)} server sorted {FormatKey(run.Plan, rows.Current)} before {FormatKey(run.Plan, previous)}, "
                        + "so its key order differs from the other side's; compared with the left rows held in memory instead.");
                }
            }

            return true;
        }
    }

    private static async Task KeyedInMemoryAsync(IRowSource left, IRowSource right, Run run, CancellationToken ct)
    {
        var plan = run.Plan;
        var unordered = (Left: plan.LeftQuery with { KeyColumns = [] }, Right: plan.RightQuery with { KeyColumns = [] });
        var leftByKey = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        await foreach (var row in left.ReadRowsAsync(unordered.Left, ct).ConfigureAwait(false))
        {
            run.CountRow(isLeft: true);
            if (!leftByKey.TryAdd(KeyText(row, plan.KeyColumns), row))
            {
                throw DuplicateKey(plan, row, isLeft: true);
            }

            CheckMemory(plan, leftByKey.Count);
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var row in right.ReadRowsAsync(unordered.Right, ct).ConfigureAwait(false))
        {
            run.CountRow(isLeft: false);
            var key = KeyText(row, plan.KeyColumns);
            if (leftByKey.Remove(key, out var leftRow))
            {
                matched.Add(key);
                run.Match(leftRow, row);
            }
            else if (matched.Contains(key))
            {
                throw DuplicateKey(plan, row, isLeft: false);
            }
            else
            {
                run.OnlyInRight(row);
            }
        }

        foreach (var row in leftByKey.Values)
        {
            run.OnlyInLeft(row);
        }
    }

    private static async Task WholeRowAsync(IRowSource left, IRowSource right, Run run, CancellationToken ct)
    {
        var plan = run.Plan;
        var unordered = (Left: plan.LeftQuery with { KeyColumns = [] }, Right: plan.RightQuery with { KeyColumns = [] });

        // How many more times each distinct row occurs on the left than on the right, and one example of it.
        var rows = new Dictionary<UInt128, RowCount>();
        await foreach (var row in left.ReadRowsAsync(unordered.Left, ct).ConfigureAwait(false))
        {
            run.CountRow(isLeft: true);
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(rows, RowHash(row), out var exists);
            entry = exists ? entry with { Surplus = entry.Surplus + 1 } : new RowCount(1, row);
            CheckMemory(plan, rows.Count);
        }

        await foreach (var row in right.ReadRowsAsync(unordered.Right, ct).ConfigureAwait(false))
        {
            run.CountRow(isLeft: false);
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(rows, RowHash(row), out var exists);
            if (!exists)
            {
                entry = new RowCount(-1, row);
                CheckMemory(plan, rows.Count);
            }
            else
            {
                if (entry.Surplus > 0)
                {
                    run.Identical();
                }

                entry = entry with { Surplus = entry.Surplus - 1 };
            }
        }

        foreach (var (surplus, row) in rows.Values)
        {
            for (var i = 0; i < Math.Abs(surplus); i++)
            {
                if (surplus > 0)
                {
                    run.OnlyInLeft(row);
                }
                else
                {
                    run.OnlyInRight(row);
                }
            }
        }
    }

    private static int CompareKey(object?[] a, object?[] b, IReadOnlyList<int> keys)
    {
        foreach (var i in keys)
        {
            var order = DbValues.CompareKeys(a[i], b[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }

    private static string KeyText(object?[] row, IReadOnlyList<int> keys) =>
        string.Join('\u001F', keys.Select(i => DbValues.CanonicalKey(row[i])));

    private static UInt128 RowHash(object?[] row)
    {
        var sb = new StringBuilder();
        foreach (var value in row)
        {
            DbValues.AppendCanonical(sb, value);
        }

        return XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void CheckMemory(RowComparePlan plan, int count)
    {
        if (count > plan.Settings.MaxInMemoryRows)
        {
            throw new InvalidOperationException(
                $"{DbNames.Quote(plan.Schema, plan.Table)} has more than {plan.Settings.MaxInMemoryRows:N0} rows to hold in memory "
                + "(there is no key both servers order the same way). Choose key columns, add a filter, or raise the limit in Settings.");
        }
    }

    private static RestartException DuplicateKey(RowComparePlan plan, object?[] row, bool isLeft) => new(
        RowCompareMode.WholeRow,
        $"The key ({string.Join(", ", plan.KeyColumnNames.Select(DbNames.Quote))}) is not unique on the {Side(isLeft)} side "
        + $"(e.g. {FormatKey(plan, row)}), so whole rows were compared instead.");

    private static string FormatKey(RowComparePlan plan, object?[] row) =>
        string.Join(", ", plan.KeyColumns.Select(i => $"{plan.Columns[i].Name}={DbValues.Format(row[i], 40)}"));

    private static string Side(bool isLeft) => isLeft ? "left" : "right";

    /// <param name="Surplus">Occurrences on the left minus occurrences on the right.</param>
    /// <param name="Row">One occurrence.</param>
    private readonly record struct RowCount(int Surplus, object?[] Row);

    private sealed class RestartException(RowCompareMode mode, string message) : Exception(message)
    {
        public RowCompareMode Mode { get; } = mode;
    }

    /// <summary>Counts and examples of one pass.</summary>
    private sealed class Run(RowComparePlan plan, RowCompareMode mode, IProgress<RowCompareProgress>? progress)
    {
        private readonly long[] _differentByColumn = new long[plan.Columns.Count];
        private readonly List<RowDifference> _onlyInLeft = [];
        private readonly List<RowDifference> _onlyInRight = [];
        private readonly List<RowDifference> _different = [];
        private long _leftRows;
        private long _rightRows;
        private long _onlyInLeftCount;
        private long _onlyInRightCount;
        private long _differentCount;
        private long _identicalCount;

        public RowComparePlan Plan { get; } = plan;

        public long RowsRead(bool isLeft) => isLeft ? _leftRows : _rightRows;

        public void CountRow(bool isLeft)
        {
            var count = isLeft ? ++_leftRows : ++_rightRows;
            if (count % ProgressInterval == 0)
            {
                progress?.Report(new RowCompareProgress(_leftRows, _rightRows));
            }
        }

        public void OnlyInLeft(object?[] row)
        {
            _onlyInLeftCount++;
            Keep(_onlyInLeft, new RowDifference(RowDiffKind.OnlyInLeft, row, null));
        }

        public void OnlyInRight(object?[] row)
        {
            _onlyInRightCount++;
            Keep(_onlyInRight, new RowDifference(RowDiffKind.OnlyInRight, null, row));
        }

        public void Identical() => _identicalCount++;

        public void Match(object?[] left, object?[] right)
        {
            List<int>? changed = null;
            for (var i = 0; i < left.Length; i++)
            {
                if (!DbValues.AreEqual(left[i], right[i]))
                {
                    (changed ??= []).Add(i);
                    _differentByColumn[i]++;
                }
            }

            if (changed is null)
            {
                _identicalCount++;
                return;
            }

            _differentCount++;
            Keep(_different, new RowDifference(RowDiffKind.Different, left, right) { ChangedColumns = changed });
        }

        public RowComparison ToResult(IReadOnlyList<string> notes, TimeSpan elapsed)
        {
            progress?.Report(new RowCompareProgress(_leftRows, _rightRows));
            return new RowComparison(Plan, mode)
            {
                LeftRows = _leftRows,
                RightRows = _rightRows,
                OnlyInLeft = _onlyInLeftCount,
                OnlyInRight = _onlyInRightCount,
                Different = _differentCount,
                Identical = _identicalCount,
                DifferentByColumn = _differentByColumn,
                Samples = [.. _different, .. _onlyInLeft, .. _onlyInRight],
                Notes = [.. notes],
                Elapsed = elapsed,
            };
        }

        private void Keep(List<RowDifference> samples, RowDifference difference)
        {
            if (samples.Count < Plan.Settings.MaxSamples)
            {
                samples.Add(difference);
            }
        }
    }
}
