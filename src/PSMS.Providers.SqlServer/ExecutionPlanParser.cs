using System.Globalization;
using System.Xml.Linq;
using PSMS.Core.Models;

namespace PSMS.Providers.SqlServer;

internal static class ExecutionPlanParser
{
    private static readonly XNamespace SqlPlan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static IReadOnlyList<PlanNodeSummary> Summarize(string? planXml)
    {
        if (string.IsNullOrWhiteSpace(planXml))
        {
            return [];
        }

        try
        {
            var doc = XDocument.Parse(planXml);
            var nodes = new List<PlanNodeSummary>();
            foreach (var rel in doc.Descendants(SqlPlan + "RelOp"))
            {
                var nodeId = (int?)rel.Attribute("NodeId") ?? nodes.Count;
                var physical = (string?)rel.Attribute("PhysicalOp") ?? string.Empty;
                var logical = (string?)rel.Attribute("LogicalOp") ?? string.Empty;
                var rows = ParseDouble(rel.Attribute("EstimateRows")?.Value);
                var cost = ParseDouble(rel.Attribute("EstimatedTotalSubtreeCost")?.Value);
                var obj = rel.Descendants(SqlPlan + "Object").FirstOrDefault();
                var objectName = obj is null
                    ? null
                    : string.Join('.', new[]
                    {
                        (string?)obj.Attribute("Database"),
                        (string?)obj.Attribute("Schema"),
                        (string?)obj.Attribute("Table"),
                        (string?)obj.Attribute("Index")
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));

                nodes.Add(new PlanNodeSummary
                {
                    NodeId = nodeId,
                    PhysicalOp = physical,
                    LogicalOp = logical,
                    EstimatedRows = rows,
                    EstimatedCost = cost,
                    ObjectName = objectName
                });
            }

            return nodes
                .OrderByDescending(n => n.EstimatedCost)
                .Take(40)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static double ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
