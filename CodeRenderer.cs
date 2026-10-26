using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// Turns Claude's reply into a FlowDocument: prose with light Markdown (headings, lists, bold, italic, links),
/// ```lang fenced blocks as an ink code box coloured like VS Code's Dark+ theme, `inline code` in the code font on a
/// violet tint.
/// </summary>
public static class CodeRenderer
{
    private static readonly FontFamily CodeFont = (FontFamily)Application.Current.FindResource("MonoFont");
    private static readonly FontFamily ProseFont = (FontFamily)Application.Current.FindResource("BodyFont");

    private static readonly Brush CodeBackground = Frozen(0x16, 0x1A, 0x33);
    private static readonly Brush InlineCodeBackground = Frozen(0xF1, 0xEE, 0xFF);
    private static readonly Brush PlainColor = Frozen(0xD4, 0xD4, 0xD4);
    private static readonly Brush KeywordColor = Frozen(0x56, 0x9C, 0xD6);
    private static readonly Brush StringColor = Frozen(0xCE, 0x91, 0x78);
    private static readonly Brush CommentColor = Frozen(0x6A, 0x99, 0x55);
    private static readonly Brush NumberColor = Frozen(0xB5, 0xCE, 0xA8);
    private static readonly Brush TypeColor = Frozen(0x4E, 0xC9, 0xB0);
    private static readonly Brush FunctionColor = Frozen(0xDC, 0xDC, 0xAA);
    private static readonly Brush InlineCodeColor = Frozen(0x3A, 0x22, 0xC4);
    private static readonly Brush HeaderColor = Frozen(0x9A, 0xA0, 0xC8);

    private static readonly Regex Fence = new(@"```[ \t]*(\w*)[^\n]*\n([\s\S]*?)```", RegexOptions.Compiled);

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

