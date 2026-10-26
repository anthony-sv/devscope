using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using Devscope.Core.Database;
using Microsoft.Data.SqlClient;

namespace Devscope.SqlServer;

/// <summary>One SQL Server database. Every call opens a pooled connection and only runs SELECT statements.</summary>
internal sealed class SqlServerSource(string connectionString, int commandTimeoutSeconds) : IDatabaseSource
{
    /// <summary>Collation that sorts text by UTF-16 code unit, which <see cref="DbValues.CompareKeys"/> reproduces.</summary>
    private const string BinaryCollation = "Latin1_General_BIN2";

    public string DisplayName { get; } = Describe(connectionString);

    public async Task<DatabaseInfo> TestAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, SqlServerCatalog.InfoQuery, commandTimeoutSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await SqlServerCatalog.ReadInfoAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseSchema> ReadSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, SqlServerCatalog.SchemaQuery, commandTimeoutSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await SqlServerCatalog.ReadSchemaAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<object?[]> ReadRowsAsync(RowQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // No timeout: sorting a large table can take a while before the first row; the user can cancel.
        await using var command = CreateCommand(connection, BuildSelect(query), timeoutSeconds: 0);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);

        var decimals = new bool[reader.FieldCount];
        for (var i = 0; i < decimals.Length; i++)
        {
            decimals[i] = reader.GetDataTypeName(i) is "decimal" or "numeric";
        }

        var finished = false;
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new object?[decimals.Length];
                for (var i = 0; i < row.Length; i++)
                {
                    var value = decimals[i] ? ReadDecimal(reader, i) : reader.GetValue(i);
                    row[i] = value is DBNull ? null : value;
                }

                yield return row;
            }

            finished = true;
        }
        finally
        {
            // Closing a reader with rows left reads them all first; cancelling the command stops the server instead.
            if (!finished)
            {
                command.Cancel();
            }
        }
    }

    /// <summary>The SELECT for a row query, ordered so <see cref="DbValues.CompareKeys"/> agrees with the server.</summary>
    internal static string BuildSelect(RowQuery query)
    {
        var sb = new StringBuilder("SELECT ");
        sb.AppendJoin(", ", query.Columns.Select(c => c.Kind == DbValueKind.Clr
            ? $"CAST({DbNames.Quote(c.Name)} AS varbinary(max)) AS {DbNames.Quote(c.Name)}"
            : DbNames.Quote(c.Name)));
        sb.Append(" FROM ").Append(DbNames.Quote(query.Schema, query.Table));
        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            sb.Append(" WHERE (").Append(query.Filter).Append(')');
        }

        if (query.KeyColumns.Count > 0)
        {
            sb.Append(" ORDER BY ").AppendJoin(", ", query.KeyColumns.Select(i => OrderExpression(query.Columns[i])));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Text keys sort under a binary collation, as Unicode, so the order no longer depends on each column's collation
    /// or code page; other types sort by value on both sides already.
    /// </summary>
    private static string OrderExpression(ColumnDefinition column)
    {
        var name = DbNames.Quote(column.Name);
        if (column.Kind != DbValueKind.Text)
        {
            return name;
        }

        return column.DataType.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase) || column.DataType.StartsWith("nchar", StringComparison.OrdinalIgnoreCase)
            ? $"{name} COLLATE {BinaryCollation}"
            : $"CAST({name} AS nvarchar({(column.DataType.Contains("(max)", StringComparison.OrdinalIgnoreCase) ? "max" : "4000")})) COLLATE {BinaryCollation}";
    }

    /// <summary>decimal(38) values beyond .NET's decimal range stay a <see cref="System.Data.SqlTypes.SqlDecimal"/>.</summary>
    private static object? ReadDecimal(SqlDataReader reader, int ordinal)
    {
        var value = reader.GetSqlDecimal(ordinal);
        if (value.IsNull)
        {
            return null;
        }

        try
        {
            return value.Value;
        }
        catch (OverflowException)
        {
            return value;
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string text, int timeoutSeconds)
    {
        var command = connection.CreateCommand();
#pragma warning disable CA2100 // Identifiers are bracket-quoted; the optional filter is the user's own WHERE condition, by design.
        command.CommandText = text;
#pragma warning restore CA2100
        command.CommandTimeout = timeoutSeconds;
        return command;
    }

    private static string Describe(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return builder.InitialCatalog.Length > 0 ? $"{builder.DataSource}/{builder.InitialCatalog}" : builder.DataSource;
    }
}
