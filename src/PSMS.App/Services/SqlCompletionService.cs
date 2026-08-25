using System.Text.RegularExpressions;
using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.JSInterop;
using PSMS.Core.Models;
using Range = BlazorMonaco.Range;

namespace PSMS.App.Services;

/// <summary>
/// Monaco completion provider. Uses only an already-cached catalog snapshot —
/// never loads from SQL during typing (that crashed Photino WebView).
/// </summary>
public sealed class SqlCompletionService
{
    private static readonly string[] Keywords =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "ON", "AND", "OR", "NOT",
        "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "ALTER", "DROP", "TABLE", "VIEW",
        "PROCEDURE", "FUNCTION", "INDEX", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "NULL",
        "ORDER", "BY", "GROUP", "HAVING", "AS", "DISTINCT", "TOP", "UNION", "ALL", "EXISTS", "IN", "BETWEEN",
        "LIKE", "IS", "CASE", "WHEN", "THEN", "ELSE", "END", "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION",
        "WITH", "OVER", "PARTITION", "COUNT", "SUM", "AVG", "MIN", "MAX", "CAST", "CONVERT",
        "DECLARE", "EXEC", "EXECUTE", "RETURN", "IF", "WHILE", "GO", "USE", "SCHEMA", "DATABASE", "TRUNCATE"
    ];

    private readonly ActiveSessionService _sessions;
    private readonly SchemaIntelliSenseService _intelliSense;
    private IJSRuntime? _js;
    private bool _registered;

    public SqlCompletionService(ActiveSessionService sessions, SchemaIntelliSenseService intelliSense)
    {
        _sessions = sessions;
        _intelliSense = intelliSense;
    }

    public async Task EnsureRegisteredAsync(IJSRuntime js)
    {
        if (_registered)
        {
            return;
        }

        _js = js;
        _registered = true;

        try
        {
            await BlazorMonaco.Languages.Global.RegisterCompletionItemProvider(
                js,
                "sql",
                new CompletionItemProvider(
                    ["."],
                    ProvideAsync,
                    null));
        }
        catch
        {
            _registered = false;
            throw;
        }
    }

    private async Task<CompletionList> ProvideAsync(string modelUri, Position position, CompletionContext context)
    {
        try
        {
            return await BuildSuggestionsAsync(modelUri, position).ConfigureAwait(false);
        }
        catch
        {
            return new CompletionList { Suggestions = [] };
        }
    }

    private async Task<CompletionList> BuildSuggestionsAsync(string modelUri, Position position)
    {
        var suggestions = new List<CompletionItem>();

        // Only use cached snapshot — never hit SQL from the completion callback.
        var tab = _sessions.ActiveTab;
        var snapshot = _intelliSense.Current;
        if (tab is null
            || snapshot is null
            || snapshot.ConnectionId != tab.ConnectionId
            || !string.Equals(snapshot.Database, tab.Database, StringComparison.OrdinalIgnoreCase)
            || snapshot.Status.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            snapshot = null;
        }

        if (_js is null)
        {
            return new CompletionList { Suggestions = suggestions };
        }

        TextModel? model;
        try
        {
            model = await BlazorMonaco.Editor.Global.GetModel(_js, modelUri).ConfigureAwait(false);
        }
        catch
        {
            return new CompletionList { Suggestions = suggestions };
        }

        if (model is null)
        {
            return new CompletionList { Suggestions = suggestions };
        }

        string line;
        WordAtPosition? word;
        try
        {
            line = await model.GetLineContent(position.LineNumber).ConfigureAwait(false) ?? string.Empty;
            word = await model.GetWordUntilPosition(position).ConfigureAwait(false);
        }
        catch
        {
            return new CompletionList { Suggestions = suggestions };
        }

        var col = Math.Clamp(position.Column - 1, 0, line.Length);
        var prefix = line[..col];
        var filter = word?.Word ?? string.Empty;

        var replaceRange = new Range(
            position.LineNumber,
            Math.Max(1, word?.StartColumn ?? position.Column),
            position.LineNumber,
            Math.Max(1, word?.EndColumn ?? position.Column));

        var dotted = ParseDottedContext(prefix);

        if (dotted.AfterDot)
        {
            if (dotted.QualifierParts.Count == 1)
            {
                var qualifier = dotted.QualifierParts[0];
                AddSchemaObjects(suggestions, snapshot, replaceRange, filter, qualifier);
                AddColumnsForTable(suggestions, snapshot, replaceRange, filter, null, qualifier);
                AddColumnsForTable(suggestions, snapshot, replaceRange, filter, "dbo", qualifier);
            }
            else if (dotted.QualifierParts.Count >= 2)
            {
                AddColumnsForTable(
                    suggestions,
                    snapshot,
                    replaceRange,
                    filter,
                    dotted.QualifierParts[^2],
                    dotted.QualifierParts[^1]);
            }
        }
        else
        {
            AddKeywords(suggestions, replaceRange, filter);
            AddTopLevelObjects(suggestions, snapshot, replaceRange, filter);
        }

        return new CompletionList
        {
            Suggestions = suggestions
                .GroupBy(s => s.LabelAsString ?? s.InsertText ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(80)
                .ToList()
        };
    }

    private static void AddKeywords(List<CompletionItem> suggestions, Range range, string filter)
    {
        foreach (var keyword in Keywords)
        {
            if (!Matches(keyword, filter))
            {
                continue;
            }

            suggestions.Add(Item(keyword, keyword, CompletionItemKind.Keyword, "keyword", range, "0" + keyword));
        }
    }

    private static void AddTopLevelObjects(List<CompletionItem> suggestions, IntelliSenseSnapshot? snapshot, Range range, string filter)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(filter) || filter.Length < 1)
        {
            // Avoid dumping hundreds of objects on every keystroke with empty filter.
            if (snapshot is null || string.IsNullOrWhiteSpace(filter))
            {
                return;
            }
        }

        foreach (var obj in snapshot.Objects)
        {
            if (obj.Kind is CatalogObjectKind.Column or CatalogObjectKind.Schema)
            {
                continue;
            }

            if (!Matches(obj.Name, filter) && !Matches(obj.Schema + "." + obj.Name, filter))
            {
                continue;
            }

            var insert = $"{Quote(obj.Schema)}.{Quote(obj.Name)}";
            suggestions.Add(Item(obj.Name, insert, MapKind(obj.Kind), $"{obj.Kind} · {obj.Schema}", range, "2" + obj.Name));
        }

        foreach (var obj in snapshot.Objects.Where(o => o.Kind == CatalogObjectKind.Schema))
        {
            if (!Matches(obj.Name, filter))
            {
                continue;
            }

            suggestions.Add(Item(obj.Name, Quote(obj.Name), CompletionItemKind.Module, "Schema", range, "1" + obj.Name));
        }
    }

    private static void AddSchemaObjects(
        List<CompletionItem> suggestions,
        IntelliSenseSnapshot? snapshot,
        Range range,
        string filter,
        string schema)
    {
        if (snapshot is null)
        {
            return;
        }

        foreach (var obj in snapshot.Objects.Where(o =>
                     o.Kind != CatalogObjectKind.Schema
                     && string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)))
        {
            if (!Matches(obj.Name, filter))
            {
                continue;
            }

            suggestions.Add(Item(obj.Name, Quote(obj.Name), MapKind(obj.Kind), $"{obj.Kind}", range, "2" + obj.Name));
        }
    }

    private static void AddColumnsForTable(
        List<CompletionItem> suggestions,
        IntelliSenseSnapshot? snapshot,
        Range range,
        string filter,
        string? schema,
        string table)
    {
        if (snapshot is null)
        {
            return;
        }

        IEnumerable<KeyValuePair<string, IReadOnlyList<ColumnInfo>>> matches;
        if (schema is null)
        {
            matches = snapshot.ColumnsByTable.Where(kv =>
                string.Equals(kv.Key.Split('.').LastOrDefault(), table, StringComparison.OrdinalIgnoreCase));
        }
        else if (snapshot.ColumnsByTable.TryGetValue($"{schema}.{table}", out var cols))
        {
            matches = [new KeyValuePair<string, IReadOnlyList<ColumnInfo>>($"{schema}.{table}", cols)];
        }
        else
        {
            matches = [];
        }

        foreach (var pair in matches)
        {
            foreach (var col in pair.Value)
            {
                if (!Matches(col.Name, filter))
                {
                    continue;
                }

                suggestions.Add(Item(col.Name, Quote(col.Name), CompletionItemKind.Field, $"Column · {col.DataType}", range, "3" + col.Name));
            }
        }
    }

    private static CompletionItem Item(
        string label,
        string insert,
        CompletionItemKind kind,
        string detail,
        Range range,
        string sort)
        => new()
        {
            LabelAsString = label,
            InsertText = insert,
            Kind = kind,
            Detail = detail,
            RangeAsObject = range,
            FilterText = label,
            SortText = sort
        };

    private static CompletionItemKind MapKind(CatalogObjectKind kind) => kind switch
    {
        CatalogObjectKind.Table => CompletionItemKind.Class,
        CatalogObjectKind.View => CompletionItemKind.Interface,
        CatalogObjectKind.Procedure => CompletionItemKind.Method,
        CatalogObjectKind.Function => CompletionItemKind.Function,
        CatalogObjectKind.Schema => CompletionItemKind.Module,
        _ => CompletionItemKind.Field
    };

    private static bool Matches(string candidate, string filter)
        => string.IsNullOrWhiteSpace(filter)
           || candidate.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string Quote(string name)
    {
        if (Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            return name;
        }

        return $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static (bool AfterDot, List<string> QualifierParts) ParseDottedContext(string prefix)
    {
        var match = Regex.Match(prefix, @"([A-Za-z0-9_\]\.\[]+)$");
        if (!match.Success)
        {
            return (false, []);
        }

        var token = match.Value;
        var afterDot = token.EndsWith('.');
        if (!afterDot && !token.Contains('.'))
        {
            return (false, []);
        }

        var trimmed = afterDot ? token[..^1] : token;
        var parts = Regex.Matches(trimmed, @"\[([^\]]+)\]|([A-Za-z0-9_]+)")
            .Select(m => string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[2].Value : m.Groups[1].Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        return (afterDot, parts);
    }
}
