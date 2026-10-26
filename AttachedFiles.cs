using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevBreak;

/// <summary>
/// Files added to a message: pasted screenshots saved as PNG, image thumbnails, and the chips that show a file
/// (a thumbnail for an image, a badge with its type for anything else) above the message box and in the transcript.
/// </summary>
public static class AttachedFiles
{
    // how a session message lists its files for Claude; the transcript reads the same lines back to show them as chips
    private const string Header = "[Attached files - read them with your Read tool (a folder: list it):";

    public static bool IsImage(string path) =>
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Saves a pasted screenshot where Claude may read it (the temp folder); returns its path.</summary>
    public static string SaveImage(BitmapSource image)
    {
        string dir = Path.Combine(Path.GetTempPath(), "devbreak-images");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(file);
        encoder.Save(stream);
        return file;
    }

    /// <summary>An image read without keeping the file locked, scaled down for the screen.</summary>
    public static BitmapImage Load(string file, int width)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = width;
        bitmap.UriSource = new Uri(file);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The message Claude gets: the text, then the files, one per line.</summary>
    public static string Message(string text, IReadOnlyList<string> files) =>
        files.Count == 0 ? text
            : (text.Length > 0 ? text : "(see the attached files)") + "\n\n" + Header + "\n" + string.Join("\n", files.Select(f => "- " + f)) + "\n]";

    /// <summary>
    /// A sent message split back into its text and its files - only when it ends exactly as <see cref="Message"/> writes
    /// it (the header on its own line, "- path" lines, "]" last); any other message, even one quoting the header, has none.
    /// </summary>
    public static (string Text, List<string> Files) Split(string message)
    {
        int at = message.LastIndexOf(Header, StringComparison.Ordinal);
        if (at < 0 || (at > 0 && message[at - 1] != '\n')) return (message, []);
        List<string> lines = message[(at + Header.Length)..].Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count < 3 || lines[0].Length != 0 || lines[^1] != "]") return (message, []);
        List<string> list = lines[1..^1];
        if (list.Any(l => !l.StartsWith("- ", StringComparison.Ordinal) || l.Length < 3)) return (message, []);
        string text = message[..at].Trim();
        return (text == "(see the attached files)" ? "" : text, list.Select(l => l[2..]).ToList());
    }

    // a path on a local drive ("C:\..."): only these are looked at on disk - a network path (\\server\share\...) would
    // make Windows connect to that server, and send it the user's credentials, just to draw a chip
    private static bool IsLocal(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    /// <summary>
    /// A file as a chip: a thumbnail for an image that still exists, else a badge with its type ("DIR" for a folder),
    /// and its name; <paramref name="onRemove"/> adds an × that takes it out.
    /// </summary>
    public static FrameworkElement Chip(string file, FrameworkElement resources, Action? onRemove = null)
    {
        Brush Res(string key) => (Brush)resources.FindResource(key);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        bool local = IsLocal(file);
        if (local && IsImage(file) && File.Exists(file))
        {
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0),
                Child = new Image { Source = Load(file, 96), Height = 30, MaxWidth = 48, Stretch = Stretch.Uniform },
            });
        }
        else
        {
            bool folder = local && Directory.Exists(file);
            string kind = folder ? "DIR" : Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
            row.Children.Add(new Border
            {
                Background = Res("VioletTintBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = kind.Length is > 0 and <= 5 ? kind : "FILE", FontSize = 10.5, FontWeight = FontWeights.Bold,
                    Foreground = Res("VioletBrush"),
                },
            });
        }
        string name = Path.GetFileName(file.TrimEnd('\\', '/'));
        row.Children.Add(new TextBlock
        {
            Text = name.Length > 0 ? name : file, FontSize = 12.5, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Foreground = Res("InkBrush"),
        });
        if (onRemove != null)
        {
            var remove = new Button
            {
                Content = "×", Style = (Style)resources.FindResource("IconButton"), Width = 22, Height = 22, Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove this file",
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, "Remove " + name);
            remove.Click += (_, _) => onRemove();
            row.Children.Add(remove);
        }
        return new Border
        {
            BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Background = Res("CardBrush"), Padding = new Thickness(6, 4, onRemove != null ? 4 : 10, 4),
            Margin = new Thickness(0, 0, 8, 6), ToolTip = file, Child = row,
        };
    }
}
