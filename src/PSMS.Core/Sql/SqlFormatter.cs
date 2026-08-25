using System.Text;
using System.Text.RegularExpressions;

namespace PSMS.Core.Sql;

/// <summary>Lightweight SQL pretty-printer (keywords, clauses, commas) — no parser dependency.</summary>
public static class SqlFormatter
{
    private static readonly HashSet<string> BreakBefore = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT",
        "INSERT", "UPDATE", "DELETE", "MERGE", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL",
        "CROSS", "APPLY", "ON", "AND", "OR", "SET", "VALUES", "INTO", "WITH", "GO"
    };

    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return sql;
        }

        // Preserve GO batches separately.
        var batches = Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var sb = new StringBuilder();
        for (var i = 0; i < batches.Length; i++)
        {
            if (i > 0)
            {
                sb.AppendLine("GO");
            }

            var batch = batches[i].Trim();
            if (batch.Length == 0)
            {
                continue;
            }

            sb.Append(FormatBatch(batch));
            if (i < batches.Length - 1)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string FormatBatch(string sql)
    {
        var tokens = Tokenize(sql);
        var sb = new StringBuilder();
        var indent = 0;
        var atLineStart = true;

        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (tok.Kind == TokenKind.Whitespace)
            {
                continue;
            }

            if (tok.Kind == TokenKind.Newline)
            {
                sb.AppendLine();
                atLineStart = true;
                continue;
            }

            var upper = tok.Text.ToUpperInvariant();
            var breakBefore = tok.Kind == TokenKind.Word && BreakBefore.Contains(upper)
                              && !(upper is "AND" or "OR" && indent == 0 && atLineStart);

            // JOIN group: LEFT/RIGHT/INNER/OUTER/FULL/CROSS before JOIN stay together.
            if (tok.Kind == TokenKind.Word && upper is "LEFT" or "RIGHT" or "INNER" or "OUTER" or "FULL" or "CROSS")
            {
                var next = PeekWord(tokens, i + 1);
                if (next is "JOIN" or "OUTER" or "APPLY")
                {
                    breakBefore = true;
                }
            }

            if (tok.Kind == TokenKind.Word && upper == "BY")
            {
                // keep with GROUP/ORDER
                breakBefore = false;
            }

            if (breakBefore && sb.Length > 0)
            {
                sb.AppendLine();
                atLineStart = true;
                indent = upper is "AND" or "OR" or "ON" ? 1 : 0;
            }

            if (tok.Text == ",")
            {
                sb.Append(',');
                sb.AppendLine();
                atLineStart = true;
                indent = 1;
                continue;
            }

            if (tok.Text == "(")
            {
                if (!atLineStart)
                {
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(new string(' ', indent * 2));
                }

                sb.Append('(');
                atLineStart = false;
                continue;
            }

            if (tok.Text == ")")
            {
                sb.Append(')');
                atLineStart = false;
                continue;
            }

            if (atLineStart)
            {
                sb.Append(new string(' ', indent * 2));
            }
            else if (NeedsSpace(sb, tok))
            {
                sb.Append(' ');
            }

            if (tok.Kind == TokenKind.Word && IsKeyword(upper))
            {
                sb.Append(upper);
            }
            else
            {
                sb.Append(tok.Text);
            }

            atLineStart = false;
        }

        return sb.ToString().TrimEnd();
    }

    private static bool NeedsSpace(StringBuilder sb, Token tok)
    {
        if (sb.Length == 0)
        {
            return false;
        }

        var prev = sb[^1];
        if (char.IsWhiteSpace(prev) || prev is '(' or '[' or '.')
        {
            return false;
        }

        if (tok.Text is "." or "," or ")" or "]" or ";")
        {
            return false;
        }

        return true;
    }

    private static string? PeekWord(List<Token> tokens, int start)
    {
        for (var i = start; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.Whitespace || tokens[i].Kind == TokenKind.Newline)
            {
                continue;
            }

            return tokens[i].Kind == TokenKind.Word ? tokens[i].Text.ToUpperInvariant() : null;
        }

        return null;
    }

    private static bool IsKeyword(string upper) =>
        BreakBefore.Contains(upper)
        || upper is "AS" or "ASC" or "DESC" or "TOP" or "DISTINCT" or "ALL" or "NULL" or "NOT" or "IN"
            or "EXISTS" or "BETWEEN" or "LIKE" or "CASE" or "WHEN" or "THEN" or "ELSE" or "END"
            or "OVER" or "PARTITION" or "BY" or "WITH" or "NOLOCK" or "BEGIN" or "COMMIT" or "ROLLBACK"
            or "DECLARE" or "IF" or "ELSE" or "WHILE" or "RETURN" or "CREATE" or "ALTER" or "DROP"
            or "TABLE" or "VIEW" or "INDEX" or "PROCEDURE" or "FUNCTION" or "TRIGGER" or "SCHEMA";

    private static List<Token> Tokenize(string sql)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < sql.Length && sql[i + 1] == '\n')
                {
                    i++;
                }

                list.Add(new Token(TokenKind.Newline, "\n"));
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                var start = i;
                while (i < sql.Length && char.IsWhiteSpace(sql[i]) && sql[i] is not '\r' and not '\n')
                {
                    i++;
                }

                list.Add(new Token(TokenKind.Whitespace, sql[start..i]));
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var start = i;
                while (i < sql.Length && sql[i] is not '\r' and not '\n')
                {
                    i++;
                }

                list.Add(new Token(TokenKind.Other, sql[start..i]));
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, sql.Length);
                list.Add(new Token(TokenKind.Other, sql[start..i]));
                continue;
            }

            if (c is '\'' or '"' or '[')
            {
                var start = i;
                var closer = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == closer)
                    {
                        i++;
                        if (closer != ']' && i < sql.Length && sql[i] == closer)
                        {
                            i++;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                list.Add(new Token(TokenKind.Other, sql[start..i]));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '@' || c == '#')
            {
                var start = i;
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '#' or '$'))
                {
                    i++;
                }

                list.Add(new Token(TokenKind.Word, sql[start..i]));
                continue;
            }

            list.Add(new Token(TokenKind.Other, sql[i].ToString()));
            i++;
        }

        return list;
    }

    private enum TokenKind { Word, Whitespace, Newline, Other }

    private readonly record struct Token(TokenKind Kind, string Text);
}
