using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevBreak;

/// <summary>
/// A follow-up conversation with Claude, shown beside the quiz or the TL;DR in the main window: about one question
/// and its answer, or about a TL;DR.
/// </summary>
public partial class ChatPanel : UserControl
{
    private readonly Func<IReadOnlyList<(bool IsMine, string Text)>, Task<string>> _reply;
    private readonly List<(bool IsMine, string Text)> _history = [];

    // images added to the message being written
    private readonly List<string> _pending = [];

    /// <summary>The close button was pressed; the owner removes the panel.</summary>
    public event Action? CloseRequested;

    /// <param name="mine">The developer's answer and <paramref name="score"/> its score, read at every turn.</param>
    public ChatPanel(string topic, Question question, Func<string> mine, Func<Score?> score)
        : this(topic, "Ask anything about this question or its answer: why, what if, a deeper example, or check an idea.",
               history => QuestionGenerator.ChatAsync(question, mine(), score(), history))
    {
    }

    public ChatPanel(string course, Lesson lesson, LessonContent content)
        : this(lesson.Title, "Ask anything about this lesson: why it works this way, a deeper example, how it applies to your work, or check an idea.",
               history => QuestionGenerator.ChatAboutLessonAsync(course, lesson, content, history))
    {
    }

    public ChatPanel(string topic, TldrContent tldr)
        : this(topic, "Ask anything about this TL;DR: what changed and why, how to use it, or what it means for your stack.",
               history => QuestionGenerator.ChatAboutTldrAsync(topic, tldr, history))
    {
    }

    private ChatPanel(string topic, string intro, Func<IReadOnlyList<(bool IsMine, string Text)>, Task<string>> reply)
    {
        _reply = reply;
        InitializeComponent();
        TopicText.Text = topic;
        Transcript.Document = CodeRenderer.Render("");
        AddMessage(false, intro);
    }

    public void FocusInput() => Dispatcher.BeginInvoke(() => InputBox.Focus());

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
        else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && PasteImages())
        {
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+V with a screenshot (or image files) on the clipboard adds them; plain text pastes as usual.</summary>
    private bool PasteImages()
    {
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource image)
        {
            Attach(SaveImage(image));
            return true;
        }
        if (Clipboard.ContainsFileDropList())
        {
            string[] files = Clipboard.GetFileDropList().Cast<string>().Where(IsImage).ToArray();
            foreach (string file in files) Attach(file);
            return files.Length > 0;
        }
        return false;
    }

    private void InputBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsImage))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void InputBox_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || !files.Any(IsImage)) return;
        foreach (string file in files.Where(IsImage)) Attach(file);
        e.Handled = true;
    }

    private static bool IsImage(string path) =>
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant());

    // a pasted screenshot is saved where the discussion may read it (its working folder is the temp folder)
    private static string SaveImage(BitmapSource image)
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

    /// <summary>Adds an image to the next message: a thumbnail above the box, with a button that takes it out again.</summary>
    private void Attach(string file)
    {
        if (_pending.Contains(file)) return;
        _pending.Add(file);
        var remove = new Button
        {
            Content = "×", Style = (Style)FindResource("IconButton"), Width = 22, Height = 22, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, -6, 0),
            ToolTip = "Remove this image",
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, "Remove image");
        var thumb = new Grid { Margin = new Thickness(0, 0, 10, 0), ToolTip = file };
        thumb.Children.Add(new Border
        {
            BorderBrush = (Brush)FindResource("InkBrush"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(6),
            Child = new Image { Source = Load(file, 120), Height = 56, MaxWidth = 120, Stretch = Stretch.Uniform },
        });
        thumb.Children.Add(remove);
        remove.Click += (_, _) =>
        {
            _pending.Remove(file);
            Attachments.Children.Remove(thumb);
            Attachments.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        Attachments.Children.Add(thumb);
        Attachments.Visibility = Visibility.Visible;
    }

    // read without keeping the file locked, scaled down for the screen
    private static BitmapImage Load(string file, int width)
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

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private async void Send()
    {
        string text = InputBox.Text.Trim();
        if ((text.Length == 0 && _pending.Count == 0) || !SendButton.IsEnabled) return;
        string[] images = [.. _pending];
        InputBox.Clear();
        _pending.Clear();
        Attachments.Children.Clear();
        Attachments.Visibility = Visibility.Collapsed;
        // Claude gets the image paths in the message (it reads them with its Read tool); the transcript shows the images
        string message = images.Length == 0 ? text
            : (text.Length > 0 ? text : "(see the attached image)") +
              "\n[Attached image" + (images.Length > 1 ? "s" : "") + " - read with your Read tool: " + string.Join(", ", images) + "]";
        _history.Add((true, message));
        AddMessage(true, text.Length > 0 ? text : "(image)");
        if (images.Length > 0) AddImages(images);
        SetThinking(true);
        try
        {
            string reply = (await _reply(_history)).Trim();
            _history.Add((false, reply));
            AddMessage(false, reply);
        }
        catch (Exception ex)
        {
            _history.RemoveAt(_history.Count - 1);
            AddMessage(false, "Could not reach the claude CLI: " + ex.Message);
        }
        SetThinking(false);
        InputBox.Focus();
    }

    private void SetThinking(bool thinking)
    {
        SendButton.IsEnabled = !thinking;
        ThinkingText.Visibility = thinking ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddMessage(bool isMine, string text) => Turns.Add(Transcript, isMine, text, this);

    /// <summary>The images of a sent message, in the transcript under it.</summary>
    private void AddImages(IEnumerable<string> files)
    {
        var row = new WrapPanel();
        foreach (string file in files)
        {
            row.Children.Add(new Border
            {
                BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 8), ToolTip = file,
                Child = new Image { Source = Load(file, 360), MaxHeight = 160, MaxWidth = 300, Stretch = Stretch.Uniform },
            });
        }
        Transcript.Document.Blocks.Add(new BlockUIContainer(row) { Margin = new Thickness(0, 0, 0, 6) });
        Transcript.ScrollToEnd();
    }
}