    public static FlowDocument Render(string text, bool softWrap = false)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = ProseFont,
            TextAlignment = TextAlignment.Left,
        };
        Append(document, text, softWrap);
        return document;
    }

    /// <summary>Adds rendered text to the end of an existing document (the chat adds one message at a time).</summary>
    /// <param name="softWrap">Markdown files: a single line break inside a paragraph is a space.</param>
    public static void Append(FlowDocument document, string text, bool softWrap = false)
    {
        int at = 0;
        foreach (Match fence in Fence.Matches(text))
        {
            AddProse(document, text[at..fence.Index], softWrap);
            string language = fence.Groups[1].Value.ToLowerInvariant();
            string code = fence.Groups[2].Value.TrimEnd();
            // a ```mermaid block is drawn as a diagram; one it cannot read stays code
            if (language == "mermaid" && DiagramRenderer.TryRender(code) is UIElement diagram)
            {
                document.Blocks.Add(new BlockUIContainer(diagram) { Margin = new Thickness(0, 4, 0, 14) });
            }
            else
            {
                document.Blocks.Add(CodeBlock(code, language));
            }
            at = fence.Index + fence.Length;
        }
        AddProse(document, text[at..], softWrap);
    }

    private static readonly Regex Heading = new(@"^\s{0,3}(#{1,4})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\s*\d+[.)]\s+(.*)$", RegexOptions.Compiled);

    // inline Markdown, in this order: `code`, **bold**, *italic* / _italic_, [text](url), a bare https URL
    private static readonly Regex Inline = new(
        @"`(?<code>[^`\n]+)`|\*\*(?<bold>[^*\n]+?)\*\*|(?<![\w*])\*(?<italic>[^*\s][^*\n]*?)\*(?![\w*])|(?<![\w_])_(?<italic>[^_\s][^_\n]*?)_(?![\w_])" +
        @"|\[(?<text>[^\]\n]+)\]\((?<link>https?://[^)\s]+)\)|(?<url>https?://[^\s<>()]+)",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    /// <summary>
    /// Prose with the Markdown Claude writes: "#" headings, "- " / "1. " lists (lines that follow an item without a
    /// marker continue it), and inline code, bold, italic and links (which open in the browser).
    /// </summary>
    private static void AddProse(FlowDocument document, string prose, bool softWrap)
    {
        foreach (string block in Regex.Split(prose.Trim(), @"\n\s*\n"))
        {
            if (block.Trim().Length == 0) continue;
            Paragraph? paragraph = null;
            List? list = null;
            foreach (string raw in block.Trim().Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                Match heading = Heading.Match(line);
                Match bullet = Bullet.Match(line);
                Match numbered = Numbered.Match(line);
                if (heading.Success)
                {
                    list = null;
                    paragraph = null;
                    var title = new Paragraph
                    {
                        FontWeight = FontWeights.Bold, FontSize = heading.Groups[1].Length <= 2 ? 17 : 15.5,
                        Margin = new Thickness(0, 4, 0, 6),
                    };
                    AddInline(title, heading.Groups[2].Value.Trim().TrimEnd('#').Trim());
                    document.Blocks.Add(title);
                }
                else if (bullet.Success || numbered.Success)
                {
                    TextMarkerStyle marker = bullet.Success ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal;
                    if (list == null || list.MarkerStyle != marker)
                    {
                        list = new List { MarkerStyle = marker, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(22, 0, 0, 0) };
                        document.Blocks.Add(list);
                    }
                    paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                    AddInline(paragraph, (bullet.Success ? bullet : numbered).Groups[1].Value);
                    list.ListItems.Add(new ListItem(paragraph));
                }
                else if (paragraph != null)
                {
                    // a line that continues the paragraph or list item above: a line break in a chat answer, a space
                    // in a markdown file (its lines are wrapped by hand)
                    paragraph.Inlines.Add(softWrap ? new Run(" ") : new LineBreak());
                    AddInline(paragraph, line.Trim());
                }
                else
                {
                    paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                    AddInline(paragraph, line);
                    document.Blocks.Add(paragraph);
                }
            }
        }
    }

    private static void AddInline(Paragraph paragraph, string line)
    {
        int at = 0;
        foreach (Match m in Inline.Matches(line))
        {
            paragraph.Inlines.Add(new Run(line[at..m.Index]));
            if (m.Groups["code"].Success)
            {
                paragraph.Inlines.Add(new Run(m.Groups["code"].Value)
                {
                    FontFamily = CodeFont, Foreground = InlineCodeColor, Background = InlineCodeBackground, FontSize = 13.5,
                });
            }
            else if (m.Groups["bold"].Success)
            {
                var bold = new Bold();
                AddInline(bold, m.Groups["bold"].Value);
                paragraph.Inlines.Add(bold);
            }
            else if (m.Groups["italic"].Success)
            {
                paragraph.Inlines.Add(new Italic(new Run(m.Groups["italic"].Value)));
            }
            else
            {
                string url = m.Groups["link"].Success ? m.Groups["link"].Value : m.Groups["url"].Value.TrimEnd('.', ',', ';', ':', '!', '?');
                string text = m.Groups["text"].Success ? m.Groups["text"].Value : url;
                paragraph.Inlines.Add(Link(text, url));
                // a bare URL may have lost trailing punctuation: keep it as text
                if (m.Groups["url"].Success) paragraph.Inlines.Add(new Run(m.Groups["url"].Value[url.Length..]));
            }
            at = m.Index + m.Length;
        }
        paragraph.Inlines.Add(new Run(line[at..]));
    }

    // bold may hold inline code; a span takes the same inline rules as a paragraph
    private static void AddInline(Span span, string text)
    {
        var holder = new Paragraph();
        AddInline(holder, text);
        foreach (Inline inline in holder.Inlines.ToList())
        {
            holder.Inlines.Remove(inline);
            span.Inlines.Add(inline);
        }
    }

    private static Hyperlink Link(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { ToolTip = url };
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            link.NavigateUri = uri;
            link.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        return link;
    }

    // code longer than this starts folded to a short preview
    private const int FoldAbove = 12;
    private const int PreviewLines = 6;
    private const double CodeLineHeight = 20;

    /// <summary>
    /// A code card: a header with the language, the line count, Copy and - for long code - Show all / Hide, over the
    /// highlighted code. Long code starts folded to its first lines, so an answer is not a wall of code.
    /// </summary>
    private static BlockUIContainer CodeBlock(string code, string language)
    {
        var text = new TextBlock
        {
            Foreground = PlainColor, FontFamily = CodeFont, FontSize = 13, LineHeight = CodeLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(14, 10, 14, 12),
        };
        Highlight(text.Inlines, code, language);

        int lines = code.Replace("\r", "").Split('\n').Length;
        bool folds = lines > FoldAbove;
        double folded = PreviewLines * CodeLineHeight + 10;
        var body = new Grid { MaxHeight = folds ? folded : double.PositiveInfinity, ClipToBounds = true };
        body.Children.Add(text);
        // the folded preview fades out at the bottom
        var fade = new Border
        {
            Height = 36, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Background = new LinearGradientBrush(Color.FromArgb(0, 0x16, 0x1A, 0x33), Color.FromArgb(0xF0, 0x16, 0x1A, 0x33), 90),
            Visibility = folds ? Visibility.Visible : Visibility.Collapsed,
        };
        body.Children.Add(fade);

        var header = new DockPanel { Margin = new Thickness(14, 7, 8, 0), LastChildFill = false };
        var label = new TextBlock
        {
            Text = (language.Length > 0 ? language : "code") + $"  ·  {lines} line{(lines == 1 ? "" : "s")}",
            FontFamily = CodeFont, FontSize = 11.5, Foreground = HeaderColor, VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(label);
        Button copy = HeaderButton("Copy");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(code);
                copy.Content = "Copied ✓";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                copy.Content = "Clipboard busy";
            }
        };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        if (folds)
        {
            Button toggle = HeaderButton($"Show all {lines} lines");
            toggle.Click += (_, _) =>
            {
                bool open = double.IsPositiveInfinity(body.MaxHeight);
                body.MaxHeight = open ? folded : double.PositiveInfinity;
                fade.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = open ? $"Show all {lines} lines" : "Hide";
            };
            DockPanel.SetDock(toggle, Dock.Right);
            header.Children.Add(toggle);
        }

        var card = new StackPanel();
        card.Children.Add(header);
        card.Children.Add(body);
        return new BlockUIContainer(new Border
        {
            Background = CodeBackground, CornerRadius = new CornerRadius(10), Child = card,
        })
        { Margin = new Thickness(0, 4, 0, 14) };
    }

    private static Button HeaderButton(string text)
    {
        var button = new Button
        {
            Content = text, FontSize = 11.5, Foreground = HeaderColor, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
            Template = PlainButtonTemplate,
        };
        return button;
    }

    // a button that is just its text (the app's button styles are for light backgrounds)
    private static readonly ControlTemplate PlainButtonTemplate = MakePlainTemplate();

    private static ControlTemplate MakePlainTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Frozen(0x2A, 0x2F, 0x55)));
        template.Triggers.Add(hover);
        template.Seal();
        return template;
    }

    private static void Highlight(InlineCollection inlines, string code, string language)
    {
        (Regex tokens, HashSet<string> keywords) = language switch
        {
            "sql" or "postgres" or "postgresql" or "plsql" or "tsql" => (SqlTokens, SqlKeywords),
            "bash" or "sh" or "shell" or "zsh" or "powershell" or "ps1" or "python" or "py" or "yaml" or "yml" => (HashCommentTokens, ShellKeywords),
            _ => (CLikeTokens, CLikeKeywords),
        };

        int at = 0;
        foreach (Match token in tokens.Matches(code))
        {
            AddRun(inlines, code[at..token.Index], null);
            Brush? color = token.Groups["comment"].Success ? CommentColor
                : token.Groups["string"].Success ? StringColor
                : token.Groups["annotation"].Success ? FunctionColor
                : token.Groups["number"].Success ? NumberColor
                : WordColor(token.Value, code, token.Index + token.Length, keywords);
            AddRun(inlines, token.Value, color);
            at = token.Index + token.Length;
        }
        AddRun(inlines, code[at..], null);
    }

    private static Brush? WordColor(string word, string code, int after, HashSet<string> keywords)
    {
        if (keywords.Contains(word)) return KeywordColor;
        while (after < code.Length && code[after] == ' ') after++;
        if (after < code.Length && code[after] == '(') return FunctionColor;
        if (char.IsUpper(word[0])) return TypeColor;
        return null;
    }

    private static void AddRun(InlineCollection inlines, string text, Brush? color)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) inlines.Add(new LineBreak());
            if (lines[i].Length == 0) continue;
            var run = new Run(lines[i]);
            if (color != null) run.Foreground = color;
            inlines.Add(run);
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
