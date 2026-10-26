using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DevBreak;

/// <summary>
/// Draws the Mermaid diagrams Claude writes - a flowchart or a sequence diagram - natively in WPF, in the notebook
/// style, so a tutorial can show a picture. Anything it cannot read returns null and is shown as code instead.
/// </summary>
public static class DiagramRenderer
{
    private const double FontSize = 13;
    private const double NodeMaxTextWidth = 170;
    private const double GapX = 70;
    private const double GapY = 46;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    private static FontFamily Font => (FontFamily)Application.Current.FindResource("BodyFont");

    /// <summary>The diagram, scaled down to fit when it is wide; null when the source is not a diagram this can draw.</summary>
    public static UIElement? TryRender(string source)
    {
        try
        {
            string[] lines = source.Replace("\r", "").Split('\n')
                .Select(l => Regex.Replace(l, "%%.*$", "").Trim()).Where(l => l.Length > 0).ToArray();
            if (lines.Length < 2) return null;
            string head = lines[0].ToLowerInvariant();
            Canvas? canvas = head.StartsWith("sequencediagram") ? Sequence(lines[1..])
                : head.StartsWith("flowchart") || head.StartsWith("graph") ? Flow(head, lines[1..])
                : null;
            if (canvas == null) return null;
            var frame = new Border
            {
                Background = Res("CardBrush"), BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = canvas,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            return new Viewbox { Child = frame, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- flowchart

    private enum Shape { Box, Round, Decision, Database, Circle }

    private sealed class Node
    {
        public required string Id;
        public string Label = "";
        public Shape Shape = Shape.Box;
        public int Rank, Order;
        public Size Size;
        public Point At;
    }

    private sealed record Edge(string From, string To, string Label, bool Dashed, bool Thick, bool Arrow);

    // a node reference with an optional shape: id, id[..], id(..), id([..]), id{..}, id((..)), id[(..)]
    private const string NodeRef = @"(?<id>[A-Za-z0-9_\-\.]+)\s*(?<shape>\(\[.*?\]\)|\[\(.*?\)\]|\(\(.*?\)\)|\[.*?\]|\(.*?\)|\{.*?\})?";
    // an arrow: -->, ---, -.->, ==>, with an optional |label| or -- label -->
    private static readonly Regex Link = new(
        @"^\s*(?<arrow>--\s*(?<mid>[^-|>][^>]*?)\s*-->|-\.->|-\.-|==>|-->|---|--)\s*(\|(?<label>[^|]*)\|)?\s*", RegexOptions.Compiled);
    private static readonly Regex NodeAt = new("^" + NodeRef, RegexOptions.Compiled);

    private static Canvas? Flow(string head, string[] lines)
    {
        bool across = head.Contains(" lr") || head.Contains(" rl");
        var nodes = new Dictionary<string, Node>();
        var order = new List<Node>();
        var edges = new List<Edge>();

        Node Get(Match m)
        {
            string id = m.Groups["id"].Value;
            if (!nodes.TryGetValue(id, out Node? node))
            {
                node = new Node { Id = id, Label = id };
                nodes[id] = node;
                order.Add(node);
            }
            if (m.Groups["shape"].Success && m.Groups["shape"].Value.Length > 0)
            {
                string s = m.Groups["shape"].Value;
                (node.Shape, node.Label) = s switch
                {
                    _ when s.StartsWith("([") => (Shape.Round, s[2..^2]),
                    _ when s.StartsWith("[(") => (Shape.Database, s[2..^2]),
                    _ when s.StartsWith("((") => (Shape.Circle, s[2..^2]),
                    _ when s.StartsWith('[') => (Shape.Box, s[1..^1]),
                    _ when s.StartsWith('(') => (Shape.Round, s[1..^1]),
                    _ => (Shape.Decision, s[1..^1]),
                };
                node.Label = Clean(node.Label);
            }
            return node;
        }

        foreach (string line in lines)
        {
            string lower = line.ToLowerInvariant();
            if (lower.StartsWith("subgraph") || lower == "end" || lower.StartsWith("style ") || lower.StartsWith("classdef")
                || lower.StartsWith("class ") || lower.StartsWith("click ") || lower.StartsWith("linkstyle") || lower.StartsWith("direction"))
            {
                continue;
            }
            foreach (string statement in line.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                string rest = statement;
                Match first = NodeAt.Match(rest);
                if (!first.Success) return null;
                Node from = Get(first);
                rest = rest[first.Length..];
                while (rest.Trim().Length > 0)
                {
                    Match link = Link.Match(rest);
                    if (!link.Success) return null;
                    rest = rest[link.Length..];
                    Match next = NodeAt.Match(rest);
                    if (!next.Success) return null;
                    Node to = Get(next);
                    rest = rest[next.Length..];
                    string arrow = link.Groups["arrow"].Value;
                    string label = link.Groups["label"].Success ? link.Groups["label"].Value
                        : link.Groups["mid"].Success ? link.Groups["mid"].Value : "";
                    edges.Add(new Edge(from.Id, to.Id, Clean(label), arrow.Contains('.'), arrow.StartsWith("=="),
                        arrow.EndsWith('>')));
                    from = to;
                }
            }
        }
        if (order.Count == 0 || order.Count > 40) return null;

        // ranks: the longest path from a start, ignoring edges that go back (cycles)
        var outgoing = order.ToDictionary(n => n.Id, n => edges.Where(e => e.From == n.Id).Select(e => e.To).ToList());
        var state = new Dictionary<string, int>();
        var back = new HashSet<(string, string)>();
        void Dfs(string id)
        {
            state[id] = 1;
            foreach (string to in outgoing[id])
            {
                if (!state.TryGetValue(to, out int s)) Dfs(to);
                else if (s == 1) back.Add((id, to));
            }
            state[id] = 2;
        }
        foreach (Node n in order) if (!state.ContainsKey(n.Id)) Dfs(n.Id);
        var forward = edges.Where(e => e.From != e.To && !back.Contains((e.From, e.To))).ToList();
        for (int pass = 0; pass < order.Count; pass++)
        {
            bool changed = false;
            foreach (Edge e in forward)
            {
                if (nodes[e.To].Rank < nodes[e.From].Rank + 1)
                {
                    nodes[e.To].Rank = nodes[e.From].Rank + 1;
                    changed = true;
                }
            }
            if (!changed) break;
        }

        // order inside a rank: first appearance, then once by the average position of the nodes pointing in
        var ranks = order.GroupBy(n => n.Rank).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        for (int r = 0; r < ranks.Count; r++)
        {
            if (r > 0)
            {
                ranks[r] = ranks[r].OrderBy(n =>
                {
                    var parents = forward.Where(e => e.To == n.Id).Select(e => nodes[e.From]).ToList();
                    return parents.Count == 0 ? n.Order + 1000 : parents.Average(p => p.Order);
                }).ToList();
            }
            for (int i = 0; i < ranks[r].Count; i++) ranks[r][i].Order = i;
        }

        foreach (Node n in order)
        {
            Size text = Measure(n.Label, NodeMaxTextWidth, bold: false);
            n.Size = n.Shape switch
            {
                Shape.Decision => new Size(text.Width * 1.45 + 30, text.Height * 1.6 + 26),
                Shape.Circle => new Size(Math.Max(text.Width, text.Height) + 34, Math.Max(text.Width, text.Height) + 34),
                Shape.Database => new Size(text.Width + 34, text.Height + 34),
                _ => new Size(text.Width + 30, text.Height + 18),
            };
        }

        // place: ranks along the flow, nodes of a rank side by side, centred on the widest rank
        double along = 0;
        var rankAlong = new List<double>();
        foreach (List<Node> rank in ranks)
        {
            rankAlong.Add(along);
            along += (across ? rank.Max(n => n.Size.Width) : rank.Max(n => n.Size.Height)) + (across ? GapX : GapY);
        }
        double Cross(List<Node> rank) => rank.Sum(n => across ? n.Size.Height : n.Size.Width) + (rank.Count - 1) * (across ? GapY * 0.6 : GapX * 0.45);
        double widest = ranks.Max(Cross);
        for (int r = 0; r < ranks.Count; r++)
        {
            double cross = (widest - Cross(ranks[r])) / 2;
            double thick = across ? ranks[r].Max(n => n.Size.Width) : ranks[r].Max(n => n.Size.Height);
            foreach (Node n in ranks[r])
            {
                n.At = across
                    ? new Point(rankAlong[r] + (thick - n.Size.Width) / 2, cross)
                    : new Point(cross, rankAlong[r] + (thick - n.Size.Height) / 2);
                cross += (across ? n.Size.Height + GapY * 0.6 : n.Size.Width + GapX * 0.45);
            }
        }

        var canvas = new Canvas
        {
            Width = order.Max(n => n.At.X + n.Size.Width) + 4,
            Height = order.Max(n => n.At.Y + n.Size.Height) + 4,
        };
        foreach (Edge e in edges) DrawEdge(canvas, nodes[e.From], nodes[e.To], e, across);
        foreach (Node n in order) DrawNode(canvas, n);
        return canvas;
    }

    private static void DrawNode(Canvas canvas, Node n)
    {
        Brush ink = Res("InkBrush");
        Brush fill = n.Shape switch
        {
            Shape.Decision => Res("MarkerBrush"),
            Shape.Database => Res("VioletTintBrush"),
            Shape.Circle => Res("CoralTintBrush"),
            _ => Res("CardBrush"),
        };
        UIElement shape = n.Shape switch
        {
            Shape.Decision => new Polygon
            {
                Points = [new(n.Size.Width / 2, 0), new(n.Size.Width, n.Size.Height / 2), new(n.Size.Width / 2, n.Size.Height), new(0, n.Size.Height / 2)],
                Fill = fill, Stroke = ink, StrokeThickness = 1.6,
            },
            Shape.Circle => new Ellipse { Width = n.Size.Width, Height = n.Size.Height, Fill = fill, Stroke = ink, StrokeThickness = 1.6 },
            Shape.Database => Cylinder(n.Size, fill, ink),
            _ => new Rectangle
            {
                Width = n.Size.Width, Height = n.Size.Height, Fill = fill, Stroke = ink, StrokeThickness = 1.6,
                RadiusX = n.Shape == Shape.Round ? n.Size.Height / 2 : 7, RadiusY = n.Shape == Shape.Round ? n.Size.Height / 2 : 7,
            },
        };
        Canvas.SetLeft(shape, n.At.X);
        Canvas.SetTop(shape, n.At.Y);
        canvas.Children.Add(shape);
        var text = Text(n.Label, NodeMaxTextWidth, bold: n.Shape != Shape.Box);
        text.Width = n.Size.Width - 8;
        text.TextAlignment = TextAlignment.Center;
        text.Measure(new Size(text.Width, double.PositiveInfinity));
        Canvas.SetLeft(text, n.At.X + 4);
        Canvas.SetTop(text, n.At.Y + (n.Size.Height - text.DesiredSize.Height) / 2 + (n.Shape == Shape.Database ? 5 : 0));
        canvas.Children.Add(text);
    }

    private static UIElement Cylinder(Size size, Brush fill, Brush ink)
    {
        double lip = 8;
        var geometry = Geometry.Parse(string.Format(CultureInfo.InvariantCulture,
            "M0,{0} A{1},{0} 0 0 1 {2},{0} L{2},{3} A{1},{0} 0 0 1 0,{3} Z M0,{0} A{1},{0} 0 0 0 {2},{0}",
            lip, size.Width / 2, size.Width, size.Height - lip));
        return new Path { Data = geometry, Fill = fill, Stroke = ink, StrokeThickness = 1.6 };
    }

    private static void DrawEdge(Canvas canvas, Node from, Node to, Edge e, bool across)
    {
        Point a = Center(from), b = Center(to);
        if (from == to)
        {
            // a node pointing to itself: a small loop on its side
            a = new Point(from.At.X + from.Size.Width, from.At.Y + from.Size.Height * 0.3);
            b = new Point(from.At.X + from.Size.Width, from.At.Y + from.Size.Height * 0.7);
            var loop = new Path
            {
                Data = Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M{0},{1} C{2},{1} {2},{3} {0},{3}", a.X, a.Y, a.X + 34, b.Y)),
                Stroke = Res("InkBrush"), StrokeThickness = 1.5,
            };
            canvas.Children.Add(loop);
            if (e.Arrow) canvas.Children.Add(Head(new Point(a.X + 10, b.Y + 2), b));
            return;
        }
        a = Exit(from, b);
        b = Exit(to, a);
        var line = new Line
        {
            X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Res("InkBrush"), StrokeThickness = e.Thick ? 2.6 : 1.5,
            StrokeDashArray = e.Dashed ? [4, 3] : null,
        };
        canvas.Children.Add(line);
        if (e.Arrow) canvas.Children.Add(Head(a, b));
        if (e.Label.Length > 0)
        {
            var label = Text(e.Label, 140, bold: false, size: 12);
            label.Background = Res("PaperBrush");
            label.Padding = new Thickness(4, 1, 4, 1);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, (a.X + b.X) / 2 - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, (a.Y + b.Y) / 2 - label.DesiredSize.Height / 2);
            canvas.Children.Add(label);
        }
    }

    private static Point Center(Node n) => new(n.At.X + n.Size.Width / 2, n.At.Y + n.Size.Height / 2);

    // where the line from the node's centre towards the target leaves its box
    private static Point Exit(Node n, Point toward)
    {
        Point c = Center(n);
        double dx = toward.X - c.X, dy = toward.Y - c.Y;
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return c;
        double hw = n.Size.Width / 2 + 2, hh = n.Size.Height / 2 + 2;
        double scale = Math.Min(Math.Abs(dx) > 0.01 ? hw / Math.Abs(dx) : double.MaxValue, Math.Abs(dy) > 0.01 ? hh / Math.Abs(dy) : double.MaxValue);
        return new Point(c.X + dx * scale, c.Y + dy * scale);
    }

    private static Polygon Head(Point from, Point to, Brush? brush = null)
    {
        double angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double len = 10, half = 0.42;
        return new Polygon
        {
            Points =
            [
                to,
                new(to.X - len * Math.Cos(angle - half), to.Y - len * Math.Sin(angle - half)),
                new(to.X - len * Math.Cos(angle + half), to.Y - len * Math.Sin(angle + half)),
            ],
            Fill = brush ?? Res("InkBrush"),
        };
    }

    // ---------------------------------------------------------------- sequence diagram

    private sealed record Message(string From, string To, string Text, bool Dashed, bool Arrow, bool Cross);
    private sealed record NoteRow(string[] Over, string Text);

    private static readonly Regex Participant = new(@"^(participant|actor)\s+(?<id>[^\s]+)(\s+as\s+(?<label>.+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Send = new(@"^(?<from>[^\s\-+>x]+?)\s*(?<arrow>-->>|->>|-->|->|--x|-x|--\)|-\))\s*[+\-]?(?<to>[^\s:]+)\s*:\s*(?<text>.*)$", RegexOptions.Compiled);
    private static readonly Regex Note = new(@"^note\s+(over|left of|right of)\s+(?<who>[^:]+):\s*(?<text>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Canvas? Sequence(string[] lines)
    {
        var ids = new List<string>();
        var labels = new Dictionary<string, string>();
        var rows = new List<object>();
        void Add(string id)
        {
            if (!ids.Contains(id))
            {
                ids.Add(id);
                labels.TryAdd(id, id);
            }
        }

        foreach (string line in lines)
        {
            Match p = Participant.Match(line);
            Match s = Send.Match(line);
            Match n = Note.Match(line);
            if (p.Success)
            {
                string id = p.Groups["id"].Value;
                Add(id);
                if (p.Groups["label"].Success) labels[id] = Clean(p.Groups["label"].Value);
            }
            else if (s.Success)
            {
                string from = s.Groups["from"].Value, to = s.Groups["to"].Value, arrow = s.Groups["arrow"].Value;
                Add(from);
                Add(to);
                rows.Add(new Message(from, to, Clean(s.Groups["text"].Value), arrow.StartsWith("--"), arrow.EndsWith(">>") || arrow.EndsWith('>'), arrow.EndsWith('x')));
            }
            else if (n.Success)
            {
                string[] who = n.Groups["who"].Value.Split(',').Select(w => w.Trim()).Where(w => w.Length > 0).ToArray();
                foreach (string w in who) Add(w);
                rows.Add(new NoteRow(who, Clean(n.Groups["text"].Value)));
            }
            // autonumber, activate, loop/alt/opt/end and the rest are left out of the picture
        }
        if (ids.Count == 0 || rows.Count == 0 || ids.Count > 10) return null;

        // columns: wide enough for each participant's box and for the messages between neighbours
        var boxSize = ids.ToDictionary(id => id, id => Measure(labels[id], 150, bold: true));
        var gap = new double[ids.Count];
        for (int i = 0; i < ids.Count - 1; i++) gap[i] = 150;
        foreach (Message m in rows.OfType<Message>())
        {
            int a = ids.IndexOf(m.From), b = ids.IndexOf(m.To);
            if (a == b) continue;
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            double need = Measure(m.Text, 260, bold: false, size: 12).Width + 30;
            double have = Enumerable.Range(lo, hi - lo).Sum(i => gap[i]);
            if (need > have) for (int i = lo; i < hi; i++) gap[i] += (need - have) / (hi - lo);
        }
        var x = new double[ids.Count];
        x[0] = Math.Max(boxSize[ids[0]].Width / 2 + 16, 60);
        for (int i = 1; i < ids.Count; i++) x[i] = x[i - 1] + Math.Max(gap[i - 1], (boxSize[ids[i - 1]].Width + boxSize[ids[i]].Width) / 2 + 40);

        double boxHeight = ids.Max(id => boxSize[id].Height) + 16;
        double y = boxHeight + 18;
        var canvas = new Canvas();
        var drawn = new List<(UIElement Element, double Top)>();
        foreach (object row in rows)
        {
            if (row is Message m)
            {
                int a = ids.IndexOf(m.From), b = ids.IndexOf(m.To);
                var text = Text(m.Text, 260, bold: false, size: 12);
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double textHeight = text.DesiredSize.Height;
                if (a == b)
                {
                    double top = y + textHeight + 2;
                    var loop = new Path
                    {
                        Data = Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M{0},{1} h34 v18 h-34", x[a], top)),
                        Stroke = Res("InkBrush"), StrokeThickness = 1.5, StrokeDashArray = m.Dashed ? [4, 3] : null,
                    };
                    canvas.Children.Add(loop);
                    canvas.Children.Add(Head(new Point(x[a] + 20, top + 18), new Point(x[a] + 2, top + 18)));
                    Canvas.SetLeft(text, x[a] + 40);
                    Canvas.SetTop(text, y + 4);
                    canvas.Children.Add(text);
                    y = top + 18 + 22;
                    continue;
                }
                double lineY = y + textHeight + 4;
                Canvas.SetLeft(text, (x[a] + x[b]) / 2 - text.DesiredSize.Width / 2);
                Canvas.SetTop(text, y);
                canvas.Children.Add(text);
                double dir = Math.Sign(x[b] - x[a]);
                var arrow = new Line
                {
                    X1 = x[a], Y1 = lineY, X2 = x[b] - dir * 2, Y2 = lineY, Stroke = Res("InkBrush"), StrokeThickness = 1.5,
                    StrokeDashArray = m.Dashed ? [5, 3] : null,
                };
                canvas.Children.Add(arrow);
                if (m.Cross)
                {
                    canvas.Children.Add(new TextBlock { Text = "×", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Res("CoralInkBrush") });
                    Canvas.SetLeft(canvas.Children[^1], x[b] - dir * 8 - 5);
                    Canvas.SetTop(canvas.Children[^1], lineY - 11);
                }
                else if (m.Arrow)
                {
                    canvas.Children.Add(Head(new Point(x[a], lineY), new Point(x[b] - dir * 1, lineY)));
                }
                y = lineY + 20;
            }
            else if (row is NoteRow n)
            {
                int[] cols = n.Over.Select(w => ids.IndexOf(w)).ToArray();
                double left = cols.Min(c => x[c]) - 60, right = cols.Max(c => x[c]) + 60;
                var text = Text(n.Text, Math.Max(right - left - 16, 120), bold: false, size: 12);
                text.Measure(new Size(Math.Max(right - left - 16, 120), double.PositiveInfinity));
                double width = Math.Max(right - left, text.DesiredSize.Width + 16);
                var note = new Border
                {
                    Background = Res("MarkerBrush"), BorderBrush = Res("InkBrush"), BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), Width = width, Child = text,
                };
                note.Measure(new Size(width, double.PositiveInfinity));
                Canvas.SetLeft(note, (left + right) / 2 - width / 2);
                Canvas.SetTop(note, y);
                drawn.Add((note, y));
                y += note.DesiredSize.Height + 14;
            }
        }
        double bottom = y + 6;

        // lifelines and participant boxes (top and bottom), drawn under the messages
        for (int i = 0; i < ids.Count; i++)
        {
            var life = new Line
            {
                X1 = x[i], Y1 = boxHeight, X2 = x[i], Y2 = bottom, Stroke = Res("LineBrush"), StrokeThickness = 1.4,
                StrokeDashArray = [5, 4],
            };
            canvas.Children.Insert(0, life);
            foreach (double top in new[] { 0, bottom })
            {
                Size s = boxSize[ids[i]];
                var box = new Border
                {
                    Background = Res("VioletTintBrush"), BorderBrush = Res("InkBrush"), BorderThickness = new Thickness(1.6),
                    CornerRadius = new CornerRadius(7), Width = s.Width + 26, Height = boxHeight,
                    Child = new TextBlock
                    {
                        Text = labels[ids[i]], FontFamily = Font, FontSize = FontSize, FontWeight = FontWeights.Bold,
                        Foreground = Res("InkBrush"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
                    },
                };
                Canvas.SetLeft(box, x[i] - (s.Width + 26) / 2);
                Canvas.SetTop(box, top);
                canvas.Children.Add(box);
            }
        }
        foreach ((UIElement element, double _) in drawn) canvas.Children.Add(element);
        canvas.Width = x[^1] + Math.Max(boxSize[ids[^1]].Width / 2 + 16, 60) + 40;
        canvas.Height = bottom + boxHeight + 2;
        return canvas;
    }

    // ---------------------------------------------------------------- text

    private static string Clean(string label) =>
        label.Trim().Trim('"').Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n").Replace("&quot;", "\"").Trim();

    private static TextBlock Text(string text, double maxWidth, bool bold, double size = FontSize) => new()
    {
        Text = text, FontFamily = Font, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Res("InkBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth,
    };

    private static Size Measure(string text, double maxWidth, bool bold, double size = FontSize)
    {
        TextBlock block = Text(text, maxWidth, bold, size);
        block.Measure(new Size(maxWidth, double.PositiveInfinity));
        return new Size(Math.Ceiling(block.DesiredSize.Width), Math.Ceiling(block.DesiredSize.Height));
    }
}
