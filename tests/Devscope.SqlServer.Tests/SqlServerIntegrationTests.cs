using System.Diagnostics;
using System.Data.SqlTypes;
using Devscope.Core.Database;
using Devscope.Core.Ear;

namespace Devscope.SqlServer.Tests;

/// <summary>Reads and compares two real databases; see <see cref="SqlServerFixture"/> for how to run them.</summary>
public sealed class SqlServerIntegrationTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<DbComparison> CompareSchemasAsync(DbCompareOptions? options = null)
    {
        var left = await fixture.OpenLeft().ReadSchemaAsync(Ct);
        var right = await fixture.OpenRight().ReadSchemaAsync(Ct);
        return SchemaComparer.Compare(left, right, options ?? new DbCompareOptions());
    }

    private async Task<RowComparison> CompareRowsAsync(string table, RowCompareSettings? settings = null)
    {
        var comparison = await CompareSchemasAsync();
        var entry = comparison.Entries.Single(e => e.Type == DbObjectType.Table && e.Name == table);
        var plan = RowComparePlanner.Plan(entry.Left!, entry.Right!, settings ?? new RowCompareSettings());
        return await RowComparer.CompareAsync(fixture.OpenLeft(), fixture.OpenRight(), plan, progress: null, Ct);
    }

    [Fact]
    public async Task TestAsync_DescribesTheDatabase()
    {
        fixture.SkipWithoutServer();

        var info = await fixture.OpenLeft().TestAsync(Ct);

        Assert.Equal(fixture.LeftDatabase, info.Database);
        Assert.NotEmpty(info.ProductVersion);
        Assert.NotEmpty(info.Collation);
        Assert.True(info.CompatibilityLevel >= 100);
    }

    [Fact]
    public async Task ReadSchema_ReadsColumnsKeysAndConstraints()
    {
        fixture.SkipWithoutServer();

        var schema = await fixture.OpenLeft().ReadSchemaAsync(Ct);
        var orders = schema.Objects.Single(o => o.Type == DbObjectType.Table && o.Name == "Orders").Table!;

        Assert.Equal(["Id", "CustomerId", "Status", "Total", "Phone", "Doubled", "Version"], orders.Columns.Select(c => c.Name));
        Assert.Equal("varchar(20)", orders.FindColumn("Status")!.DataType);
        Assert.Equal(new DefaultConstraintDefinition("DF_Orders_Status", "('new')", false), orders.FindColumn("Status")!.Default);
        Assert.Equal(("[dbo].[Phone]", DbValueKind.Text), (orders.FindColumn("Phone")!.DataType, orders.FindColumn("Phone")!.Kind));
        Assert.Equal("([Total]*(2))", orders.FindColumn("Doubled")!.ComputedExpression);
        Assert.Equal(DbValueKind.RowVersion, orders.FindColumn("Version")!.Kind);
        Assert.Equal("PK_Orders", orders.PrimaryKey!.Name);
        Assert.True(Assert.Single(orders.CheckConstraints).IsSystemNamed);
        Assert.Equal(["Total"], Assert.Single(orders.Indexes).IncludedColumns);
        Assert.Equal(("dbo", "Customers", "Id"), (orders.ForeignKeys[0].ReferencedSchema, orders.ForeignKeys[0].ReferencedTable, orders.ForeignKeys[0].ReferencedColumns[0]));
        Assert.Equal(200000L, schema.Objects.Single(o => o.Name == "Big").RowCount);
        Assert.Equal(DbValueKind.Clr, schema.Objects.Single(o => o.Name == "Data").Table!.FindColumn("Shape")!.Kind);
    }

    [Fact]
    public async Task CompareSchemas_FindsEveryKindOfDifference()
    {
        fixture.SkipWithoutServer();

        var comparison = await CompareSchemasAsync();
        DbDiffEntry Entry(DbObjectType type, string name) => comparison.Entries.Single(e => e.Type == type && e.Name == name);

        Assert.Equal(DiffStatus.Removed, Entry(DbObjectType.Table, "Legacy").Status);
        Assert.Equal(DiffStatus.Added, Entry(DbObjectType.Table, "Audit").Status);

        // System-generated names (PK__Customers__..., DF__Customers__Creat__...) differ but are ignored.
        Assert.Equal(DiffStatus.Unchanged, Entry(DbObjectType.Table, "Customers").Status);
        Assert.Equal(DiffStatus.Unchanged, Entry(DbObjectType.Type, "OrderLines").Status);
        Assert.Equal(DiffStatus.Unchanged, Entry(DbObjectType.Synonym, "Clients").Status);

        var orders = Entry(DbObjectType.Table, "Orders");
        Assert.Equal(DiffStatus.Modified, orders.Status);
        Assert.Equal(
            [
                "7 columns → 8 columns",
                "Column [Email] only in right: nvarchar(200) COLLATE SQL_Latin1_General_CP1_CI_AS NULL",
                "Column [Status]: varchar(20) → varchar(30)",
                "Index [IX_Orders_Customer]: NONCLUSTERED ([CustomerId]) INCLUDE ([Total]) → NONCLUSTERED ([CustomerId], [Status]) INCLUDE ([Total])",
            ],
            orders.Differences);

        Assert.Equal(DiffStatus.Unchanged, Entry(DbObjectType.View, "vOrders").Status);
        Assert.Equal("Only whitespace differs (ignored)", Assert.Single(Entry(DbObjectType.View, "vOrders").Differences));
        Assert.Equal("Definition differs: +1 −1 lines", Assert.Single(Entry(DbObjectType.Procedure, "GetOrders").Differences));
        Assert.Equal("Enabled → disabled", Assert.Single(Entry(DbObjectType.Trigger, "trg_Orders").Differences));
        Assert.Equal("INCREMENT BY 1 → INCREMENT BY 10", Assert.Single(Entry(DbObjectType.Sequence, "OrderNumbers").Differences));
        Assert.Equal(
            "CREATE TYPE [dbo].[Phone] FROM nvarchar(20) NOT NULL; → CREATE TYPE [dbo].[Phone] FROM nvarchar(30) NOT NULL;",
            Assert.Single(Entry(DbObjectType.Type, "Phone").Differences));
    }

    [Fact]
    public async Task CompareSchemas_NotIgnoringGeneratedNames_ReportsThem()
    {
        fixture.SkipWithoutServer();

        var comparison = await CompareSchemasAsync(new DbCompareOptions { IgnoreSystemNames = false });

        var customers = comparison.Entries.Single(e => e.Name == "Customers");
        Assert.Equal(DiffStatus.Modified, customers.Status);
        Assert.Contains(customers.Differences, d => d.StartsWith("Primary key: CONSTRAINT [PK__Customer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareRows_FindsMissingExtraAndChangedRowsOfEveryType()
    {
        fixture.SkipWithoutServer();

        var result = await CompareRowsAsync("Data");

        Assert.Equal(RowCompareMode.Merge, result.Mode);
        Assert.Equal((1L, 1L, 1L, 2L), (result.OnlyInLeft, result.OnlyInRight, result.Different, result.Identical));
        var changed = Assert.Single(result.Samples, s => s.Kind == RowDiffKind.Different);
        Assert.Equal(["Name"], changed.ChangedColumns.Select(i => result.Plan.Columns[i].Name));

        // Values .NET cannot hold natively come through intact: a 38-digit decimal and a geography as bytes.
        Assert.Equal(SqlDecimal.Parse("12345678901234567890123456789012345678"), Assert.IsType<SqlDecimal>(await FirstValueAsync("Amount")));
        Assert.IsType<byte[]>(await FirstValueAsync("Shape"));
    }

    private async Task<object?> FirstValueAsync(string column)
    {
        var schema = await fixture.OpenLeft().ReadSchemaAsync(Ct);
        var data = schema.Objects.Single(o => o.Name == "Data").Table!;
        var query = new RowQuery("dbo", "Data", [data.Columns[0], data.FindColumn(column)!], [0], "Id = 1");
        await foreach (var row in fixture.OpenLeft().ReadRowsAsync(query, Ct))
        {
            return row[1];
        }

        return null;
    }

    [Theory]
    [InlineData("TextKeys", "zz", "Z")]
    [InlineData("VarcharKeys", "€", "é")]
    public async Task CompareRows_TextKeysInAnyCollation_MergeWithoutFallingBack(string table, string onlyLeft, string onlyRight)
    {
        fixture.SkipWithoutServer();

        var result = await CompareRowsAsync(table);

        Assert.Equal(RowCompareMode.Merge, result.Mode);
        Assert.DoesNotContain(result.Notes, n => n.Contains("sorted", StringComparison.Ordinal));
        Assert.Equal(onlyLeft, Assert.Single(result.Samples, s => s.Kind == RowDiffKind.OnlyInLeft).Left![0]);
        Assert.Equal(onlyRight, Assert.Single(result.Samples, s => s.Kind == RowDiffKind.OnlyInRight).Right![0]);
        Assert.Equal(0L, result.Different);
    }

    [Theory]
    [InlineData("GuidKeys", 0L, 1L)]
    [InlineData("BinKeys", 1L, 0L)]
    public async Task CompareRows_GuidAndBinaryKeys_MergeWithoutFallingBack(string table, long onlyInLeft, long onlyInRight)
    {
        fixture.SkipWithoutServer();

        var result = await CompareRowsAsync(table);

        Assert.Equal(RowCompareMode.Merge, result.Mode);
        Assert.Equal((onlyInLeft, onlyInRight, 0L), (result.OnlyInLeft, result.OnlyInRight, result.Different));
    }

    [Fact]
    public async Task CompareRows_TableWithoutKey_MatchesWholeRows()
    {
        fixture.SkipWithoutServer();

        var result = await CompareRowsAsync("Heap");

        Assert.Equal(RowCompareMode.WholeRow, result.Mode);
        Assert.Equal((2L, 1L, 1L), (result.OnlyInLeft, result.OnlyInRight, result.Identical));
    }

    [Fact]
    public async Task CompareRows_WithFilter_OnlyReadsMatchingRows()
    {
        fixture.SkipWithoutServer();

        var result = await CompareRowsAsync("Data", new RowCompareSettings { Filter = "Id <= 2" });

        Assert.Equal((2L, 2L, 2L), (result.LeftRows, result.RightRows, result.Identical));
    }

    [Fact]
    public async Task ReadRows_StoppedEarly_DoesNotReadTheRestOfTheTable()
    {
        fixture.SkipWithoutServer();
        var schema = await fixture.OpenLeft().ReadSchemaAsync(Ct);
        var big = schema.Objects.Single(o => o.Name == "Big").Table!;
        var query = new RowQuery("dbo", "Big", big.Columns, [0], null);

        var stopwatch = Stopwatch.StartNew();
        var read = 0;
        await foreach (var _ in fixture.OpenLeft().ReadRowsAsync(query, Ct))
        {
            if (++read == 10)
            {
                break;
            }
        }

        Assert.Equal(10, read);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Stopping took {stopwatch.Elapsed}");
    }
}
