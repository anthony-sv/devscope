namespace Devscope.Core.Database;

/// <summary>Rows to read from one table for a data comparison.</summary>
/// <param name="Schema">Table schema.</param>
/// <param name="Table">Table name.</param>
/// <param name="Columns">Columns to read, in this order.</param>
/// <param name="KeyColumns">Indexes into <paramref name="Columns"/> to order the rows by; empty when the order does not matter.</param>
/// <param name="Filter">Optional WHERE condition, used as typed (the same on both sides).</param>
public sealed record RowQuery(
    string Schema, string Table, IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<int> KeyColumns, string? Filter);

/// <summary>Streams table rows.</summary>
public interface IRowSource
{
    /// <summary>
    /// Streams the rows of <paramref name="query"/>, one value per column (null for NULL). When key columns are given,
    /// rows arrive in the order <see cref="DbValues.CompareKeys"/> defines: text by UTF-16 code unit with trailing
    /// spaces ignored (as a binary collation sorts it), binary with trailing zero bytes ignored, other types by value.
    /// </summary>
    IAsyncEnumerable<object?[]> ReadRowsAsync(RowQuery query, CancellationToken cancellationToken);
}

/// <summary>One database to compare. Opens a (pooled) connection per call, and only ever reads.</summary>
public interface IDatabaseSource : IRowSource
{
    /// <summary>Server and database, for messages.</summary>
    string DisplayName { get; }

    /// <summary>Connects and reads basic information, to check a connection.</summary>
    Task<DatabaseInfo> TestAsync(CancellationToken cancellationToken);

    /// <summary>Reads every user object: tables with columns, keys and indexes, code, types, sequences and synonyms.</summary>
    Task<DatabaseSchema> ReadSchemaAsync(CancellationToken cancellationToken);
}

/// <summary>A database product (SQL Server) that saved connections can be opened with.</summary>
public interface IDatabaseProvider
{
    /// <summary>Opens a connection profile. <paramref name="password"/> is used for SQL logins.</summary>
    IDatabaseSource Open(DbConnectionProfile profile, string? password);

    /// <summary>
    /// Fills a profile from a connection string (e.g. one copied from a config file). Throws
    /// <see cref="ArgumentException"/> when it cannot be parsed.
    /// </summary>
    DbConnectionProfile ParseConnectionString(string connectionString, out string? password);
}
