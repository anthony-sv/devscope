using System.Runtime.CompilerServices;
using Devscope.Core.Database;
using static Devscope.Core.Tests.Database.DbTestData;

namespace Devscope.Core.Tests.Database;

public sealed class RowComparerTests
{
    private static readonly DbObject Customers = Table("Customers", new TableDefinition
    {
        Columns = [Int("Id"), Text("Name"), Text("City")],
        PrimaryKey = Pk("PK_Customers", "Id"),
    });

    /// <summary>Serves rows like a server would: sorted by the query's key columns, unless told to ignore the order.</summary>
    private sealed class FakeSource(IEnumerable<object?[]> rows, bool sorts = true) : IRowSource
    {
        public List<RowQuery> Queries { get; } = [];

        public async IAsyncEnumerable<object?[]> ReadRowsAsync(RowQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Queries.Add(query);
            IEnumerable<object?[]> result = rows;
            if (sorts && query.KeyColumns.Count > 0)
            {
                result = rows.Order(Comparer<object?[]>.Create((a, b) =>
                    query.KeyColumns.Select(i => DbValues.CompareKeys(a[i], b[i])).FirstOrDefault(c => c != 0)));
            }

            foreach (var row in result)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return [.. row];
            }
        }
    }

    private static Task<RowComparison> CompareAsync(FakeSource left, FakeSource right, DbObject? table = null, RowCompareSettings? settings = null)
    {
        var plan = RowComparePlanner.Plan(table ?? Customers, table ?? Customers, settings ?? new RowCompareSettings());
        return RowComparer.CompareAsync(left, right, plan, progress: null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompareAsync_ByPrimaryKey_FindsMissingExtraAndChangedRows()
    {
        var left = new FakeSource([[3, "Cleo", "Oslo"], [1, "Ada", "London"], [2, "Bob", "Paris"], [4, "Dan", null]]);
        var right = new FakeSource([[1, "Ada", "London"], [2, "Bob", "Lyon"], [4, "Dan", null], [5, "Eve", "Rome"]]);

        var result = await CompareAsync(left, right);

        Assert.Equal(RowCompareMode.Merge, result.Mode);
        Assert.Equal((4L, 4L), (result.LeftRows, result.RightRows));
        Assert.Equal((1L, 1L, 1L, 2L), (result.OnlyInLeft, result.OnlyInRight, result.Different, result.Identical));
        Assert.Equal([0L, 0L, 1L], result.DifferentByColumn);
        Assert.True(result.SamplesAreComplete);

        var changed = Assert.Single(result.Samples, s => s.Kind == RowDiffKind.Different);
        Assert.Equal([2], changed.ChangedColumns);
        Assert.Equal("Paris", changed.Left![2]);
        Assert.Equal("Lyon", changed.Right![2]);
        Assert.Equal(3, Assert.Single(result.Samples, s => s.Kind == RowDiffKind.OnlyInLeft).Left![0]);
        Assert.Equal(5, Assert.Single(result.Samples, s => s.Kind == RowDiffKind.OnlyInRight).Right![0]);
        Assert.Equal([0], left.Queries.Single().KeyColumns);
    }

    [Fact]
    public async Task CompareAsync_NumbersOfDifferentWidths_AreEqual()
    {
        var left = new FakeSource([[1, "Ada", "London"]]);
        var right = new FakeSource([[1L, "Ada", "London"]]);

        var result = await CompareAsync(left, right);

        Assert.Equal(1L, result.Identical);
        Assert.False(result.HasDifferences);
    }

    [Fact]
    public async Task CompareAsync_ServerSortsDifferently_StartsOverWithLeftRowsInMemory()
    {
        var rows = new object?[][] { [1, "Ada", "London"], [2, "Bob", "Paris"], [3, "Cleo", "Oslo"] };
        var left = new FakeSource(rows);
        var right = new FakeSource(rows.Reverse().Select(r => r[0] is 2 ? new object?[] { 2, "Bob", "Lyon" } : r), sorts: false);

        var result = await CompareAsync(left, right);

        Assert.Equal(RowCompareMode.KeyedInMemory, result.Mode);
        Assert.Contains(result.Notes, n => n.StartsWith("The right server sorted Id=2 before Id=3", StringComparison.Ordinal));
        Assert.Equal((0L, 0L, 1L, 2L), (result.OnlyInLeft, result.OnlyInRight, result.Different, result.Identical));
        Assert.Equal(3L, result.RightRows); // counts restart with the new pass
        Assert.Empty(right.Queries[^1].KeyColumns); // the second pass does not ask the server to sort
    }

    [Fact]
    public async Task CompareAsync_KeyNotUnique_ComparesWholeRows()
    {
        var left = new FakeSource([[1, "Ada", "London"], [1, "Ada", "London"], [2, "Bob", "Paris"]]);
        var right = new FakeSource([[1, "Ada", "London"], [2, "Bob", "Lyon"]]);

        var result = await CompareAsync(left, right);

        Assert.Equal(RowCompareMode.WholeRow, result.Mode);
        Assert.Contains(result.Notes, n => n.Contains("is not unique on the left side (e.g. Id=1)", StringComparison.Ordinal));
        Assert.Equal((2L, 1L, 0L, 1L), (result.OnlyInLeft, result.OnlyInRight, result.Different, result.Identical));
    }

    [Fact]
    public async Task CompareAsync_NoKey_MatchesRowsAsAMultiset()
    {
        var heap = Table("Log", new TableDefinition { Columns = [Text("Message"), Int("Level")] });
        var left = new FakeSource([["a", 1], ["a", 1], ["b", 2]]);
        var right = new FakeSource([["a", 1], ["c", 3], ["c", 3]]);

        var result = await CompareAsync(left, right, heap);

        Assert.Equal(RowCompareMode.WholeRow, result.Mode);
        Assert.Equal((2L, 2L, 1L), (result.OnlyInLeft, result.OnlyInRight, result.Identical));
        Assert.Equal(["a", "b"], result.Samples.Where(s => s.Kind == RowDiffKind.OnlyInLeft).Select(s => (string)s.Left![0]!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_TooManyRowsForMemory_ExplainsWhatToDo()
    {
        var heap = Table("Log", new TableDefinition { Columns = [Int("N")] });
        var left = new FakeSource(Enumerable.Range(0, 10).Select(i => new object?[] { i }));
        var right = new FakeSource([]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CompareAsync(left, right, heap, new RowCompareSettings { MaxInMemoryRows = 5 }));

        Assert.Contains("Choose key columns, add a filter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareAsync_ManyDifferences_KeepsAtMostMaxSamplesPerKind()
    {
        var left = new FakeSource(Enumerable.Range(0, 50).Select(i => new object?[] { i, "x", "y" }));
        var right = new FakeSource(Enumerable.Range(25, 50).Select(i => new object?[] { i, "x", "z" }));

        var result = await CompareAsync(left, right, settings: new RowCompareSettings { MaxSamples = 10 });

        Assert.Equal((25L, 25L, 25L), (result.OnlyInLeft, result.OnlyInRight, result.Different));
        Assert.Equal(30, result.Samples.Count);
        Assert.False(result.SamplesAreComplete);
    }

    [Fact]
    public async Task CompareAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var plan = RowComparePlanner.Plan(Customers, Customers, new RowCompareSettings());
        var source = new FakeSource([[1, "Ada", "London"]]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RowComparer.CompareAsync(source, source, plan, null, cts.Token));
    }
}
