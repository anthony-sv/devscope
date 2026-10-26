using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Devscope.SqlServer;

/// <summary>
/// Turns the JDBC URL of a Java data source (e.g. a WebSphere connection pool) into a SqlClient connection string:
/// Microsoft's driver (<c>jdbc:sqlserver://host\instance:1433;databaseName=Billing;user=app</c>) and jTDS
/// (<c>jdbc:jtds:sqlserver://host:1433/Billing;user=app</c>).
/// </summary>
internal static class JdbcUrl
{
    private const string MicrosoftPrefix = "jdbc:sqlserver://";
    private const string JtdsPrefix = "jdbc:jtds:sqlserver://";

    public static bool IsJdbcUrl(string text) =>
        text.StartsWith(MicrosoftPrefix, StringComparison.OrdinalIgnoreCase) || text.StartsWith(JtdsPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Converts a JDBC URL; throws <see cref="ArgumentException"/> when it has no server.</summary>
    public static string ToConnectionString(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var jtds = url.StartsWith(JtdsPrefix, StringComparison.OrdinalIgnoreCase);
        var rest = url[(jtds ? JtdsPrefix : MicrosoftPrefix).Length..];

        var separator = rest.IndexOf(';', StringComparison.Ordinal);
        var location = separator < 0 ? rest : rest[..separator];
        var properties = ParseProperties(separator < 0 ? string.Empty : rest[(separator + 1)..]);

        // host[\instance][:port] for Microsoft's driver; host[:port][/database] for jTDS.
        string? database = null;
        if (jtds && location.IndexOf('/', StringComparison.Ordinal) is var slash and >= 0)
        {
            database = location[(slash + 1)..];
            location = location[..slash];
        }

        string? port = null;
        if (location.LastIndexOf(':') is var colon and >= 0)
        {
            port = location[(colon + 1)..];
            location = location[..colon];
        }

        string? instance = null;
        if (location.IndexOf('\\', StringComparison.Ordinal) is var backslash and >= 0)
        {
            instance = location[(backslash + 1)..];
            location = location[..backslash];
        }

        var host = Get(properties, "serverName") ?? location;
        instance = Get(properties, "instanceName", "instance") ?? instance;
        port = Get(properties, "portNumber", "port") ?? port;
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("The JDBC URL has no server.", nameof(url));
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = host + (string.IsNullOrEmpty(instance) ? string.Empty : "\\" + instance) + (string.IsNullOrEmpty(port) ? string.Empty : "," + port),
        };
        if ((Get(properties, "databaseName", "database") ?? database) is { Length: > 0 } db)
        {
            builder.InitialCatalog = db;
        }

        if (Get(properties, "user", "userName") is { Length: > 0 } user)
        {
            builder.UserID = user;
            builder.Password = Get(properties, "password") ?? string.Empty;
        }
        else
        {
            // integratedSecurity=true, or no login in the URL because the data source keeps it elsewhere (e.g. a
            // J2C authentication alias): start from Windows authentication; the user can switch to a SQL login.
            builder.IntegratedSecurity = true;
        }

        if (Get(properties, "encrypt") is { } encrypt)
        {
            builder.Encrypt = IsTrue(encrypt) || encrypt.Equals("strict", StringComparison.OrdinalIgnoreCase)
                ? SqlConnectionEncryptOption.Mandatory
                : SqlConnectionEncryptOption.Optional;
        }

        if (IsTrue(Get(properties, "trustServerCertificate")))
        {
            builder.TrustServerCertificate = true;
        }

        if (string.Equals(Get(properties, "applicationIntent"), "ReadOnly", StringComparison.OrdinalIgnoreCase))
        {
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        }

        if (int.TryParse(Get(properties, "loginTimeout"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            builder.ConnectTimeout = seconds;
        }

        return builder.ConnectionString;
    }

    /// <summary>name=value pairs separated by ';'. A value in braces may contain ';' (Microsoft's driver).</summary>
    private static Dictionary<string, string> ParseProperties(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            var equals = text.IndexOf('=', i);
            if (equals < 0)
            {
                break;
            }

            var name = text[i..equals].Trim();
            string value;
            if (equals + 1 < text.Length && text[equals + 1] == '{' && text.IndexOf('}', equals + 2) is var close and > 0)
            {
                value = text[(equals + 2)..close];
                i = text.IndexOf(';', close) is var next and >= 0 ? next + 1 : text.Length;
            }
            else
            {
                var end = text.IndexOf(';', equals + 1);
                value = (end < 0 ? text[(equals + 1)..] : text[(equals + 1)..end]).Trim();
                i = end < 0 ? text.Length : end + 1;
            }

            if (name.Length > 0)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static string? Get(Dictionary<string, string> properties, params string[] names)
    {
        foreach (var name in names)
        {
            if (properties.TryGetValue(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
