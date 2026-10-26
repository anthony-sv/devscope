using System.Globalization;
using System.Text;

namespace Devscope.Core.Database;

/// <summary>
/// Writes the INSERT, UPDATE and DELETE statements that would make one side's rows match the other's, for the
/// differing rows a comparison kept. Meant to be read and run by hand; nothing here touches a database.
/// </summary>
public static class SyncScripter
{
    private const string NewLine = "\r\n";

    /// <summary>Scripts the changes to the right table (<paramref name="toRight"/>) or the left one.</summary>
    public static string Script(RowComparison comparison, bool toRight)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var plan = comparison.Plan;
        var target = toRight ? plan.RightQuery : plan.LeftQuery;
        var table = DbNames.Quote(target.Schema, target.Table);
        var columns = target.Columns;
        var keys = plan.KeyColumns;

        // Rows the target lacks are inserted, rows only it has are deleted, changed rows take the source's values.
        var inserts = comparison.Samples.Where(s => s.Kind == (toRight ? RowDiffKind.OnlyInLeft : RowDiffKind.OnlyInRight)).ToList();
        var deletes = comparison.Samples.Where(s => s.Kind == (toRight ? RowDiffKind.OnlyInRight : RowDiffKind.OnlyInLeft)).ToList();
        var updates = comparison.Samples.Where(s => s.Kind == RowDiffKind.Different).ToList();

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"-- Makes {table} on the {(toRight ? "right" : "left")} side match the {(toRight ? "left" : "right")} side.").Append(NewLine);
        sb.Append(CultureInfo.InvariantCulture, $"-- {inserts.Count:N0} INSERT, {updates.Count:N0} UPDATE, {deletes.Count:N0} DELETE");
        sb.Append(comparison.SamplesAreComplete
            ? ": every difference found."
            : string.Create(CultureInfo.InvariantCulture, $": only the {comparison.Samples.Count:N0} example rows of {comparison.DifferenceCount:N0} differences (raise \"Example rows\" in Settings, or add a filter)."));
        sb.Append(NewLine);
        foreach (var note in comparison.Notes.Where(n => n.StartsWith("Not compared", StringComparison.Ordinal) || n.StartsWith("Ignored", StringComparison.Ordinal)))
        {
            sb.Append("-- ").Append(note).Append(" (not set by this script)").Append(NewLine);
        }

        sb.Append("-- Review before running. Wrap in BEGIN TRANSACTION ... COMMIT to apply it all or nothing.").Append(NewLine);

        var writable = Enumerable.Range(0, columns.Count).Where(i => !columns[i].IsComputed).ToList();
        if (inserts.Count > 0)
        {
            var identity = writable.Any(i => columns[i].Identity is not null);
            sb.Append(NewLine);
            if (identity)
            {
                sb.Append("SET IDENTITY_INSERT ").Append(table).Append(" ON;").Append(NewLine);
            }

            var columnList = string.Join(", ", writable.Select(i => DbNames.Quote(columns[i].Name)));
            foreach (var row in inserts.Select(s => (toRight ? s.Left : s.Right)!))
            {
                sb.Append("INSERT INTO ").Append(table).Append(" (").Append(columnList).Append(") VALUES (")
                    .Append(string.Join(", ", writable.Select(i => DbValues.ToSqlLiteral(row[i], BaseType(columns[i]))))).Append(");").Append(NewLine);
            }

            if (identity)
            {
                sb.Append("SET IDENTITY_INSERT ").Append(table).Append(" OFF;").Append(NewLine);
            }
        }

        if (updates.Count > 0)
        {
            sb.Append(NewLine);
            foreach (var update in updates)
            {
                var source = (toRight ? update.Left : update.Right)!;
                var current = (toRight ? update.Right : update.Left)!;
                var settable = update.ChangedColumns.Where(i => !columns[i].IsComputed && columns[i].Identity is null).ToList();
                if (settable.Count == 0)
                {
                    sb.Append("-- ").Append(Where(columns, keys, current)).Append(": only computed or identity columns differ").Append(NewLine);
                    continue;
                }

                sb.Append("UPDATE ").Append(table).Append(" SET ")
                    .Append(string.Join(", ", settable.Select(i => $"{DbNames.Quote(columns[i].Name)} = {DbValues.ToSqlLiteral(source[i], BaseType(columns[i]))}")))
                    .Append(" WHERE ").Append(Where(columns, keys, current)).Append(';').Append(NewLine);
            }
        }

        if (deletes.Count > 0)
        {
            sb.Append(NewLine);
            foreach (var row in deletes.Select(s => (toRight ? s.Right : s.Left)!))
            {
                // Without a key, a whole-row match: TOP (1) removes one of several identical rows.
                sb.Append(keys.Count == 0 ? "DELETE TOP (1) FROM " : "DELETE FROM ").Append(table)
                    .Append(" WHERE ").Append(Where(columns, keys, row)).Append(';').Append(NewLine);
            }
        }

        return sb.ToString();
    }

    /// <summary>Identifies a row by its key, or by every column that can be compared with = when there is none.</summary>
    private static string Where(IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<int> keys, IReadOnlyList<object?> row)
    {
        var identifying = keys.Count > 0
            ? keys
            : Enumerable.Range(0, columns.Count).Where(i => columns[i].Kind is not (DbValueKind.Other or DbValueKind.Clr)).ToList();
        return string.Join(" AND ", identifying.Select(i => row[i] is null
            ? $"{DbNames.Quote(columns[i].Name)} IS NULL"
            : $"{DbNames.Quote(columns[i].Name)} = {DbValues.ToSqlLiteral(row[i], BaseType(columns[i]))}"));
    }

    /// <summary>datetime2(7) → datetime2, so the literal gets the right precision.</summary>
    private static string BaseType(ColumnDefinition column)
    {
        var paren = column.DataType.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? column.DataType : column.DataType[..paren];
    }
}
