using Devscope.Core.Database;
using Devscope.Core.Ear;
using static Devscope.Core.Tests.Database.DbTestData;

namespace Devscope.Core.Tests.Database;

public sealed class SchemaComparerTests
{
    private static readonly DbCompareOptions Defaults = new();

    [Fact]
    public void Compare_ObjectsOnEitherSide_ReportsOnlyInLeftAndOnlyInRight()
    {
        var left = Schema(Orders(), Table("Legacy", new TableDefinition { Columns = [Int("Id")] }));
        var right = Schema(Orders(), new DbObject(DbObjectType.View, "dbo", "vOrders") { Definition = "CREATE VIEW vOrders AS SELECT 1 AS x" });

        var result = SchemaComparer.Compare(left, right, Defaults);
        var byName = result.Entries.ToDictionary(e => e.FullName);

        Assert.Equal(DiffStatus.Unchanged, byName["dbo.Orders"].Status);
        Assert.Equal(DiffStatus.Removed, byName["dbo.Legacy"].Status);
        Assert.Equal(DiffStatus.Added, byName["dbo.vOrders"].Status);
        Assert.Equal((1, 1, 0, 1), (result.OnlyInLeft, result.OnlyInRight, result.Different, result.Identical));
    }

    [Fact]
    public void Compare_NamesDifferOnlyInCase_AreTheSameObject()
    {
        var left = Schema(Orders());
        var right = Schema(Table("ORDERS", Orders().Table!, schema: "DBO"));

        var entry = Assert.Single(SchemaComparer.Compare(left, right, Defaults).Entries);

        Assert.NotNull(entry.Left);
        Assert.NotNull(entry.Right);
        Assert.Equal(DiffStatus.Modified, entry.Status); // the CREATE statements differ in the name's case
        Assert.Equal("Name differs in case: [dbo].[Orders] → [DBO].[ORDERS]", Assert.Single(entry.Differences));
    }

