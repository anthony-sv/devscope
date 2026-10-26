using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Devscope.Core.Database;

/// <summary>How a connection logs in.</summary>
public enum DbAuthentication
{
    /// <summary>The Windows account running Devscope (integrated security).</summary>
    Windows,

    /// <summary>A SQL Server login with a password.</summary>
    SqlLogin,
}

/// <summary>A saved database connection, e.g. "Billing PROD". Persisted locally as JSON; passwords only encrypted.</summary>
public sealed class DbConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New connection";

    /// <summary>Server, instance and port as SQL Server tools accept them: <c>sql01</c>, <c>sql01\PROD</c>, <c>sql01,1433</c>.</summary>
    public string Server { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public DbAuthentication Authentication { get; set; } = DbAuthentication.Windows;

    public string UserName { get; set; } = string.Empty;

    /// <summary>The password encrypted by <see cref="ISecretProtector"/>; empty when it is not saved.</summary>
    public string ProtectedPassword { get; set; } = string.Empty;

    /// <summary>Encrypt the connection (TLS). Required by default in current SQL Server drivers.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>Accept the server's certificate without validating it, e.g. a self-signed one on a test server.</summary>
    public bool TrustServerCertificate { get; set; }

    /// <summary>Connect with ApplicationIntent=ReadOnly, which routes to a readable secondary in an availability group.</summary>
    public bool ReadOnlyIntent { get; set; } = true;

    /// <summary>Seconds a schema query may take. Row comparisons are not limited (they can be cancelled).</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>More connection string keywords, e.g. <c>MultiSubnetFailover=True</c>.</summary>
    public string AdditionalOptions { get; set; } = string.Empty;

    /// <summary>Server/database, for lists and messages.</summary>
    [JsonIgnore]
    public string Target => string.IsNullOrWhiteSpace(Database) ? Server : $"{Server}/{Database}";

    public DbConnectionProfile Clone() => (DbConnectionProfile)MemberwiseClone();
}

/// <summary>Encrypts saved passwords; on Windows with DPAPI, so only the same Windows account can read them.</summary>
public interface ISecretProtector
{
    string Protect(string secret);

    /// <summary>Returns null when the secret cannot be decrypted (e.g. another account or machine saved it).</summary>
    string? Unprotect(string protectedSecret);
}

/// <summary>Loads and saves connection profiles.</summary>
public interface IConnectionStore
{
    Task<IReadOnlyList<DbConnectionProfile>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(IReadOnlyList<DbConnectionProfile> connections, CancellationToken cancellationToken);
}

/// <summary>Stores connection profiles in a JSON file (by default under <c>%APPDATA%\Devscope</c>).</summary>
public sealed partial class JsonConnectionStore(string filePath, ILogger<JsonConnectionStore> logger) : IConnectionStore
{
    /// <summary>Default location of the connections file.</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Devscope", "connections.json");

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbConnectionProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var document = await JsonSerializer.DeserializeAsync(stream, ConnectionJsonContext.Default.ConnectionDocument, cancellationToken)
                .ConfigureAwait(false);
            return document?.Connections ?? [];
        }
        catch (JsonException ex)
        {
            // Keep the broken file for the user to inspect instead of silently overwriting it later.
            var backup = filePath + ".corrupt";
            File.Copy(filePath, backup, overwrite: true);
            LogCorrupt(ex, filePath, backup);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyList<DbConnectionProfile> connections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Write to a temp file then swap, so a crash mid-write never loses the existing connections.
        var temp = filePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            var document = new ConnectionDocument { Connections = [.. connections] };
            await JsonSerializer.SerializeAsync(stream, document, ConnectionJsonContext.Default.ConnectionDocument, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temp, filePath, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Connections file {Path} is corrupt; a copy was kept at {Backup}")]
    private partial void LogCorrupt(Exception exception, string path, string backup);
}

/// <summary>On-disk shape; versioned so the format can evolve.</summary>
internal sealed class ConnectionDocument
{
    public int Version { get; set; } = 1;

    public List<DbConnectionProfile> Connections { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConnectionDocument))]
internal sealed partial class ConnectionJsonContext : JsonSerializerContext;
