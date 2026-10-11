using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// Turns Claude's reply into a FlowDocument: prose as plain text, ```lang fenced blocks as a dark
/// code box coloured like VS Code's Dark+ theme, `inline code` in the code font.
/// </summary>
public static class CodeRenderer
{
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    private static readonly Brush CodeBackground = Frozen(0x1E, 0x1E, 0x1E);
    private static readonly Brush PlainColor = Frozen(0xD4, 0xD4, 0xD4);
    private static readonly Brush KeywordColor = Frozen(0x56, 0x9C, 0xD6);
    private static readonly Brush StringColor = Frozen(0xCE, 0x91, 0x78);
    private static readonly Brush CommentColor = Frozen(0x6A, 0x99, 0x55);
    private static readonly Brush NumberColor = Frozen(0xB5, 0xCE, 0xA8);
    private static readonly Brush TypeColor = Frozen(0x4E, 0xC9, 0xB0);
    private static readonly Brush FunctionColor = Frozen(0xDC, 0xDC, 0xAA);
    private static readonly Brush InlineCodeColor = Frozen(0xC7, 0x25, 0x4E);

    private static readonly Regex Fence = new(@"```[ \t]*(\w*)[^\n]*\n([\s\S]*?)```", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`([^`\n]+)`", RegexOptions.Compiled);

    private static readonly HashSet<string> CLikeKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
        "continue", "default", "do", "double", "else", "enum", "export", "extends", "false", "final", "finally",
        "float", "for", "from", "fun", "function", "if", "implements", "import", "in", "instanceof", "int",
        "interface", "is", "let", "long", "namespace", "new", "null", "of", "override", "package", "private",
        "protected", "public", "readonly", "record", "return", "sealed", "short", "static", "super", "switch",
        "synchronized", "this", "throw", "throws", "true", "try", "typeof", "undefined", "using", "val", "var",
        "void", "volatile", "when", "while", "yield",
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "alter", "and", "as", "asc", "avg", "begin", "between", "by", "case", "commit", "count", "create",
        "delete", "desc", "distinct", "drop", "else", "end", "exists", "foreign", "from", "full", "group", "having",
        "in", "index", "inner", "insert", "into", "is", "join", "key", "left", "like", "limit", "max", "min", "not",
        "null", "offset", "on", "or", "order", "outer", "over", "partition", "primary", "references", "right",
        "rollback", "select", "set", "sum", "table", "then", "union", "update", "values", "when", "where", "with",
    };

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.Ordinal)
    {
        "case", "cd", "curl", "do", "done", "echo", "elif", "else", "esac", "export", "fi", "for", "function",
        "git", "grep", "if", "in", "local", "return", "sudo", "then", "while",
    };

    private const string Strings = @"""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|`[^`]*`";
    private const string Rest = @"|(?<annotation>@[A-Za-z_]\w*)|(?<number>\b\d[\d_]*(?:\.\d+)?[LlFfDdMm]?\b)|(?<word>[A-Za-z_$][\w$]*)";

    private static readonly Regex CLikeTokens = new(@"(?<comment>//[^\n]*|/\*[\s\S]*?\*/)|(?<string>" + Strings + ")" + Rest, RegexOptions.Compiled);
    private static readonly Regex SqlTokens = new(@"(?<comment>--[^\n]*|/\*[\s\S]*?\*/)|(?<string>" + Strings + ")" + Rest, RegexOptions.Compiled);
    private static readonly Regex HashCommentTokens = new(@"(?<comment>#[^\n]*)|(?<string>" + Strings + ")" + Rest, RegexOptions.Compiled);

    public static FlowDocument Render(string text)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            TextAlignment = TextAlignment.Left,
        };
        Append(document, text);
        return document;
    }

    /// <summary>Adds rendered text to the end of an existing document (the chat adds one message at a time).</summary>
    public static void Append(FlowDocument document, string text)
    {
        int at = 0;
        foreach (Match fence in Fence.Matches(text))
        {
            AddProse(document, text[at..fence.Index]);
            document.Blocks.Add(CodeBlock(fence.Groups[2].Value.TrimEnd(), fence.Groups[1].Value.ToLowerInvariant()));
            at = fence.Index + fence.Length;
        }
        AddProse(document, text[at..]);
    }

    private static void AddProse(FlowDocument document, string prose)
    {
        foreach (string block in Regex.Split(prose.Trim(), @"\n\s*\n"))
        {
            if (block.Trim().Length == 0) continue;
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            string[] lines = block.Trim().Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) paragraph.Inlines.Add(new LineBreak());
                AddInlineCode(paragraph, lines[i].TrimEnd('\r'));
            }
            document.Blocks.Add(paragraph);
        }
    }

    private static void AddInlineCode(Paragraph paragraph, string line)
    {
        int at = 0;
        foreach (Match code in InlineCode.Matches(line))
        {
            paragraph.Inlines.Add(new Run(line[at..code.Index]));
            paragraph.Inlines.Add(new Run(code.Groups[1].Value) { FontFamily = CodeFont, Foreground = InlineCodeColor });
            at = code.Index + code.Length;
        }
        paragraph.Inlines.Add(new Run(line[at..]));
    }

    private static Paragraph CodeBlock(string code, string language)
    {
        var paragraph = new Paragraph
        {
            Background = CodeBackground,
            Foreground = PlainColor,
            FontFamily = CodeFont,
            FontSize = 13,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 2, 0, 12),
        };
        (Regex tokens, HashSet<string> keywords) = language switch
        {
            "sql" or "postgres" or "postgresql" or "plsql" or "tsql" => (SqlTokens, SqlKeywords),
            "bash" or "sh" or "shell" or "zsh" or "powershell" or "ps1" or "python" or "py" or "yaml" or "yml" => (HashCommentTokens, ShellKeywords),
            _ => (CLikeTokens, CLikeKeywords),
        };

        int at = 0;
        foreach (Match token in tokens.Matches(code))
        {
            AddPlain(paragraph, code[at..token.Index]);
            Brush? color = token.Groups["comment"].Success ? CommentColor
                : token.Groups["string"].Success ? StringColor
                : token.Groups["annotation"].Success ? FunctionColor
                : token.Groups["number"].Success ? NumberColor
                : WordColor(token.Value, code, token.Index + token.Length, keywords);
            AddRun(paragraph, token.Value, color);
            at = token.Index + token.Length;
        }
        AddPlain(paragraph, code[at..]);
        return paragraph;
    }

    private static Brush? WordColor(string word, string code, int after, HashSet<string> keywords)
    {
        if (keywords.Contains(word)) return KeywordColor;
        while (after < code.Length && code[after] == ' ') after++;
        if (after < code.Length && code[after] == '(') return FunctionColor;
        if (char.IsUpper(word[0])) return TypeColor;
        return null;
    }

    private static void AddPlain(Paragraph paragraph, string text) => AddRun(paragraph, text, null);

    private static void AddRun(Paragraph paragraph, string text, Brush? color)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            if (lines[i].Length == 0) continue;
            var run = new Run(lines[i]);
            if (color != null) run.Foreground = color;
            paragraph.Inlines.Add(run);
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
