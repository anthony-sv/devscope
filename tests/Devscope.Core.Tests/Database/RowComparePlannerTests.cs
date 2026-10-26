using Devscope.Core.Database;
using static Devscope.Core.Tests.Database.DbTestData;

namespace Devscope.Core.Tests.Database;

public sealed class RowComparePlannerTests
{
    private static readonly RowCompareSettings Defaults = new();

    [Fact]
    public void Plan_PrimaryKey_MergesOnItAndSkipsRowVersionAndOneSidedColumns()
    {
        var rowVersion = new ColumnDefinition("Version", "timestamp", false) { Kind = DbValueKind.RowVersion };
        var left = Table("Orders", new TableDefinition { Columns = [Int("Id"), Text("Note"), rowVersion, Text("Legacy")], PrimaryKey = Pk("PK_Orders", "Id") });
        var right = Table("Orders", new TableDefinition { Columns = [Int("Id"), Text("Note"), rowVersion, Text("Email")], PrimaryKey = Pk("PK_Orders", "Id") });

        var plan = RowComparePlanner.Plan(left, right, Defaults);

        Assert.Equal(RowCompareMode.Merge, plan.Mode);
        Assert.Equal("primary key [PK_Orders]", plan.KeyDescription);
        Assert.Equal(["Id", "Note"], plan.Columns.Select(c => c.Name));
        Assert.Equal([0], plan.LeftQuery.KeyColumns);
        Assert.Equal(
            ["Not compared, only in left: [Legacy]", "Not compared, only in right: [Email]", "Not compared, rowversion (differs between databases by design): [Version]"],
            plan.Notes);
    }

    [Fact]
    public void Plan_NoPrimaryKey_UsesSmallestUniqueIndex()
    {
        var table = Table("Codes", new TableDefinition
        {
            Columns = [Int("A"), Int("B"), Int("C")],
            Indexes =
            [
                new IndexDefinition("UX_AB", "NONCLUSTERED", true, [new IndexColumn("A"), new IndexColumn("B")]),
                new IndexDefinition("UX_C", "NONCLUSTERED", true, [new IndexColumn("C")]),
                new IndexDefinition("UX_Filtered", "NONCLUSTERED", true, [new IndexColumn("B")]) { Filter = "([B]>(0))" },
            ],
        });

        var plan = RowComparePlanner.Plan(table, table, Defaults);

        Assert.Equal("unique index [UX_C]", plan.KeyDescription);
        Assert.Equal(["C"], plan.KeyColumnNames);
    }

    [Fact]
    public void Plan_NoKeyAtAll_ComparesWholeRows()
    {
        var heap = Table("Log", new TableDefinition { Columns = [Text("Message")] });

        var plan = RowComparePlanner.Plan(heap, heap, Defaults);

        Assert.Equal(RowCompareMode.WholeRow, plan.Mode);
        Assert.Empty(plan.LeftQuery.KeyColumns);
        Assert.Contains(plan.Notes, n => n.StartsWith("No primary key or unique index", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ChosenKeyAndIgnoredColumns_AreApplied()
    {
        var table = Table("Orders", new TableDefinition { Columns = [Int("Id"), Text("Code"), Text("UpdatedAt")], PrimaryKey = Pk("PK", "Id") });

        var plan = RowComparePlanner.Plan(table, table, Defaults with { KeyColumns = ["code"], IgnoredColumns = ["UpdatedAt"], Filter = " Id > 10 " });

        Assert.Equal("chosen columns", plan.KeyDescription);
        Assert.Equal(["Code"], plan.KeyColumnNames);
        Assert.Equal(["Id", "Code"], plan.Columns.Select(c => c.Name));
        Assert.Equal("Id > 10", plan.LeftQuery.Filter);
        Assert.Contains("Ignored: [UpdatedAt]", plan.Notes);
    }

    [Fact]
    public void Plan_KeyTypesDifferBetweenSides_HoldsLeftRowsInMemory()
    {
        var left = Table("T", new TableDefinition { Columns = [Int("Code")], PrimaryKey = Pk("PK", "Code") });
        var right = Table("T", new TableDefinition { Columns = [Text("Code", nullable: false)], PrimaryKey = Pk("PK", "Code") });

        var plan = RowComparePlanner.Plan(left, right, Defaults);

        Assert.Equal(RowCompareMode.KeyedInMemory, plan.Mode);
        Assert.Empty(plan.RightQuery.KeyColumns);
    }

    [Theory]
    [InlineData(new[] { "Nope" }, new string[0], null, "Key column [Nope] is not a column both tables have")]
    [InlineData(new[] { "Id" }, new[] { "Id" }, null, "Key column [Id] is also ignored.")]
    [InlineData(new string[0], new[] { "Nope" }, null, "There is no column [Nope] to ignore.")]
    [InlineData(new string[0], new string[0], "Id = 1; DROP TABLE x", "The filter must be a single condition")]
    public void Plan_InvalidSettings_ThrowsWithMessage(string[] keys, string[] ignored, string? filter, string message)
    {
        var table = Table("T", new TableDefinition { Columns = [Int("Id")] });

        var ex = Assert.Throws<ArgumentException>(() =>
            RowComparePlanner.Plan(table, table, Defaults with { KeyColumns = keys, IgnoredColumns = ignored, Filter = filter }));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_NotATableOnBothSides_Throws()
    {
        var view = new DbObject(DbObjectType.View, "dbo", "V");

        Assert.Throws<ArgumentException>(() => RowComparePlanner.Plan(view, view, Defaults));
    }
}
