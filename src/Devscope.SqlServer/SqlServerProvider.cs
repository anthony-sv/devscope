using Devscope.Core.Database;
using Microsoft.Data.SqlClient;

namespace Devscope.SqlServer;

/// <summary>Opens saved connections against SQL Server (and Azure SQL) with Microsoft.Data.SqlClient.</summary>
public sealed class SqlServerProvider : IDatabaseProvider
{
    // Keywords a profile sets itself; everything else in a pasted connection string goes to AdditionalOptions.
    private static readonly string[] ProfileKeywords =
    [
        "Data Source", "Initial Catalog", "Integrated Security", "User ID", "Password", "Encrypt",
        "Trust Server Certificate", "Application Intent", "Command Timeout", "Application Name",
    ];

    /// <inheritdoc />
    public IDatabaseSource Open(DbConnectionProfile profile, string? password)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new SqlServerSource(BuildConnectionString(profile, password), Math.Max(1, profile.CommandTimeoutSeconds));
    }

    /// <inheritdoc />
    /// <remarks>Also accepts the JDBC URL of a Java data source (Microsoft's driver or jTDS).</remarks>
    public DbConnectionProfile ParseConnectionString(string connectionString, out string? password)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        if (JdbcUrl.IsJdbcUrl(connectionString.Trim()))
        {
            connectionString = JdbcUrl.ToConnectionString(connectionString.Trim());
        }

        SqlConnectionStringBuilder builder;
        SqlConnectionStringBuilder extra;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
            extra = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ArgumentException($"This is not a SQL Server connection string: {ex.Message}", nameof(connectionString), ex);
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new ArgumentException("The connection string has no server (Server= or Data Source=).", nameof(connectionString));
        }

        foreach (var keyword in ProfileKeywords)
        {
            extra.Remove(keyword);
        }

        password = builder.Password.Length > 0 ? builder.Password : null;
        return new DbConnectionProfile
        {
            Name = builder.InitialCatalog.Length > 0 ? $"{builder.InitialCatalog} ({builder.DataSource})" : builder.DataSource,
            Server = builder.DataSource,
            Database = builder.InitialCatalog,
            Authentication = builder.IntegratedSecurity || builder.UserID.Length == 0 ? DbAuthentication.Windows : DbAuthentication.SqlLogin,
            UserName = builder.UserID,
            Encrypt = builder.Encrypt != SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = builder.TrustServerCertificate,
            CommandTimeoutSeconds = builder.ShouldSerialize("Command Timeout") ? builder.CommandTimeout : new DbConnectionProfile().CommandTimeoutSeconds,
            AdditionalOptions = extra.ConnectionString,
        };
    }

    /// <summary>The connection string for a profile: its own settings over any additional options.</summary>
    internal static string BuildConnectionString(DbConnectionProfile profile, string? password)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(profile.AdditionalOptions ?? string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ArgumentException($"The additional options of '{profile.Name}' are not valid: {ex.Message}", nameof(profile), ex);
        }

        builder.DataSource = profile.Server.Trim();
        builder.InitialCatalog = profile.Database.Trim();

        // An explicit Authentication= option (e.g. Microsoft Entra) replaces Windows authentication.
        var external = builder.Authentication != SqlAuthenticationMethod.NotSpecified;
        if (profile.Authentication == DbAuthentication.SqlLogin)
        {
            builder.IntegratedSecurity = false;
            builder.UserID = profile.UserName;
            builder.Password = password ?? string.Empty;
        }
        else if (!external)
        {
            builder.IntegratedSecurity = true;
        }

        builder.Encrypt = profile.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional;
        builder.TrustServerCertificate = profile.TrustServerCertificate;
        if (profile.ReadOnlyIntent)
        {
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        }

        builder.ApplicationName = "Devscope";
        return builder.ConnectionString;
    }
}
