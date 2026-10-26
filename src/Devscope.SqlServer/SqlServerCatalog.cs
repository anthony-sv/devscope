using System.Globalization;
using Devscope.Core.Database;
using Microsoft.Data.SqlClient;

namespace Devscope.SqlServer;

/// <summary>
/// Reads a database's user objects from the catalog views in one batch (SQL Server 2012 and later, and Azure SQL),
/// and builds the <see cref="DatabaseSchema"/> the comparison works on.
/// </summary>
internal static class SqlServerCatalog
{
    internal const string InfoQuery = """
        SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)), DB_NAME(), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)),
               CAST(d.compatibility_level AS int)
        FROM sys.databases AS d
        WHERE d.database_id = DB_ID();
        """;

    // One result set per section, read in this order by ReadSchemaAsync. Objects SSMS creates for database diagrams
    // (marked microsoft_database_tools_support) are left out.
    internal const string SchemaQuery = "SET NOCOUNT ON;\r\n" + InfoQuery + """

        SELECT o.object_id, SCHEMA_NAME(o.schema_id), o.name, RTRIM(o.type), o.parent_object_id, ISNULL(t.is_disabled, 0)
        FROM sys.objects AS o
        LEFT JOIN sys.triggers AS t ON t.object_id = o.object_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'TR', 'TA', 'SO', 'SN')
          AND NOT EXISTS (SELECT 1 FROM sys.extended_properties AS ep
                          WHERE ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = N'microsoft_database_tools_support');

        SELECT tt.type_table_object_id, SCHEMA_NAME(tt.schema_id), tt.name
        FROM sys.table_types AS tt
        WHERE tt.is_user_defined = 1;

        SELECT SCHEMA_NAME(t.schema_id), t.name, bt.name, t.max_length, t.precision, t.scale, t.is_nullable
        FROM sys.types AS t
        JOIN sys.types AS bt ON bt.user_type_id = t.system_type_id
        WHERE t.is_user_defined = 1 AND t.is_table_type = 0 AND t.is_assembly_type = 0;

        DECLARE @tables TABLE (object_id int PRIMARY KEY);
        INSERT INTO @tables (object_id)
        SELECT o.object_id FROM sys.objects AS o WHERE o.type = 'U' AND o.is_ms_shipped = 0
        UNION
        SELECT tt.type_table_object_id FROM sys.table_types AS tt WHERE tt.is_user_defined = 1;

        SELECT c.object_id, c.name, tp.name, tp.is_user_defined, tp.is_assembly_type, SCHEMA_NAME(tp.schema_id), bt.name,
               c.max_length, c.precision, c.scale, c.is_nullable, c.collation_name,
               CAST(ic.seed_value AS nvarchar(50)), CAST(ic.increment_value AS nvarchar(50)),
               cc.definition, ISNULL(cc.is_persisted, 0),
               dc.name, dc.definition, ISNULL(dc.is_system_named, 0),
               c.is_rowguidcol, c.is_sparse
        FROM sys.columns AS c
        JOIN @tables AS x ON x.object_id = c.object_id
        JOIN sys.types AS tp ON tp.user_type_id = c.user_type_id
        LEFT JOIN sys.types AS bt ON bt.user_type_id = c.system_type_id AND bt.is_user_defined = 0
        LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        ORDER BY c.object_id, c.column_id;

        SELECT i.object_id, i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint,
               i.filter_definition, i.is_disabled, ISNULL(kc.is_system_named, 0)
        FROM sys.indexes AS i
        JOIN @tables AS x ON x.object_id = i.object_id
        LEFT JOIN sys.key_constraints AS kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
        WHERE i.index_id > 0 AND i.is_hypothetical = 0;

        SELECT ic.object_id, ic.index_id, c.name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column
        FROM sys.index_columns AS ic
        JOIN @tables AS x ON x.object_id = ic.object_id
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        ORDER BY ic.object_id, ic.index_id, ic.key_ordinal, ic.index_column_id;

        SELECT fk.object_id, fk.parent_object_id, fk.name, fk.is_system_named, SCHEMA_NAME(rt.schema_id), rt.name,
               fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_disabled
        FROM sys.foreign_keys AS fk
        JOIN @tables AS x ON x.object_id = fk.parent_object_id
        JOIN sys.objects AS rt ON rt.object_id = fk.referenced_object_id;

        SELECT fkc.constraint_object_id, pc.name, rc.name
        FROM sys.foreign_key_columns AS fkc
        JOIN @tables AS x ON x.object_id = fkc.parent_object_id
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        ORDER BY fkc.constraint_object_id, fkc.constraint_column_id;

        SELECT cc.parent_object_id, cc.name, cc.definition, cc.is_system_named, cc.is_disabled
        FROM sys.check_constraints AS cc
        JOIN @tables AS x ON x.object_id = cc.parent_object_id;

        SELECT m.object_id, m.definition
        FROM sys.sql_modules AS m
        JOIN sys.objects AS o ON o.object_id = m.object_id
        WHERE o.is_ms_shipped = 0 AND o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR');

        SELECT am.object_id, a.name, am.assembly_class, am.assembly_method
        FROM sys.assembly_modules AS am
        JOIN sys.assemblies AS a ON a.assembly_id = am.assembly_id;

        SELECT sq.object_id, TYPE_NAME(sq.user_type_id), sq.precision, sq.scale,
               CAST(sq.start_value AS nvarchar(50)), CAST(sq.increment AS nvarchar(50)),
               CAST(sq.minimum_value AS nvarchar(50)), CAST(sq.maximum_value AS nvarchar(50)),
               sq.is_cycling, sq.is_cached, sq.cache_size
        FROM sys.sequences AS sq;

        SELECT sn.object_id, sn.base_object_name
        FROM sys.synonyms AS sn;

        SELECT p.object_id, SUM(p.rows)
        FROM sys.partitions AS p
        JOIN @tables AS x ON x.object_id = p.object_id
        WHERE p.index_id IN (0, 1)
        GROUP BY p.object_id;
        """;

