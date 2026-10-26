using System.Globalization;
using System.Text;

namespace Devscope.Core.Database;

/// <summary>
/// What counts as a difference. The options shape the scripts too, so the script diff always shows exactly what an
/// object's status is based on.
/// </summary>
public sealed record DbCompareOptions
{
    /// <summary>
    /// Leave out the names SQL Server generates for unnamed constraints (DF__Orders__Statu__3B75D760), which differ in
    /// every database even when the constraint is the same.
    /// </summary>
    public bool IgnoreSystemNames { get; init; } = true;

    /// <summary>Compare code ignoring whitespace: indentation, blank lines and line endings.</summary>
    public bool IgnoreWhitespace { get; init; } = true;

    /// <summary>Treat a table with the same columns in another order as identical.</summary>
    public bool IgnoreColumnOrder { get; init; }

    /// <summary>Leave column collations out, e.g. between servers with different default collations.</summary>
    public bool IgnoreCollation { get; init; }
}

/// <summary>
/// Writes objects as deterministic T-SQL: one column or constraint per line and everything in a stable order, so
/// two databases script identically when they match and a line diff pinpoints what differs.
/// </summary>
public static class DbScripter
{
    private const string NewLine = "\r\n";
    private const string Indent = "    ";

    /// <summary>Scripts an object as the comparison sees it.</summary>
    public static string Script(DbObject obj, DbCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(options);

        if (obj.Table is { } table)
        {
            return obj.Type == DbObjectType.Table
                ? ScriptTable(obj.Schema, obj.Name, table, options)
                : ScriptTableType(obj.Schema, obj.Name, table, options);
        }

        var definition = obj.Definition?.Trim('\r', '\n')
            ?? $"-- The definition of {DbNames.Quote(obj.Schema, obj.Name)} is not visible: the object is encrypted, "
            + "or the login lacks the VIEW DEFINITION permission.";
        return obj is { Type: DbObjectType.Trigger, IsDisabled: true }
            ? $"{definition}{NewLine}{NewLine}DISABLE TRIGGER {DbNames.Quote(obj.Schema, obj.Name)} ON {QuoteQualified(obj.Parent ?? "?")};"
            : definition;
    }

    public static string ScriptTable(string schema, string name, TableDefinition table, DbCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);

        var qualified = DbNames.Quote(schema, name);
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(qualified).Append(" (").Append(NewLine);
        AppendBody(sb, table, options, inlineIndexes: false);
        sb.Append(");");

        var indexes = SortedIndexes(table);
        if (indexes.Count > 0)
        {
            sb.Append(NewLine);
            foreach (var index in indexes)
            {
                sb.Append(NewLine).Append(ScriptIndex(index, qualified)).Append(';');
            }
        }

        // Disabled constraints and indexes are their own statements, as SQL Server scripts them.
        var disabled = new List<string>();
        foreach (var fk in SortedForeignKeys(table, options).Where(fk => fk.IsDisabled))
        {
            disabled.Add(DisableConstraint(qualified, fk.Name, ShowName(fk.IsSystemNamed, options), ForeignKeyText(fk)));
        }

        foreach (var check in SortedChecks(table, options).Where(c => c.IsDisabled))
        {
            disabled.Add(DisableConstraint(qualified, check.Name, ShowName(check.IsSystemNamed, options), CheckText(check)));
        }

        disabled.AddRange(indexes.Where(i => i.IsDisabled).Select(i => $"ALTER INDEX {DbNames.Quote(i.Name)} ON {qualified} DISABLE;"));
        if (disabled.Count > 0)
        {
            sb.Append(NewLine);
            foreach (var line in disabled)
            {
                sb.Append(NewLine).Append(line);
            }
        }

