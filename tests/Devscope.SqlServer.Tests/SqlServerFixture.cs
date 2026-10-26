using Devscope.Core.Database;
using Microsoft.Data.SqlClient;

namespace Devscope.SqlServer.Tests;

/// <summary>
/// Two scratch databases with known differences, on the server named by the DEVSCOPE_TEST_SQLSERVER environment
/// variable (a connection string whose login may create databases). Without it, the tests that need it are skipped.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "DEVSCOPE_TEST_SQLSERVER";

    private const string Common = """
        CREATE TYPE dbo.OrderLines AS TABLE (Id int PRIMARY KEY, Qty int NOT NULL);
        CREATE TABLE dbo.Customers (
            Id int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
            Name nvarchar(100) NOT NULL,
            City nvarchar(50) NULL,
            Created datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
        CREATE SYNONYM dbo.Clients FOR dbo.Customers;
        CREATE TABLE dbo.Data (
            Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Amount decimal(38, 0) NULL, Shape geography NULL, Doc xml NULL,
            At datetimeoffset NULL, Span time NULL, Variant sql_variant NULL, Bin varbinary(20) NULL);
        CREATE TABLE dbo.TextKeys (K nvarchar(20) COLLATE Latin1_General_CI_AS NOT NULL PRIMARY KEY, V int NULL);
        CREATE TABLE dbo.VarcharKeys (K varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL PRIMARY KEY, V int NULL);
        CREATE TABLE dbo.GuidKeys (K uniqueidentifier NOT NULL PRIMARY KEY, V int NULL);
        CREATE TABLE dbo.BinKeys (K varbinary(10) NOT NULL PRIMARY KEY);
        CREATE TABLE dbo.Heap (Message nvarchar(50) NULL, Level int NULL);
        CREATE TABLE dbo.Big (Id int NOT NULL PRIMARY KEY, Filler nchar(200) NOT NULL);
        INSERT INTO dbo.Big (Id, Filler) SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'x' FROM sys.all_columns a CROSS JOIN sys.all_columns b;
        """;

    // A table can only use an alias type created in an earlier batch.
    private const string LeftTypes = "CREATE TYPE dbo.Phone FROM nvarchar(20) NOT NULL;";

    private const string RightTypes = "CREATE TYPE dbo.Phone FROM nvarchar(30) NOT NULL;";

    private const string Left = """
        CREATE SEQUENCE dbo.OrderNumbers AS bigint START WITH 1000 INCREMENT BY 1;
        CREATE TABLE dbo.Orders (
            Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (Id),
            Status varchar(20) NOT NULL CONSTRAINT DF_Orders_Status DEFAULT ('new'),
            Total decimal(18, 2) NULL CHECK (Total >= 0),
            Phone dbo.Phone NULL,
            Doubled AS (Total * 2),
            Version rowversion);
        CREATE INDEX IX_Orders_Customer ON dbo.Orders (CustomerId) INCLUDE (Total);
        CREATE TABLE dbo.Legacy (Id int NULL);
        EXEC (N'CREATE VIEW dbo.vOrders AS SELECT Id, Total FROM dbo.Orders');
        EXEC (N'CREATE PROCEDURE dbo.GetOrders AS SELECT Id FROM dbo.Orders');
        EXEC (N'CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER UPDATE AS SET NOCOUNT ON');
        INSERT INTO dbo.Data (Id, Name, Amount, Shape, Doc, At, Span, Variant, Bin) VALUES
            (1, N'one', 12345678901234567890123456789012345678, geography::Point(47.65, -122.34, 4326), N'<a>1</a>', '2026-01-01T10:00:00+01:00', '10:00', CAST(1 AS sql_variant), 0x01),
            (2, N'two', 2, NULL, NULL, NULL, NULL, CAST(N'text' AS sql_variant), NULL),
            (3, N'three', 3, NULL, NULL, NULL, NULL, NULL, NULL),
            (4, N'four', 4, NULL, NULL, NULL, NULL, NULL, NULL);
        INSERT INTO dbo.Heap VALUES (N'a', 1), (N'a', 1), (N'b', 2);
        """;

    private const string Right = """
        CREATE SEQUENCE dbo.OrderNumbers AS bigint START WITH 1000 INCREMENT BY 10;
        CREATE TABLE dbo.Orders (
            Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (Id),
            Status varchar(30) NOT NULL CONSTRAINT DF_Orders_Status DEFAULT ('new'),
            Total decimal(18, 2) NULL CHECK (Total >= 0),
            Phone dbo.Phone NULL,
            Doubled AS (Total * 2),
            Version rowversion,
            Email nvarchar(200) NULL);
        CREATE INDEX IX_Orders_Customer ON dbo.Orders (CustomerId, Status) INCLUDE (Total);
        CREATE TABLE dbo.Audit (Id bigint IDENTITY PRIMARY KEY, At datetime2 NOT NULL);
        EXEC (N'CREATE VIEW dbo.vOrders
            AS
            SELECT Id, Total
            FROM dbo.Orders');
        EXEC (N'CREATE PROCEDURE dbo.GetOrders AS SELECT Id, Total FROM dbo.Orders');
        EXEC (N'CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER UPDATE AS SET NOCOUNT ON');
        DISABLE TRIGGER dbo.trg_Orders ON dbo.Orders;
        INSERT INTO dbo.Data (Id, Name, Amount, Shape, Doc, At, Span, Variant, Bin) VALUES
            (1, N'one', 12345678901234567890123456789012345678, geography::Point(47.65, -122.34, 4326), N'<a>1</a>', '2026-01-01T10:00:00+01:00', '10:00', CAST(1 AS sql_variant), 0x01),
            (2, N'two', 2, NULL, NULL, NULL, NULL, CAST(N'text' AS sql_variant), NULL),
            (3, N'THREE', 3, NULL, NULL, NULL, NULL, NULL, NULL),
            (5, N'five', 5, NULL, NULL, NULL, NULL, NULL, NULL);
        INSERT INTO dbo.Heap VALUES (N'a', 1), (N'c', 3);
        """;

    // The same keys on both sides, minus one each, chosen so the columns' own collations order them differently
    // from a binary collation (case-insensitive, accents, a tab, punctuation, CJK; CP1252's euro sign).
    private const string Keys = """
        INSERT INTO dbo.TextKeys (K, V) VALUES (N'a', 1), (N'B', 2), (N'c', 3), (N'_', 4), (N'á', 5), (N'中', 6), (N'a!', 7), (N'a' + NCHAR(9), 8), (N'Z', 9), (N'zz', 10);
        INSERT INTO dbo.VarcharKeys (K, V) VALUES ('e', 1), ('é', 2), ('€', 3), ('E2', 4), ('z', 5), ('_', 6), ('ä', 7);
        INSERT INTO dbo.BinKeys (K) VALUES (0x00), (0x01), (0x0102), (0x02), (0xFF), (0x0001), (0x7F80);
        INSERT INTO dbo.GuidKeys (K, V)
        SELECT TOP (300) CAST(HASHBYTES('MD5', CAST(n AS varchar(10))) AS uniqueidentifier), n
        FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_columns) AS numbers;
        """;

    public string? ConnectionString { get; } = Environment.GetEnvironmentVariable(EnvironmentVariable);

    public string LeftDatabase { get; } = $"DevscopeTest_{Guid.NewGuid():N}_L";

    public string RightDatabase { get; } = $"DevscopeTest_{Guid.NewGuid():N}_R";

    public SqlServerProvider Provider { get; } = new();

    public IDatabaseSource OpenLeft() => Open(LeftDatabase);

    public IDatabaseSource OpenRight() => Open(RightDatabase);

    public void SkipWithoutServer() =>
        Assert.SkipWhen(ConnectionString is null, $"Set {EnvironmentVariable} to a SQL Server connection string to run the integration tests.");

    public async ValueTask InitializeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        await ExecuteAsync(null, $"CREATE DATABASE [{LeftDatabase}]; CREATE DATABASE [{RightDatabase}];");
        try
        {
            await CreateObjectsAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task CreateObjectsAsync()
    {
        await ExecuteAsync(LeftDatabase, Common, Keys, LeftTypes, Left, "DELETE FROM dbo.TextKeys WHERE K = N'Z'; DELETE FROM dbo.VarcharKeys WHERE K = 'é'; DELETE FROM dbo.GuidKeys WHERE V = 7;");
        await ExecuteAsync(RightDatabase, Common, Keys, RightTypes, Right, "DELETE FROM dbo.TextKeys WHERE K = N'zz'; DELETE FROM dbo.VarcharKeys WHERE K = '€'; DELETE FROM dbo.BinKeys WHERE K = 0x7F80;");
    }

    public async ValueTask DisposeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await ExecuteAsync(null,
            $"ALTER DATABASE [{LeftDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{LeftDatabase}];",
            $"ALTER DATABASE [{RightDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{RightDatabase}];");
    }

    private IDatabaseSource Open(string database)
    {
        var profile = Provider.ParseConnectionString(ConnectionString!, out var password);
        profile.Database = database;
        return Provider.Open(profile, password);
    }

    private async Task ExecuteAsync(string? database, params string[] batches)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database ?? "master" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        foreach (var batch in batches)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }
}
