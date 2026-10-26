using Devscope.Core.Database;

namespace Devscope.Core.Tests.Database;

/// <summary>Builders for schema objects, so tests state only what matters to them.</summary>
internal static class DbTestData
{
    public static readonly DatabaseInfo Info = new("sql01", "Billing", "16.0.4305.5", "Developer Edition", "SQL_Latin1_General_CP1_CI_AS", 160);

    public static ColumnDefinition Int(string name, bool nullable = false) => new(name, "int", nullable) { Kind = DbValueKind.Number };

    public static ColumnDefinition Text(string name, int length = 50, bool nullable = true) =>
        new(name, $"nvarchar({length})", nullable) { Kind = DbValueKind.Text, Collation = "SQL_Latin1_General_CP1_CI_AS" };

    public static KeyConstraint Pk(string name, params string[] columns) =>
        new(name, IsSystemNamed: false, IsClustered: true, [.. columns.Select(c => new IndexColumn(c))]);

    public static DbObject Table(string name, TableDefinition table, string schema = "dbo") =>
        new(DbObjectType.Table, schema, name) { Table = table };

    public static DbObject Orders(params ColumnDefinition[] extraColumns) => Table("Orders", new TableDefinition
    {
        Columns = [Int("Id"), Int("CustomerId"), Text("Status", 20, nullable: false), .. extraColumns],
        PrimaryKey = Pk("PK_Orders", "Id"),
    });

    public static DatabaseSchema Schema(params DbObject[] objects) => new(Info, objects);
}