        return sb.ToString();
    }

    public static string ScriptTableType(string schema, string name, TableDefinition table, DbCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);

        var sb = new StringBuilder();
        sb.Append("CREATE TYPE ").Append(DbNames.Quote(schema, name)).Append(" AS TABLE (").Append(NewLine);
        AppendBody(sb, table, options, inlineIndexes: true);
        sb.Append(");");
        return sb.ToString();
    }

    public static string ScriptSequence(string schema, string name, SequenceDefinition sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        // One property per line, so the diff names the property that changed.
        var cache = !sequence.IsCached ? "NO CACHE"
            : sequence.CacheSize is { } size ? string.Create(CultureInfo.InvariantCulture, $"CACHE {size}")
            : "CACHE";
        return string.Join(
            NewLine,
            $"CREATE SEQUENCE {DbNames.Quote(schema, name)}",
            $"{Indent}AS {sequence.DataType}",
            $"{Indent}START WITH {sequence.StartValue}",
            $"{Indent}INCREMENT BY {sequence.Increment}",
            $"{Indent}MINVALUE {sequence.MinValue}",
            $"{Indent}MAXVALUE {sequence.MaxValue}",
            $"{Indent}{(sequence.IsCycling ? "CYCLE" : "NO CYCLE")}",
            $"{Indent}{cache};");
    }

    /// <param name="schema">Synonym schema.</param>
    /// <param name="name">Synonym name.</param>
    /// <param name="baseObject">What it points to, as the server stores it, e.g. <c>[Archive].[dbo].[Orders]</c>.</param>
    public static string ScriptSynonym(string schema, string name, string baseObject) =>
        $"CREATE SYNONYM {DbNames.Quote(schema, name)} FOR {baseObject};";

    /// <param name="schema">Type schema.</param>
    /// <param name="name">Type name.</param>
    /// <param name="baseType">Base type as T-SQL, e.g. <c>nvarchar(20)</c>.</param>
    /// <param name="isNullable">Whether the type allows NULL.</param>
    public static string ScriptAliasType(string schema, string name, string baseType, bool isNullable) =>
        $"CREATE TYPE {DbNames.Quote(schema, name)} FROM {baseType} {(isNullable ? "NULL" : "NOT NULL")};";

    /// <summary>A column as written in CREATE TABLE, e.g. <c>[Id] int IDENTITY(1, 1) NOT NULL</c>.</summary>
    public static string ColumnText(ColumnDefinition column, DbCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(options);

        var sb = new StringBuilder(DbNames.Quote(column.Name));
        if (column.ComputedExpression is { } expression)
        {
            sb.Append(" AS ").Append(expression);
            if (column.IsPersisted)
            {
                sb.Append(" PERSISTED");
                if (!column.IsNullable)
                {
                    sb.Append(" NOT NULL");
                }
            }

            return sb.ToString();
        }

        sb.Append(' ').Append(column.DataType);
        if (column.Collation is { } collation && !options.IgnoreCollation)
        {
            sb.Append(" COLLATE ").Append(collation);
        }

        if (column.IsSparse)
        {
            sb.Append(" SPARSE");
        }

        if (column.Identity is { } identity)
        {
            sb.Append(" IDENTITY(").Append(identity.Seed).Append(", ").Append(identity.Increment).Append(')');
        }

        if (column.IsRowGuid)
        {
            sb.Append(" ROWGUIDCOL");
        }

        sb.Append(column.IsNullable ? " NULL" : " NOT NULL");
        if (column.Default is { } defaultConstraint)
        {
            sb.Append(' ').Append(ConstraintPrefix(defaultConstraint.Name, ShowName(defaultConstraint.IsSystemNamed, options)))
                .Append("DEFAULT ").Append(defaultConstraint.Expression);
        }

        return sb.ToString();
    }

    /// <summary>A primary key or unique constraint without its name, e.g. <c>PRIMARY KEY CLUSTERED ([Id])</c>.</summary>
    public static string KeyText(KeyConstraint key, bool isPrimaryKey)
    {
        ArgumentNullException.ThrowIfNull(key);
        return $"{(isPrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {(key.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({ColumnList(key.Columns)})";
    }

    /// <summary>A foreign key without its name.</summary>
    public static string ForeignKeyText(ForeignKeyDefinition fk)
    {
        ArgumentNullException.ThrowIfNull(fk);
        var sb = new StringBuilder()
            .Append("FOREIGN KEY (").Append(string.Join(", ", fk.Columns.Select(DbNames.Quote))).Append(") REFERENCES ")
            .Append(DbNames.Quote(fk.ReferencedSchema, fk.ReferencedTable))
            .Append(" (").Append(string.Join(", ", fk.ReferencedColumns.Select(DbNames.Quote))).Append(')');
        if (!IsNoAction(fk.OnDelete))
        {
            sb.Append(" ON DELETE ").Append(fk.OnDelete);
        }

        if (!IsNoAction(fk.OnUpdate))
        {
            sb.Append(" ON UPDATE ").Append(fk.OnUpdate);
        }

        return sb.ToString();
    }

    /// <summary>A check constraint without its name.</summary>
    public static string CheckText(CheckConstraintDefinition check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return $"CHECK {check.Expression}";
    }

    /// <summary>An index without its name, e.g. <c>UNIQUE NONCLUSTERED ([Code]) INCLUDE ([Name]) WHERE ([Active]=(1))</c>.</summary>
    public static string IndexText(IndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var sb = new StringBuilder();
        if (index.IsUnique)
        {
            sb.Append("UNIQUE ");
        }

        sb.Append(index.Kind);
        if (index.Columns.Count > 0)
        {
            sb.Append(" (").Append(ColumnList(index.Columns)).Append(')');
        }

        if (index.IncludedColumns.Count > 0)
        {
            // Included columns are unordered.
            sb.Append(" INCLUDE (").Append(string.Join(", ", index.IncludedColumns.Order(DbNames.Comparer).Select(DbNames.Quote))).Append(')');
        }

        if (index.Filter is { } filter)
        {
            sb.Append(" WHERE ").Append(filter);
        }

        return sb.ToString();
    }

    /// <summary>Whether the comparison shows a constraint's name.</summary>
    public static bool ShowName(bool isSystemNamed, DbCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !(isSystemNamed && options.IgnoreSystemNames);
    }

    /// <summary>Columns in the order the scripts list them.</summary>
    public static IReadOnlyList<ColumnDefinition> OrderedColumns(TableDefinition table, DbCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);
        return options.IgnoreColumnOrder ? [.. table.Columns.OrderBy(c => c.Name, DbNames.Comparer)] : table.Columns;
    }

    private static void AppendBody(StringBuilder sb, TableDefinition table, DbCompareOptions options, bool inlineIndexes)
    {
        var lines = new List<string>();
        lines.AddRange(OrderedColumns(table, options).Select(c => ColumnText(c, options)));
        if (table.PrimaryKey is { } pk)
        {
            lines.Add(ConstraintPrefix(pk.Name, ShowName(pk.IsSystemNamed, options)) + KeyText(pk, isPrimaryKey: true));
        }

        lines.AddRange(SortedUniqueConstraints(table, options)
            .Select(u => ConstraintPrefix(u.Name, ShowName(u.IsSystemNamed, options)) + KeyText(u, isPrimaryKey: false)));
        lines.AddRange(SortedForeignKeys(table, options)
            .Select(fk => ConstraintPrefix(fk.Name, ShowName(fk.IsSystemNamed, options)) + ForeignKeyText(fk)));
        lines.AddRange(SortedChecks(table, options)
            .Select(c => ConstraintPrefix(c.Name, ShowName(c.IsSystemNamed, options)) + CheckText(c)));
        if (inlineIndexes)
        {
            // Table types declare their indexes inline.
            lines.AddRange(SortedIndexes(table).Select(i => $"INDEX {DbNames.Quote(i.Name)} {IndexText(i)}"));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append(Indent).Append(lines[i]).Append(i < lines.Count - 1 ? "," : string.Empty).Append(NewLine);
        }
    }

    private static string ScriptIndex(IndexDefinition index, string qualifiedTable)
    {
        var text = IndexText(index);

        // CREATE UNIQUE NONCLUSTERED INDEX [IX] ON [dbo].[T] ([A]) ...: the name goes between the type and the columns.
        var columnsAt = text.IndexOf(" (", StringComparison.Ordinal);
        var head = columnsAt < 0 ? text : text[..columnsAt];
        var tail = columnsAt < 0 ? string.Empty : text[columnsAt..];
        return $"CREATE {head} INDEX {DbNames.Quote(index.Name)} ON {qualifiedTable}{tail}";
    }

    private static string DisableConstraint(string qualifiedTable, string name, bool showName, string definition) => showName
        ? $"ALTER TABLE {qualifiedTable} NOCHECK CONSTRAINT {DbNames.Quote(name)};"
        : $"-- Disabled: {definition}";

    private static string ConstraintPrefix(string name, bool showName) => showName ? $"CONSTRAINT {DbNames.Quote(name)} " : string.Empty;

    private static string ColumnList(IEnumerable<IndexColumn> columns) =>
        string.Join(", ", columns.Select(c => c.IsDescending ? DbNames.Quote(c.Name) + " DESC" : DbNames.Quote(c.Name)));

    private static bool IsNoAction(string action) =>
        string.IsNullOrEmpty(action) || action.Replace('_', ' ').Equals("NO ACTION", StringComparison.OrdinalIgnoreCase);

    private static string QuoteQualified(string schemaDotName)
    {
        var dot = schemaDotName.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? DbNames.Quote(schemaDotName) : DbNames.Quote(schemaDotName[..dot], schemaDotName[(dot + 1)..]);
    }

    // Constraints sort by name, or by definition when the name is hidden, so both sides list them in the same order.
    internal static IReadOnlyList<KeyConstraint> SortedUniqueConstraints(TableDefinition table, DbCompareOptions options) =>
        [.. table.UniqueConstraints.OrderBy(u => ShowName(u.IsSystemNamed, options) ? u.Name : "~" + KeyText(u, false), DbNames.Comparer)];

    internal static IReadOnlyList<ForeignKeyDefinition> SortedForeignKeys(TableDefinition table, DbCompareOptions options) =>
        [.. table.ForeignKeys.OrderBy(fk => ShowName(fk.IsSystemNamed, options) ? fk.Name : "~" + ForeignKeyText(fk), DbNames.Comparer)];

    internal static IReadOnlyList<CheckConstraintDefinition> SortedChecks(TableDefinition table, DbCompareOptions options) =>
        [.. table.CheckConstraints.OrderBy(c => ShowName(c.IsSystemNamed, options) ? c.Name : "~" + CheckText(c), DbNames.Comparer)];

    internal static IReadOnlyList<IndexDefinition> SortedIndexes(TableDefinition table) =>
        [.. table.Indexes.OrderBy(i => i.Name, DbNames.Comparer)];
}