    [Fact]
    public void Compare_TableWithOneColumnLess_SaysSoAndNamesTheColumn()
    {
        var left = Schema(Orders(Text("Email", 200)));
        var right = Schema(Orders());

        var entry = Assert.Single(SchemaComparer.Compare(left, right, Defaults).Entries);

        Assert.Equal(DiffStatus.Modified, entry.Status);
        Assert.Equal(
            ["4 columns → 3 columns", "Column [Email] only in left: nvarchar(200) COLLATE SQL_Latin1_General_CP1_CI_AS NULL"],
            entry.Differences);
        Assert.Contains("[Email]", entry.LeftScript, StringComparison.Ordinal);
        Assert.DoesNotContain("[Email]", entry.RightScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_ChangedColumn_ListsEachChange()
    {
        var left = Schema(Orders(Text("Note", 50) with { Default = new DefaultConstraintDefinition("DF_Note", "('')", false) }));
        var right = Schema(Orders(Text("Note", 100, nullable: false)));

        var entry = Assert.Single(SchemaComparer.Compare(left, right, Defaults).Entries);

        Assert.Equal(
            "Column [Note]: nvarchar(50) → nvarchar(100); NULL → NOT NULL; CONSTRAINT [DF_Note] DEFAULT ('') → no default",
            Assert.Single(entry.Differences));
    }

    [Fact]
    public void Compare_SystemGeneratedConstraintNames_IgnoredByDefault()
    {
        TableDefinition WithGeneratedNames(string suffix) => new()
        {
            Columns = [Int("Id"), Int("Qty") with { Default = new DefaultConstraintDefinition("DF__Orders__Qty__" + suffix, "((0))", true) }],
            PrimaryKey = new KeyConstraint("PK__Orders__" + suffix, true, true, [new IndexColumn("Id")]),
            CheckConstraints = [new CheckConstraintDefinition("CK__Orders__Qty__" + suffix, true, "([Qty]>=(0))")],
        };
        var left = Schema(Table("Orders", WithGeneratedNames("3B75D760")));
        var right = Schema(Table("Orders", WithGeneratedNames("4A8310C6")));

        var ignoring = Assert.Single(SchemaComparer.Compare(left, right, Defaults).Entries);
        var strict = Assert.Single(SchemaComparer.Compare(left, right, Defaults with { IgnoreSystemNames = false }).Entries);

        Assert.Equal(DiffStatus.Unchanged, ignoring.Status);
        Assert.Equal(DiffStatus.Modified, strict.Status);
        Assert.Contains("Check constraint CHECK ([Qty]>=(0)): name [CK__Orders__Qty__3B75D760] (generated) → [CK__Orders__Qty__4A8310C6] (generated)", strict.Differences);
    }

    [Fact]
    public void Compare_IndexChanges_ReportsAddedRemovedChangedAndRenamed()
    {
        var left = Table("Orders", new TableDefinition
        {
            Columns = [Int("A"), Int("B")],
            Indexes =
            [
                new IndexDefinition("IX_Gone", "NONCLUSTERED", false, [new IndexColumn("A")]),
                new IndexDefinition("IX_Changed", "NONCLUSTERED", false, [new IndexColumn("A")]),
                new IndexDefinition("IX_Old", "NONCLUSTERED", true, [new IndexColumn("B")]),
            ],
        });
        var right = Table("Orders", new TableDefinition
        {
            Columns = [Int("A"), Int("B")],
            Indexes =
            [
                new IndexDefinition("IX_Changed", "NONCLUSTERED", false, [new IndexColumn("A"), new IndexColumn("B")]),
                new IndexDefinition("IX_New", "NONCLUSTERED", true, [new IndexColumn("B")]),
                new IndexDefinition("IX_Added", "NONCLUSTERED", false, [new IndexColumn("B", IsDescending: true)]),
            ],
        });

        var differences = Assert.Single(SchemaComparer.Compare(Schema(left), Schema(right), Defaults).Entries).Differences;

        Assert.Equal(
            [
                "Index [IX_Changed]: NONCLUSTERED ([A]) → NONCLUSTERED ([A], [B])",
                "Index [IX_Gone] only in left: NONCLUSTERED ([A])",
                "Index UNIQUE NONCLUSTERED ([B]): name [IX_Old] → [IX_New]",
                "Index [IX_Added] only in right: NONCLUSTERED ([B] DESC)",
            ],
            differences);
    }

    [Fact]
    public void Compare_ColumnOrder_ReportedUnlessIgnored()
    {
        var left = Schema(Table("T", new TableDefinition { Columns = [Int("A"), Int("B"), Int("C")] }));
        var right = Schema(Table("T", new TableDefinition { Columns = [Int("A"), Int("C"), Int("B")] }));

        var strict = Assert.Single(SchemaComparer.Compare(left, right, Defaults).Entries);
        var ignoring = Assert.Single(SchemaComparer.Compare(left, right, Defaults with { IgnoreColumnOrder = true }).Entries);

        Assert.Equal("Column order differs from position 2: …, [B], [C] → …, [C], [B]", Assert.Single(strict.Differences));
        Assert.Equal(DiffStatus.Unchanged, ignoring.Status);
    }

    [Fact]
    public void Compare_ProcedureDifferingOnlyInWhitespace_IdenticalWhenIgnoringWhitespace()
    {
        var left = new DbObject(DbObjectType.Procedure, "dbo", "GetOrders") { Definition = "CREATE PROCEDURE GetOrders AS\r\n    SELECT * FROM Orders\r\n" };
        var right = left with { Definition = "CREATE PROCEDURE GetOrders AS\n\tSELECT *  FROM Orders" };
        var changed = left with { Definition = "CREATE PROCEDURE GetOrders AS\r\n    SELECT Id FROM Orders\r\n" };

        var ignoring = SchemaComparer.CompareObject(left, right, Defaults);
        var strict = SchemaComparer.CompareObject(left, right, Defaults with { IgnoreWhitespace = false });
        var different = SchemaComparer.CompareObject(left, changed, Defaults);

        Assert.Equal(DiffStatus.Unchanged, ignoring.Status);
        Assert.Equal("Only whitespace differs (ignored)", Assert.Single(ignoring.Differences));
        Assert.Equal(DiffStatus.Modified, strict.Status);
        Assert.Equal("Definition differs: +1 −1 lines", Assert.Single(different.Differences));
    }

    [Fact]
    public void Compare_Sequence_NamesTheChangedProperty()
    {
        DbObject Sequence(string increment) => new(DbObjectType.Sequence, "dbo", "Numbers")
        {
            Definition = DbScripter.ScriptSequence("dbo", "Numbers", new SequenceDefinition("int", "1", increment, "1", "1000", false, true, null)),
        };

        var entry = SchemaComparer.CompareObject(Sequence("1"), Sequence("10"), Defaults);

        Assert.Equal("INCREMENT BY 1 → INCREMENT BY 10", Assert.Single(entry.Differences));
    }

    [Fact]
    public void DescribeDatabases_ReportsCollationAndCompatibilityLevel()
    {
        var right = Info with { Collation = "Latin1_General_CI_AS", CompatibilityLevel = 150 };

        Assert.Equal(
            ["Default collation: SQL_Latin1_General_CP1_CI_AS → Latin1_General_CI_AS", "Compatibility level: 160 → 150"],
            SchemaComparer.DescribeDatabases(Info, right));
    }
}