    internal static async Task<DatabaseInfo> ReadInfoAsync(SqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The database did not describe itself: sys.databases is not visible to this login.");
        }

        return new DatabaseInfo(
            Text(reader, 0) ?? string.Empty,
            Text(reader, 1) ?? string.Empty,
            Text(reader, 2) ?? string.Empty,
            Text(reader, 3) ?? string.Empty,
            Text(reader, 4) ?? string.Empty,
            Int(reader, 5));
    }

    internal static async Task<DatabaseSchema> ReadSchemaAsync(SqlDataReader reader, CancellationToken ct)
    {
        var info = await ReadInfoAsync(reader, ct).ConfigureAwait(false);

        var objects = new Dictionary<int, ObjectRow>();
        await ReadAllAsync(reader, r => objects[Int(r, 0)] = new ObjectRow(Text(r, 1)!, Text(r, 2)!, Text(r, 3)!, Int(r, 4), Bool(r, 5)), ct).ConfigureAwait(false);

        var tableTypes = new List<(int Id, string Schema, string Name)>();
        await ReadAllAsync(reader, r => tableTypes.Add((Int(r, 0), Text(r, 1)!, Text(r, 2)!)), ct).ConfigureAwait(false);

        var result = new List<DbObject>();
        await ReadAllAsync(reader, r =>
        {
            var baseType = SqlDataTypes.Format(Text(r, 2)!, Int(r, 3), Int(r, 4), Int(r, 5));
            result.Add(new DbObject(DbObjectType.Type, Text(r, 0)!, Text(r, 1)!)
            {
                Definition = DbScripter.ScriptAliasType(Text(r, 0)!, Text(r, 1)!, baseType, Bool(r, 6)),
            });
        }, ct).ConfigureAwait(false);

        var columns = new Dictionary<int, List<ColumnDefinition>>();
        await ReadAllAsync(reader, r => List(columns, Int(r, 0)).Add(ReadColumn(r)), ct).ConfigureAwait(false);

        var indexes = new Dictionary<(int ObjectId, int IndexId), IndexRow>();
        await ReadAllAsync(reader, r => indexes[(Int(r, 0), Int(r, 1))] = new IndexRow(
            Text(r, 2) ?? string.Empty, Text(r, 3)!, Bool(r, 4), Bool(r, 5), Bool(r, 6), Text(r, 7), Bool(r, 8), Bool(r, 9)), ct).ConfigureAwait(false);

        await ReadAllAsync(reader, r =>
        {
            if (!indexes.TryGetValue((Int(r, 0), Int(r, 1)), out var index))
            {
                return;
            }

            var keyOrdinal = Int(r, 3);
            if (keyOrdinal > 0)
            {
                index.Keys.Add((keyOrdinal, new IndexColumn(Text(r, 2)!, Bool(r, 4))));
            }
            else if (Bool(r, 5))
            {
                index.Included.Add(Text(r, 2)!);
            }

            // Otherwise a partitioning column, which is not part of the index definition.
        }, ct).ConfigureAwait(false);

        var foreignKeys = new Dictionary<int, ForeignKeyRow>();
        await ReadAllAsync(reader, r => foreignKeys[Int(r, 0)] = new ForeignKeyRow(Int(r, 1), new ForeignKeyDefinition(
            Text(r, 2)!, Bool(r, 3), [], Text(r, 4)!, Text(r, 5)!, [])
        {
            OnDelete = Text(r, 6)!.Replace('_', ' '),
            OnUpdate = Text(r, 7)!.Replace('_', ' '),
            IsDisabled = Bool(r, 8),
        }), ct).ConfigureAwait(false);

        await ReadAllAsync(reader, r =>
        {
            if (foreignKeys.TryGetValue(Int(r, 0), out var fk))
            {
                fk.Columns.Add(Text(r, 1)!);
                fk.ReferencedColumns.Add(Text(r, 2)!);
            }
        }, ct).ConfigureAwait(false);

        var checks = new Dictionary<int, List<CheckConstraintDefinition>>();
        await ReadAllAsync(reader, r => List(checks, Int(r, 0)).Add(
            new CheckConstraintDefinition(Text(r, 1)!, Bool(r, 3), Text(r, 2)!) { IsDisabled = Bool(r, 4) }), ct).ConfigureAwait(false);

        var definitions = new Dictionary<int, string?>();
        await ReadAllAsync(reader, r => definitions[Int(r, 0)] = Text(r, 1), ct).ConfigureAwait(false);
        await ReadAllAsync(reader, r => definitions[Int(r, 0)] =
            $"-- CLR object: EXTERNAL NAME {DbNames.Quote(Text(r, 1)!)}.{DbNames.Quote(Text(r, 2) ?? string.Empty)}.{DbNames.Quote(Text(r, 3) ?? string.Empty)}", ct).ConfigureAwait(false);

        var sequences = new Dictionary<int, SequenceDefinition>();
        await ReadAllAsync(reader, r => sequences[Int(r, 0)] = new SequenceDefinition(
            SqlDataTypes.Format(Text(r, 1)!, 0, Int(r, 2), Int(r, 3)), Text(r, 4)!, Text(r, 5)!, Text(r, 6)!, Text(r, 7)!,
            Bool(r, 8), Bool(r, 9), r.IsDBNull(10) ? null : Int(r, 10)), ct).ConfigureAwait(false);

        var synonyms = new Dictionary<int, string>();
        await ReadAllAsync(reader, r => synonyms[Int(r, 0)] = Text(r, 1)!, ct).ConfigureAwait(false);

        var rowCounts = new Dictionary<int, long>();
        await ReadAllAsync(reader, r => rowCounts[Int(r, 0)] = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        var indexesByTable = indexes.ToLookup(i => i.Key.ObjectId, i => i.Value);
        var foreignKeysByTable = foreignKeys.Values.ToLookup(fk => fk.ParentId);
        TableDefinition Table(int id) => BuildTable(
            columns.GetValueOrDefault(id) ?? [], indexesByTable[id], foreignKeysByTable[id], checks.GetValueOrDefault(id) ?? []);

        foreach (var (id, o) in objects)
        {
            var type = o.Type switch
            {
                "U" => DbObjectType.Table,
                "V" => DbObjectType.View,
                "P" or "PC" => DbObjectType.Procedure,
                "FN" or "IF" or "TF" or "FS" or "FT" => DbObjectType.Function,
                "TR" or "TA" => DbObjectType.Trigger,
                "SO" => DbObjectType.Sequence,
                _ => DbObjectType.Synonym,
            };
            var obj = new DbObject(type, o.Schema, o.Name);
            result.Add(type switch
            {
                DbObjectType.Table => obj with { Table = Table(id), RowCount = rowCounts.GetValueOrDefault(id) },
                DbObjectType.Sequence => obj with
                {
                    Definition = sequences.TryGetValue(id, out var sequence) ? DbScripter.ScriptSequence(o.Schema, o.Name, sequence) : null,
                },
                DbObjectType.Synonym => obj with
                {
                    Definition = synonyms.TryGetValue(id, out var target) ? DbScripter.ScriptSynonym(o.Schema, o.Name, target) : null,
                },
                DbObjectType.Trigger => obj with
                {
                    Definition = definitions.GetValueOrDefault(id),
                    Parent = objects.TryGetValue(o.ParentId, out var parent) ? $"{parent.Schema}.{parent.Name}" : null,
                    IsDisabled = o.IsDisabled,
                },
                _ => obj with { Definition = definitions.GetValueOrDefault(id) },
            });
        }

        result.AddRange(tableTypes.Select(t => new DbObject(DbObjectType.Type, t.Schema, t.Name) { Table = Table(t.Id) }));
        return new DatabaseSchema(info, result);
    }

    private static ColumnDefinition ReadColumn(SqlDataReader r)
    {
        var typeName = Text(r, 2)!;
        var isUserDefined = Bool(r, 3);
        var isAssemblyType = Bool(r, 4);
        var baseType = Text(r, 6) ?? typeName;
        var dataType = isUserDefined
            ? DbNames.Quote(Text(r, 5)!, typeName)
            : SqlDataTypes.Format(typeName, Int(r, 7), Int(r, 8), Int(r, 9));
        return new ColumnDefinition(Text(r, 1)!, dataType, Bool(r, 10))
        {
            Kind = isAssemblyType ? DbValueKind.Clr : SqlDataTypes.KindOf(baseType),
            Collation = Text(r, 11),
            Identity = r.IsDBNull(12) ? null : new IdentityDefinition(Text(r, 12)!, Text(r, 13)!),
            ComputedExpression = Text(r, 14),
            IsPersisted = Bool(r, 15),
            Default = r.IsDBNull(16) ? null : new DefaultConstraintDefinition(Text(r, 16)!, Text(r, 17)!, Bool(r, 18)),
            IsRowGuid = Bool(r, 19),
            IsSparse = Bool(r, 20),
        };
    }

    private static TableDefinition BuildTable(
        List<ColumnDefinition> columns,
        IEnumerable<IndexRow> indexes,
        IEnumerable<ForeignKeyRow> foreignKeys,
        List<CheckConstraintDefinition> checks)
    {
        KeyConstraint? primaryKey = null;
        var uniqueConstraints = new List<KeyConstraint>();
        var otherIndexes = new List<IndexDefinition>();
        foreach (var index in indexes)
        {
            var keys = index.Keys.OrderBy(k => k.Ordinal).Select(k => k.Column).ToList();
            var clustered = index.Kind.Equals("CLUSTERED", StringComparison.OrdinalIgnoreCase);
            if (index.IsPrimaryKey)
            {
                primaryKey = new KeyConstraint(index.Name, index.IsSystemNamed, clustered, keys);
            }
            else if (index.IsUniqueConstraint)
            {
                uniqueConstraints.Add(new KeyConstraint(index.Name, index.IsSystemNamed, clustered, keys));
            }
            else if (index.Kind.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase))
            {
                // Columnstore indexes list their columns as included columns, in no particular order.
                otherIndexes.Add(new IndexDefinition(index.Name, index.Kind, index.IsUnique,
                    [.. index.Included.Order(DbNames.Comparer).Select(c => new IndexColumn(c))]) { IsDisabled = index.IsDisabled });
            }
            else
            {
                otherIndexes.Add(new IndexDefinition(index.Name, index.Kind, index.IsUnique, keys)
                {
                    IncludedColumns = index.Included,
                    Filter = index.Filter,
                    IsDisabled = index.IsDisabled,
                });
            }
        }

        return new TableDefinition
        {
            Columns = columns,
            PrimaryKey = primaryKey,
            UniqueConstraints = uniqueConstraints,
            Indexes = otherIndexes,
            ForeignKeys = [.. foreignKeys.Select(fk => fk.Definition with
            {
                Columns = [.. fk.Columns],
                ReferencedColumns = [.. fk.ReferencedColumns],
            })],
            CheckConstraints = checks,
        };
    }

    private static async Task ReadAllAsync(SqlDataReader reader, Action<SqlDataReader> read, CancellationToken ct)
    {
        if (!await reader.NextResultAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The server returned fewer catalog results than expected.");
        }

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            read(reader);
        }
    }

    private static List<T> List<T>(Dictionary<int, List<T>> lists, int key)
    {
        if (!lists.TryGetValue(key, out var list))
        {
            lists[key] = list = [];
        }

        return list;
    }

    private static string? Text(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int Int(SqlDataReader reader, int ordinal) => Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool Bool(SqlDataReader reader, int ordinal) => !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private sealed record ObjectRow(string Schema, string Name, string Type, int ParentId, bool IsDisabled);

    private sealed record IndexRow(
        string Name, string Kind, bool IsUnique, bool IsPrimaryKey, bool IsUniqueConstraint, string? Filter, bool IsDisabled, bool IsSystemNamed)
    {
        public List<(int Ordinal, IndexColumn Column)> Keys { get; } = [];

        public List<string> Included { get; } = [];
    }

    private sealed record ForeignKeyRow(int ParentId, ForeignKeyDefinition Definition)
    {
        public List<string> Columns { get; } = [];

        public List<string> ReferencedColumns { get; } = [];
    }
}
