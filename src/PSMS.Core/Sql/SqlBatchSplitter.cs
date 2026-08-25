using System.Text.RegularExpressions;

namespace PSMS.Core.Sql;

public static partial class SqlBatchSplitter
{
    /// <summary>Splits a script on lines that contain only GO (optional trailing comment), SSMS-style.</summary>
    public static IReadOnlyList<string> Split(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return [];
        }

        var batches = new List<string>();
        var current = new List<string>();

        foreach (var line in script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (GoLineRegex().IsMatch(line))
            {
                var batch = string.Join('\n', current).Trim();
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    batches.Add(batch);
                }

                current.Clear();
            }
            else
            {
                current.Add(line);
            }
        }

        var last = string.Join('\n', current).Trim();
        if (!string.IsNullOrWhiteSpace(last))
        {
            batches.Add(last);
        }

        return batches.Count > 0 ? batches : [script];
    }

    [GeneratedRegex(@"^\s*GO\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoLineRegex();
}
