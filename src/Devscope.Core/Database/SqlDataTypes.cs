using System.Globalization;

namespace Devscope.Core.Database;

/// <summary>SQL Server data types: how they are written in T-SQL and what kind of values they hold.</summary>
public static class SqlDataTypes
{
    /// <summary>Formats a system type as T-SQL, e.g. <c>nvarchar(50)</c>, <c>decimal(18, 2)</c>, <c>varbinary(max)</c>.</summary>
    /// <param name="typeName">Name from sys.types.</param>
    /// <param name="maxLength">sys.columns.max_length: bytes, or -1 for (max).</param>
    /// <param name="precision">sys.columns.precision.</param>
    /// <param name="scale">sys.columns.scale.</param>
    public static string Format(string typeName, int maxLength, int precision, int scale)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var length = maxLength == -1 ? "max" : null;
        return typeName.ToLowerInvariant() switch
        {
            "varchar" or "char" or "varbinary" or "binary" =>
                string.Create(CultureInfo.InvariantCulture, $"{typeName}({length ?? maxLength.ToString(CultureInfo.InvariantCulture)})"),
            "nvarchar" or "nchar" =>
                string.Create(CultureInfo.InvariantCulture, $"{typeName}({length ?? (maxLength / 2).ToString(CultureInfo.InvariantCulture)})"),
            "decimal" or "numeric" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({precision}, {scale})"),
            "datetime2" or "time" or "datetimeoffset" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({scale})"),
            _ => typeName,
        };
    }

    /// <summary>The kind of values a system type holds.</summary>
    /// <param name="systemTypeName">The system type, or for an alias type its base type (nvarchar for a Phone type).</param>
    public static DbValueKind KindOf(string? systemTypeName) => systemTypeName?.ToLowerInvariant() switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "sysname" => DbValueKind.Text,
        "bigint" or "int" or "smallint" or "tinyint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => DbValueKind.Number,
        "bit" => DbValueKind.Boolean,
        "date" or "datetime" or "datetime2" or "smalldatetime" => DbValueKind.DateTime,
        "time" => DbValueKind.Time,
        "datetimeoffset" => DbValueKind.DateTimeOffset,
        "uniqueidentifier" => DbValueKind.UniqueIdentifier,
        "binary" or "varbinary" => DbValueKind.Binary,
        "timestamp" or "rowversion" => DbValueKind.RowVersion,
        "hierarchyid" or "geometry" or "geography" => DbValueKind.Clr,
        _ => DbValueKind.Other,
    };

    /// <summary>Whether rows can be ordered by a column of this kind the same way on the server and here.</summary>
    public static bool IsOrderable(DbValueKind kind) => kind is DbValueKind.Text or DbValueKind.Number or DbValueKind.Boolean
        or DbValueKind.DateTime or DbValueKind.Time or DbValueKind.DateTimeOffset or DbValueKind.UniqueIdentifier or DbValueKind.Binary;
}
