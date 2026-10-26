using Devscope.Core.Database;
using static Devscope.Core.Tests.Database.DbTestData;

namespace Devscope.Core.Tests.Database;

public sealed class SyncScripterTests
{
    private static readonly DbObject Orders = Table("Orders", new TableDefinition
    {
        Columns =
        [
            Int("Id") with { Identity = new IdentityDefinition("1", "1") },
            Text("Status"),
            new ColumnDefinition("Total", "decimal(18, 2)", true) { Kind = DbValueKind.Number },
            new ColumnDefinition("Doubled", "decimal(19, 2)", true) { ComputedExpression = "([Total]*(2))" },
        ],
        PrimaryKey = Pk("PK_Orders", "Id"),
    });

    private static RowComparison Comparison(params RowDifference[] samples) =>
        new(RowComparePlanner.Plan(Orders, Orders, new RowCompareSettings()), RowCompareMode.Merge)
        {
            Samples = samples,
            OnlyInLeft = samples.Count(s => s.Kind == RowDiffKind.OnlyInLeft),
            OnlyInRight = samples.Count(s => s.Kind == RowDiffKind.OnlyInRight),
            Different = samples.Count(s => s.Kind == RowDiffKind.Different),
        };

    [Fact]
    public void Script_ToRight_InsertsUpdatesAndDeletes()
    {
        var comparison = Comparison(
            new RowDifference(RowDiffKind.OnlyInLeft, [1, "new", 10.5m, 21m], null),
            new RowDifference(RowDiffKind.Different, [2, "paid", 5m, 10m], [2, "open", 5m, 10m]) { ChangedColumns = [1] },
            new RowDifference(RowDiffKind.OnlyInRight, null, [3, "o'brien", null, null]));

        var script = SyncScripter.Script(comparison, toRight: true);

        Assert.Contains("-- 1 INSERT, 1 UPDATE, 1 DELETE: every difference found.", script, StringComparison.Ordinal);
        Assert.Contains(
            "SET IDENTITY_INSERT [dbo].[Orders] ON;\r\n"
            + "INSERT INTO [dbo].[Orders] ([Id], [Status], [Total]) VALUES (1, N'new', 10.5);\r\n"
            + "SET IDENTITY_INSERT [dbo].[Orders] OFF;\r\n",
            script,
            StringComparison.Ordinal);
        Assert.Contains("UPDATE [dbo].[Orders] SET [Status] = N'paid' WHERE [Id] = 2;", script, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM [dbo].[Orders] WHERE [Id] = 3;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_ToLeft_ReversesTheDirection()
    {
        var comparison = Comparison(
            new RowDifference(RowDiffKind.OnlyInLeft, [1, "new", 10.5m, 21m], null),
            new RowDifference(RowDiffKind.Different, [2, "paid", 5m, 10m], [2, "open", 5m, 10m]) { ChangedColumns = [1] });

        var script = SyncScripter.Script(comparison, toRight: false);

        Assert.Contains("UPDATE [dbo].[Orders] SET [Status] = N'open' WHERE [Id] = 2;", script, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM [dbo].[Orders] WHERE [Id] = 1;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_SamplesIncomplete_SaysHowManyAreCovered()
    {
        var comparison = Comparison(new RowDifference(RowDiffKind.OnlyInRight, null, [3, "x", null, null])) with { OnlyInRight = 5000 };

        var script = SyncScripter.Script(comparison, toRight: true);

        Assert.Contains("only the 1 example rows of 5,000 differences", script, StringComparison.Ordinal);
    }
}
