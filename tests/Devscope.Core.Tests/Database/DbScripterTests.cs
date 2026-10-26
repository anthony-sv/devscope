using Devscope.Core.Database;
using static Devscope.Core.Tests.Database.DbTestData;

namespace Devscope.Core.Tests.Database;

public sealed class DbScripterTests
{
    private static readonly DbCompareOptions Defaults = new();

    [Fact]
    public void ScriptTable_FullTable_OneColumnOrConstraintPerLine()
    {
        var table = new TableDefinition
        {
            Columns =
            [
                Int("Id") with { Identity = new IdentityDefinition("1", "1") },
                Text("Status", 20, nullable: false) with { Default = new DefaultConstraintDefinition("DF_Orders_Status", "('new')", false) },
                new ColumnDefinition("Total", "decimal(18, 2)", true) { Kind = DbValueKind.Number },
                new ColumnDefinition("Tax", "decimal(38, 6)", true) { ComputedExpression = "([Total]*(0.2))", IsPersisted = true },
            ],
            PrimaryKey = Pk("PK_Orders", "Id"),
            ForeignKeys = [new ForeignKeyDefinition("FK_Orders_Customers", false, ["CustomerId"], "dbo", "Customers", ["Id"]) { OnDelete = "CASCADE" }],
            CheckConstraints = [new CheckConstraintDefinition("CK__Orders__Total__1A2B3C", true, "([Total]>=(0))")],
            Indexes = [new IndexDefinition("IX_Orders_Status", "NONCLUSTERED", false, [new IndexColumn("Status")]) { IncludedColumns = ["Total"], Filter = "([Status]<>'done')" }],
        };

        var script = DbScripter.ScriptTable("dbo", "Orders", table, Defaults);

        Assert.Equal(
            """
            CREATE TABLE [dbo].[Orders] (
                [Id] int IDENTITY(1, 1) NOT NULL,
                [Status] nvarchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL CONSTRAINT [DF_Orders_Status] DEFAULT ('new'),
                [Total] decimal(18, 2) NULL,
                [Tax] AS ([Total]*(0.2)) PERSISTED,
                CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([Id]),
                CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customers] ([Id]) ON DELETE CASCADE,
                CHECK ([Total]>=(0))
            );

            CREATE NONCLUSTERED INDEX [IX_Orders_Status] ON [dbo].[Orders] ([Status]) INCLUDE ([Total]) WHERE ([Status]<>'done');
            """.ReplaceLineEndings("\r\n"),
            script);
    }

    [Fact]
    public void ScriptTable_SystemNamedConstraints_NamesShownOnlyWhenNotIgnored()
    {
        var table = new TableDefinition
        {
            Columns = [Int("Qty") with { Default = new DefaultConstraintDefinition("DF__Orders__Qty__3B75D760", "((0))", IsSystemNamed: true) }],
        };

        var ignored = DbScripter.ScriptTable("dbo", "Orders", table, Defaults);
        var shown = DbScripter.ScriptTable("dbo", "Orders", table, Defaults with { IgnoreSystemNames = false });

        Assert.Contains("[Qty] int NOT NULL DEFAULT ((0))", ignored, StringComparison.Ordinal);
        Assert.Contains("[Qty] int NOT NULL CONSTRAINT [DF__Orders__Qty__3B75D760] DEFAULT ((0))", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptTable_IgnoreColumnOrderAndCollation_SortsColumnsAndDropsCollate()
    {
        var table = new TableDefinition { Columns = [Text("Zeta"), Int("Alpha")] };

        var script = DbScripter.ScriptTable("dbo", "T", table, Defaults with { IgnoreColumnOrder = true, IgnoreCollation = true });

        Assert.Contains("    [Alpha] int NOT NULL,\r\n    [Zeta] nvarchar(50) NULL\r\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptTable_DisabledForeignKeyAndIndex_AddsStatements()
    {
        var table = new TableDefinition
        {
            Columns = [Int("CustomerId")],
            ForeignKeys = [new ForeignKeyDefinition("FK_A", false, ["CustomerId"], "dbo", "Customers", ["Id"]) { IsDisabled = true }],
            Indexes = [new IndexDefinition("IX_A", "NONCLUSTERED", false, [new IndexColumn("CustomerId", IsDescending: true)]) { IsDisabled = true }],
        };

        var script = DbScripter.ScriptTable("dbo", "Orders", table, Defaults);

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_A] ON [dbo].[Orders] ([CustomerId] DESC);", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE [dbo].[Orders] NOCHECK CONSTRAINT [FK_A];", script, StringComparison.Ordinal);
        Assert.Contains("ALTER INDEX [IX_A] ON [dbo].[Orders] DISABLE;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptTableType_IndexesAreInline()
    {
        var table = new TableDefinition
        {
            Columns = [Int("Id"), Int("Qty")],
            PrimaryKey = new KeyConstraint("PK__TT_Lines__3214EC07", true, true, [new IndexColumn("Id")]),
            Indexes = [new IndexDefinition("IX_Qty", "NONCLUSTERED", false, [new IndexColumn("Qty")])],
        };

        var script = DbScripter.Script(new DbObject(DbObjectType.Type, "dbo", "Lines") { Table = table }, Defaults);

        Assert.Equal(
            "CREATE TYPE [dbo].[Lines] AS TABLE (\r\n    [Id] int NOT NULL,\r\n    [Qty] int NOT NULL,\r\n"
            + "    PRIMARY KEY CLUSTERED ([Id]),\r\n    INDEX [IX_Qty] NONCLUSTERED ([Qty])\r\n);",
            script);
    }

    [Fact]
    public void ScriptSequence_OnePropertyPerLine()
    {
        var script = DbScripter.ScriptSequence("dbo", "Numbers", new SequenceDefinition("bigint", "1000", "1", "1", "9223372036854775807", false, true, 50));

        Assert.Equal(
            "CREATE SEQUENCE [dbo].[Numbers]\r\n    AS bigint\r\n    START WITH 1000\r\n    INCREMENT BY 1\r\n    MINVALUE 1\r\n"
            + "    MAXVALUE 9223372036854775807\r\n    NO CYCLE\r\n    CACHE 50;",
            script);
    }

    [Fact]
    public void Script_DisabledTriggerAndHiddenDefinition_AreSpelledOut()
    {
        var trigger = new DbObject(DbObjectType.Trigger, "dbo", "trg_Audit") { Definition = "CREATE TRIGGER trg_Audit ON dbo.Orders AFTER UPDATE AS SELECT 1", Parent = "dbo.Orders", IsDisabled = true };
        var hidden = new DbObject(DbObjectType.Procedure, "dbo", "Secret");

        Assert.EndsWith("\r\n\r\nDISABLE TRIGGER [dbo].[trg_Audit] ON [dbo].[Orders];", DbScripter.Script(trigger, Defaults), StringComparison.Ordinal);
        Assert.StartsWith("-- The definition of [dbo].[Secret] is not visible", DbScripter.Script(hidden, Defaults), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varbinary", 16, 0, 0, "varbinary(16)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18, 2)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("int", 4, 10, 0, "int")]
    public void Format_SystemTypes(string type, int maxLength, int precision, int scale, string expected) =>
        Assert.Equal(expected, SqlDataTypes.Format(type, maxLength, precision, scale));

    [Fact]
    public void Quote_EscapesClosingBracket() => Assert.Equal("[a]]b]", DbNames.Quote("a]b"));
}
