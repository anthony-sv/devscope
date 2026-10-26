namespace Devscope.Core.Database;

/// <summary>Kinds of schema objects compared between two databases.</summary>
public enum DbObjectType
{
    Table,
    View,
    Procedure,
    Function,
    Trigger,
    Type,
    Sequence,
    Synonym,
}

/// <summary>
/// What kind of values a column holds, independent of the exact SQL type. Decides whether a column can order rows
/// (as a key) and how its values are read.
/// </summary>
public enum DbValueKind
{
    /// <summary>Values that cannot order rows reliably: xml, sql_variant, text, ntext, image.</summary>
    Other,
    Text,
    Number,
    Boolean,
    DateTime,
    Time,
    DateTimeOffset,
    UniqueIdentifier,
    Binary,

    /// <summary>rowversion/timestamp: set by the server on every write, so it differs between databases by design.</summary>
    RowVersion,

    /// <summary>CLR types (geography, geometry, hierarchyid), read as their binary form.</summary>
    Clr,
}

/// <summary>The server and database a schema was read from.</summary>
public sealed record DatabaseInfo(
    string Server, string Database, string ProductVersion, string Edition, string Collation, int CompatibilityLevel)
{
    public string DisplayName => $"{Server}/{Database}";
}

/// <summary>Everything read from one database.</summary>
public sealed record DatabaseSchema(DatabaseInfo Info, IReadOnlyList<DbObject> Objects);

/// <summary>One schema object, e.g. a table or a procedure.</summary>
public sealed record DbObject(DbObjectType Type, string Schema, string Name)
{
    /// <summary>Columns, keys and indexes of a table or a table type.</summary>
    public TableDefinition? Table { get; init; }

    /// <summary>
    /// T-SQL of a view, procedure, function or trigger as the server stores it, or the scripted definition of a
    /// sequence, synonym or alias type. Null when the server does not show it (encrypted, or no VIEW DEFINITION).
    /// </summary>
    public string? Definition { get; init; }

    /// <summary>For a trigger, the table or view it belongs to (schema.name).</summary>
    public string? Parent { get; init; }

    /// <summary>For a trigger, whether it is disabled.</summary>
    public bool IsDisabled { get; init; }

    /// <summary>For a table, the row count from metadata: approximate, but free to read.</summary>
    public long? RowCount { get; init; }

    public string FullName => $"{Schema}.{Name}";
}

/// <summary>Columns, keys, indexes and constraints of a table or a table type.</summary>
public sealed record TableDefinition
{
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }

    public KeyConstraint? PrimaryKey { get; init; }

    public IReadOnlyList<KeyConstraint> UniqueConstraints { get; init; } = [];

    /// <summary>Indexes other than the ones behind the primary key and unique constraints.</summary>
    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = [];

    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; init; } = [];

    public IReadOnlyList<CheckConstraintDefinition> CheckConstraints { get; init; } = [];

    public ColumnDefinition? FindColumn(string name) => Columns.FirstOrDefault(c => DbNames.Comparer.Equals(c.Name, name));
}

/// <summary>A table column.</summary>
/// <param name="Name">Column name.</param>
/// <param name="DataType">Type as written in T-SQL, e.g. <c>nvarchar(50)</c>, <c>decimal(18, 2)</c> or <c>[dbo].[Phone]</c>.</param>
/// <param name="IsNullable">Whether NULL is allowed.</param>
public sealed record ColumnDefinition(string Name, string DataType, bool IsNullable)
{
    public DbValueKind Kind { get; init; }

    /// <summary>Collation of a character column; null for other types.</summary>
    public string? Collation { get; init; }

    public IdentityDefinition? Identity { get; init; }

    /// <summary>Expression of a computed column, as the server stores it, e.g. <c>([Price]*[Quantity])</c>.</summary>
    public string? ComputedExpression { get; init; }

    public bool IsPersisted { get; init; }

    public DefaultConstraintDefinition? Default { get; init; }

    public bool IsRowGuid { get; init; }

    public bool IsSparse { get; init; }

    public bool IsComputed => ComputedExpression is not null;
}

/// <param name="Seed">Seed as the server reports it, e.g. <c>1</c>.</param>
/// <param name="Increment">Increment, e.g. <c>1</c>.</param>
public sealed record IdentityDefinition(string Seed, string Increment);

/// <param name="Name">Constraint name.</param>
/// <param name="Expression">Default expression as the server stores it, e.g. <c>('new')</c> or <c>(getdate())</c>.</param>
/// <param name="IsSystemNamed">Whether SQL Server generated the name (DF__Orders__Statu__3B75D760).</param>
public sealed record DefaultConstraintDefinition(string Name, string Expression, bool IsSystemNamed);

/// <summary>A key or index column.</summary>
public sealed record IndexColumn(string Name, bool IsDescending = false);

/// <summary>A primary key or unique constraint.</summary>
public sealed record KeyConstraint(string Name, bool IsSystemNamed, bool IsClustered, IReadOnlyList<IndexColumn> Columns);

/// <summary>An index that does not back a primary key or unique constraint.</summary>
/// <param name="Name">Index name.</param>
/// <param name="Kind">Index type as the server describes it: CLUSTERED, NONCLUSTERED, NONCLUSTERED COLUMNSTORE, XML, ...</param>
/// <param name="IsUnique">Whether the index is unique.</param>
/// <param name="Columns">Key columns, in key order.</param>
public sealed record IndexDefinition(string Name, string Kind, bool IsUnique, IReadOnlyList<IndexColumn> Columns)
{
    public IReadOnlyList<string> IncludedColumns { get; init; } = [];

    /// <summary>Filter of a filtered index as the server stores it, e.g. <c>([Status]='open')</c>.</summary>
    public string? Filter { get; init; }

    public bool IsDisabled { get; init; }
}

/// <summary>A foreign key.</summary>
public sealed record ForeignKeyDefinition(
    string Name,
    bool IsSystemNamed,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns)
{
    /// <summary>NO ACTION, CASCADE, SET NULL or SET DEFAULT.</summary>
    public string OnDelete { get; init; } = "NO ACTION";

    public string OnUpdate { get; init; } = "NO ACTION";

    public bool IsDisabled { get; init; }
}

/// <param name="Name">Constraint name.</param>
/// <param name="IsSystemNamed">Whether SQL Server generated the name.</param>
/// <param name="Expression">Condition as the server stores it, e.g. <c>([Total]&gt;=(0))</c>.</param>
public sealed record CheckConstraintDefinition(string Name, bool IsSystemNamed, string Expression)
{
    public bool IsDisabled { get; init; }
}

/// <summary>Properties of a sequence, used to script it.</summary>
public sealed record SequenceDefinition(
    string DataType, string StartValue, string Increment, string MinValue, string MaxValue, bool IsCycling, bool IsCached, int? CacheSize);

/// <summary>Identifier rules shared by the comparison and the scripts.</summary>
public static class DbNames
{
    /// <summary>
    /// Object and column names are matched case-insensitively, as under the default SQL Server collations, so
    /// <c>dbo.Orders</c> and <c>dbo.orders</c> are the same table.
    /// </summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Brackets an identifier: <c>Order Lines</c> becomes <c>[Order Lines]</c>.</summary>
    public static string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary><c>[schema].[name]</c>.</summary>
    public static string Quote(string schema, string name) => Quote(schema) + "." + Quote(name);
}
