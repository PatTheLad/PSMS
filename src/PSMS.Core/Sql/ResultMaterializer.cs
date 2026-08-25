using System.Data.Common;
using System.Globalization;
using System.Text;
using PSMS.Core.Models;

namespace PSMS.Core.Sql;

/// <summary>
/// Reads and formats result cells with size caps so large LOBs/binaries don't freeze the UI.
/// </summary>
public static class ResultMaterializer
{
    public const int MaxBinaryPreviewBytes = 64;
    public const int MaxStringPreviewChars = 8_192;

    public static object? ReadCell(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var fieldType = reader.GetFieldType(ordinal);

        if (fieldType == typeof(byte[]))
        {
            var bytes = (byte[])reader.GetValue(ordinal);
            if (bytes.Length > MaxBinaryPreviewBytes)
            {
                return new TruncatedBinary(bytes.AsSpan(0, MaxBinaryPreviewBytes).ToArray(), bytes.Length);
            }

            return bytes;
        }

        if (fieldType == typeof(string))
        {
            var text = reader.GetString(ordinal);
            if (text.Length > MaxStringPreviewChars)
            {
                return text[..MaxStringPreviewChars] + $"… ({text.Length:N0} chars)";
            }

            return text;
        }

        return reader.GetValue(ordinal);
    }

    public static string?[] FormatRow(IReadOnlyList<object?> row)
    {
        var display = new string?[row.Count];
        for (var i = 0; i < row.Count; i++)
        {
            display[i] = FormatCell(row[i]);
        }

        return display;
    }

    public static string? FormatCell(object? cell) =>
        cell switch
        {
            null => null,
            TruncatedBinary tb => $"0x{Convert.ToHexString(tb.Preview)}… ({tb.TotalLength:N0} bytes)",
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            byte[] bytes when bytes.Length > MaxBinaryPreviewBytes
                => $"0x{Convert.ToHexString(bytes.AsSpan(0, MaxBinaryPreviewBytes))}… ({bytes.Length:N0} bytes)",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            bool b => b ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => cell.ToString()
        };

    public static string EscapeCsv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    public static string BuildCsv(ResultSet set)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', set.Columns.Select(EscapeCsv)));
        IEnumerable<IReadOnlyList<string?>> rows = set.DisplayRows.Count > 0
            ? set.DisplayRows
            : set.Rows.Select(FormatRow);
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(',', row.Select(c => EscapeCsv(c ?? string.Empty))));
        }

        return sb.ToString();
    }
}

/// <summary>Binary value truncated at read time for display/export safety.</summary>
public sealed class TruncatedBinary(byte[] preview, int totalLength)
{
    public byte[] Preview { get; } = preview;
    public int TotalLength { get; } = totalLength;
}
