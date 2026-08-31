using System.Globalization;
using System.Text;
using PSMS.Core.Models;

namespace PSMS.Core.Sql;

public static class UpdateScriptBuilder
{
    /// <summary>
    /// Builds UPDATE statements for edited cells. Keys map (rowIndex, columnIndex) → new display text
    /// (empty string means NULL).
    /// </summary>
    public static string Build(
        ResultSet set,
        EditableResultContext ctx,
        IReadOnlyDictionary<(int Row, int Col), string?> edits)
    {
        if (edits.Count == 0)
        {
            return "-- No cell edits to script.";
        }

        var keyIndexes = ctx.KeyColumns
            .Select(k => IndexOf(set.Columns, k))
            .Where(i => i >= 0)
            .ToList();

        var byRow = edits.GroupBy(e => e.Key.Row).OrderBy(g => g.Key);
        var sb = new StringBuilder();
        var nl = Environment.NewLine;
        var table = Qualify(ctx.Engine, ctx.Schema, ctx.Table);

        foreach (var group in byRow)
        {
            var rowIndex = group.Key;
            if (rowIndex < 0 || rowIndex >= set.Rows.Count)
            {
                continue;
            }

            var setParts = new List<string>();
            foreach (var edit in group.OrderBy(e => e.Key.Col))
            {
                var col = edit.Key.Col;
                if (col < 0 || col >= set.Columns.Count)
                {
                    continue;
                }

                if (keyIndexes.Contains(col))
                {
                    continue; // don't SET primary key columns in MVP
                }

                var colName = QuoteIdent(ctx.Engine, set.Columns[col]);
                setParts.Add($"{colName} = {Literal(ctx.Engine, edit.Value)}");
            }

            if (setParts.Count == 0)
            {
                continue;
            }

            var whereParts = new List<string>();
            if (keyIndexes.Count > 0)
            {
                foreach (var ki in keyIndexes)
                {
                    var colName = QuoteIdent(ctx.Engine, set.Columns[ki]);
                    var original = GetOriginalDisplay(set, rowIndex, ki);
                    whereParts.Add(original is null
                        ? $"{colName} IS NULL"
                        : $"{colName} = {Literal(ctx.Engine, original)}");
                }
            }
            else
            {
                // Fallback: match on all original column values
                for (var c = 0; c < set.Columns.Count; c++)
                {
                    var colName = QuoteIdent(ctx.Engine, set.Columns[c]);
                    var original = GetOriginalDisplay(set, rowIndex, c);
                    whereParts.Add(original is null
                        ? $"{colName} IS NULL"
                        : $"{colName} = {Literal(ctx.Engine, original)}");
                }
            }

            sb.Append("UPDATE ").Append(table).Append(nl);
            sb.Append("SET ").Append(string.Join($",{nl}    ", setParts)).Append(nl);
            sb.Append("WHERE ").Append(string.Join(" AND ", whereParts)).Append(';').Append(nl).Append(nl);
        }

        return sb.Length == 0 ? "-- No updatable cell edits (key columns only?)." : sb.ToString().TrimEnd() + nl;
    }

    private static string? GetOriginalDisplay(ResultSet set, int row, int col)
    {
        if (set.DisplayRows.Count > row && set.DisplayRows[row].Count > col)
        {
            return set.DisplayRows[row][col];
        }

        if (set.Rows.Count > row && set.Rows[row].Count > col)
        {
            var v = set.Rows[row][col];
            return v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Qualify(DbEngine engine, string schema, string name) =>
        engine == DbEngine.SqlServer
            ? $"[{schema}].[{name}]"
            : string.IsNullOrWhiteSpace(schema) || schema is "main" or "dbo"
                ? QuoteIdent(engine, name)
                : $"{QuoteIdent(engine, schema)}.{QuoteIdent(engine, name)}";

    private static string QuoteIdent(DbEngine engine, string ident) =>
        engine == DbEngine.SqlServer ? $"[{ident}]" : $"\"{ident.Replace("\"", "\"\"")}\"";

    private static string Literal(DbEngine engine, string? display)
    {
        if (display is null)
        {
            return "NULL";
        }

        if (long.TryParse(display, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || decimal.TryParse(display, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
            || bool.TryParse(display, out _))
        {
            return display;
        }

        var escaped = display.Replace("'", "''");
        return engine == DbEngine.SqlServer ? $"N'{escaped}'" : $"'{escaped}'";
    }
}
