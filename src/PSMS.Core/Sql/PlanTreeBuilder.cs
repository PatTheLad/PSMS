using System.Text;
using PSMS.Core.Models;

namespace PSMS.Core.Sql;

public sealed record PlanTreeNode(
    int NodeId,
    int ParentId,
    int Depth,
    string Text,
    string? PhysicalOp,
    string? EstimateRows,
    string? ActualRows,
    string? Cost);

/// <summary>Builds an indented operator tree from SHOWPLAN_ALL / STATISTICS PROFILE grids.</summary>
public static class PlanTreeBuilder
{
    public static bool LooksLikePlan(ResultSet set)
    {
        var cols = set.Columns;
        return IndexOf(cols, "NodeId") >= 0
               && IndexOf(cols, "Parent") >= 0
               && (IndexOf(cols, "StmtText") >= 0 || IndexOf(cols, "PhysicalOp") >= 0);
    }

    public static IReadOnlyList<PlanTreeNode> Build(ResultSet set)
    {
        var nodeIdCol = IndexOf(set.Columns, "NodeId");
        var parentCol = IndexOf(set.Columns, "Parent");
        var stmtCol = IndexOf(set.Columns, "StmtText");
        var physCol = IndexOf(set.Columns, "PhysicalOp");
        var estCol = IndexOf(set.Columns, "EstimateRows");
        var rowsCol = IndexOf(set.Columns, "Rows");
        var costCol = IndexOf(set.Columns, "TotalSubtreeCost");

        if (nodeIdCol < 0 || parentCol < 0)
        {
            return [];
        }

        var display = set.DisplayRows.Count == set.Rows.Count ? set.DisplayRows : null;
        var nodes = new List<(int Id, int Parent, string Text, string? Phys, string? Est, string? Act, string? Cost)>();

        for (var r = 0; r < set.Rows.Count; r++)
        {
            var row = set.Rows[r];
            if (!TryInt(row, nodeIdCol, out var id))
            {
                continue;
            }

            TryInt(row, parentCol, out var parent);
            var text = stmtCol >= 0
                ? Cell(display, row, r, stmtCol)
                : physCol >= 0
                    ? Cell(display, row, r, physCol)
                    : $"Node {id}";
            nodes.Add((
                id,
                parent,
                text ?? "",
                physCol >= 0 ? Cell(display, row, r, physCol) : null,
                estCol >= 0 ? Cell(display, row, r, estCol) : null,
                rowsCol >= 0 ? Cell(display, row, r, rowsCol) : null,
                costCol >= 0 ? Cell(display, row, r, costCol) : null));
        }

        var byParent = nodes.ToLookup(n => n.Parent);
        var result = new List<PlanTreeNode>();
        void Walk(int parentId, int depth)
        {
            foreach (var n in byParent[parentId].OrderBy(x => x.Id))
            {
                result.Add(new PlanTreeNode(n.Id, n.Parent, depth, n.Text, n.Phys, n.Est, n.Act, n.Cost));
                Walk(n.Id, depth + 1);
            }
        }

        // Roots: parent 0 or parent not present as a node id
        var ids = nodes.Select(n => n.Id).ToHashSet();
        foreach (var root in nodes.Where(n => n.Parent == 0 || !ids.Contains(n.Parent)).OrderBy(n => n.Id))
        {
            if (result.Any(r => r.NodeId == root.Id))
            {
                continue;
            }

            result.Add(new PlanTreeNode(root.Id, root.Parent, 0, root.Text, root.Phys, root.Est, root.Act, root.Cost));
            Walk(root.Id, 1);
        }

        return result;
    }

    public static string FormatText(IReadOnlyList<PlanTreeNode> nodes)
    {
        var sb = new StringBuilder();
        foreach (var n in nodes)
        {
            sb.Append(' ', n.Depth * 2);
            if (!string.IsNullOrWhiteSpace(n.PhysicalOp))
            {
                sb.Append('[').Append(n.PhysicalOp).Append("] ");
            }

            sb.Append(n.Text.Trim());
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(n.ActualRows))
            {
                bits.Add($"rows={n.ActualRows}");
            }
            else if (!string.IsNullOrWhiteSpace(n.EstimateRows))
            {
                bits.Add($"est={n.EstimateRows}");
            }

            if (!string.IsNullOrWhiteSpace(n.Cost))
            {
                bits.Add($"cost={n.Cost}");
            }

            if (bits.Count > 0)
            {
                sb.Append("  (").Append(string.Join(", ", bits)).Append(')');
            }

            sb.AppendLine();
        }

        return sb.ToString();
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

    private static bool TryInt(IReadOnlyList<object?> row, int col, out int value)
    {
        value = 0;
        if (col < 0 || col >= row.Count || row[col] is null)
        {
            return false;
        }

        return int.TryParse(Convert.ToString(row[col]), out value);
    }

    private static string? Cell(IReadOnlyList<IReadOnlyList<string?>>? display, IReadOnlyList<object?> row, int r, int c)
    {
        if (display is not null && r < display.Count && c < display[r].Count)
        {
            return display[r][c];
        }

        return c < row.Count ? Convert.ToString(row[c]) : null;
    }
}
