using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DevBreak;

/// <summary>
/// One session on screen: its header (a status light, title, folder, Setup, Copy resume, Open folder), its
/// transcript and the message box. The Sessions view has one pane, or two side by side when the screen is split.
/// </summary>
public partial class SessionPane : UserControl
{
    private SessionState? _state;

    /// <summary>The pane was clicked or typed in: it becomes the active one.</summary>
    public event Action<SessionPane>? Activated;

    /// <summary>The pane's close button was pressed.</summary>
    public event Action<SessionPane>? CloseRequested;

    /// <summary>A new session finished its first turn, so it now has a file and a title.</summary>
    public event Action? SessionSaved;

    public SessionState? State => _state;

    public SessionPane()
    {
        InitializeComponent();
        PreviewMouseDown += (_, _) => Activated?.Invoke(this);
        PreviewGotKeyboardFocus += (_, _) => Activated?.Invoke(this);
        SetupArea.BackRequested += () => ShowSetup(false);
        SetupArea.UseRequested += text =>
        {
            ShowSetup(false);
            InputBox.Text = text;
            InputBox.CaretIndex = text.Length;
            InputBox.Focus();
        };
    }

    /// <summary>Marks this pane as the active one (outlined) when the screen is split.</summary>
    public void SetActive(bool active, bool split)
    {
        Outline.BorderBrush = active && split ? (Brush)FindResource("VioletBrush") : Brushes.Transparent;
        ClosePaneButton.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows a session here, taking it from the pane that had it; loads its transcript the first time.</summary>
    public async void Show(SessionState state)
    {
        if (_state == state) return;
        if (state.ShownIn != null && state.ShownIn != this) state.ShownIn.Clear();
        Release();
        _state = state;
        state.ShownIn = this;
        state.Changed += OnChanged;
        Loading.Visibility = Visibility.Collapsed;
        SessionTitle.Text = state.Info.Title;
        SessionFolder.Text = state.Info.Folder + "   ·   session " + state.Info.Id;
        Transcript.Document = state.Document;
        CopyResumeButton.IsEnabled = OpenFolderButton.IsEnabled = SetupButton.IsEnabled = true;
        ShowSetup(false);
        Update();
        if (!state.Loaded)
        {
            Loading.Visibility = Visibility.Visible;
            IReadOnlyList<SessionEntry> entries = await Task.Run(() => ClaudeSessions.Transcript(state.Info.Id));
            if (!state.Loaded)
            {
                state.Loaded = true;
                foreach (SessionEntry entry in entries) Add(state, entry);
            }
            if (_state == state) Loading.Visibility = Visibility.Collapsed;
        }
        if (_state == state) ShowLatest();
        InputBox.Focus();
    }

    /// <summary>Shows nothing (the session moved to the other pane, or this pane is closing).</summary>
    public void Clear()
    {
        Release();
        Transcript.Document = CodeRenderer.Render("");
        SessionTitle.Text = "Pick a session";
        SessionFolder.Text = "Pick one on the left for this pane.";
        CopyResumeButton.IsEnabled = OpenFolderButton.IsEnabled = SetupButton.IsEnabled = false;
        ShowSetup(false);
        Update();
    }

    private void Release()
    {
        if (_state == null) return;
        _state.Changed -= OnChanged;
        if (_state.ShownIn == this) _state.ShownIn = null;
        _state = null;
        // a FlowDocument can belong to one RichTextBox: give this one back before another pane takes it
        Transcript.Document = new FlowDocument();
    }

    private void OnChanged(SessionState state) => Update();

    // the light, the buttons and the working line follow the session's state
    private void Update()
    {
        bool running = _state?.Working == true;
        InputBox.IsEnabled = _state != null;
        SendButton.IsEnabled = _state != null && !running;
        StopButton.IsEnabled = running;
        WorkingText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running) WorkingText.Text = $"Claude is working... ({_state!.Steps} step{(_state.Steps == 1 ? "" : "s")})";
        Lights.Set(Light, _state, this);
    }

    private void ShowLatest()
    {
        if (_state?.ReplyStart != null) Turns.ScrollToStart(Transcript, _state.ReplyStart);
        else Transcript.ScrollToEnd();
    }

    private void Setup_Click(object sender, RoutedEventArgs e) => ShowSetup(SetupArea.Visibility != Visibility.Visible);

    private void ShowSetup(bool show)
    {
        if (show && _state == null) return;
        SetupArea.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ChatArea.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SetupButton.Content = show ? "Chat" : "Setup";
        if (show) SetupArea.Show(_state!.Info.Folder);
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private async void Send()
    {
        SessionState? state = _state;
        string text = InputBox.Text.Trim();
        if (state == null || state.Working || text.Length == 0) return;
        InputBox.Clear();
        Add(state, new SessionEntry(SessionEntryKind.You, text));
        state.Running = new CancellationTokenSource();
        state.Steps = 0;
        state.Failed = false;
        state.Notify();
        bool wasNew = state.IsNew;
        try
        {
            await ClaudeSessions.RunTurnAsync(state.Info.Folder, state.Info.Id, state.IsNew, text, entry =>
                Dispatcher.Invoke(() =>
                {
                    if (entry.Kind == SessionEntryKind.Tool) state.Steps++;
                    Add(state, entry);
                    state.Notify();
                }), state.Running.Token);
            state.IsNew = false;
        }
        catch (Exception ex)
        {
            Add(state, new SessionEntry(SessionEntryKind.Error, "Could not run the claude CLI: " + ex.Message));
        }
        state.Running.Dispose();
        state.Running = null;
        state.Notify();
        if (wasNew && !state.IsNew) SessionSaved?.Invoke();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _state?.Running?.Cancel();

    private void CopyResume_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        Clipboard.SetText(ClaudeSessions.ResumeCommand(_state.Info));
        WorkingText.Text = "Copied - paste it in a terminal to continue there.";
        WorkingText.Visibility = Visibility.Visible;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_state != null) Process.Start(new ProcessStartInfo(_state.Info.Folder) { UseShellExecute = true });
    }

    private void ClosePane_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);

    /// <summary>
    /// Adds a step of a session to its transcript: the developer's message in its box; one "Claude" label per turn
    /// with only the turn's answer under it; the work on the way folded into one line; the done line. When the session
    /// is on screen (in whichever pane), the view goes to the start of the reply once and then stays.
    /// </summary>
    private void Add(SessionState state, SessionEntry entry)
    {
        FlowDocument document = state.Document;
        RichTextBox? shown = state.ShownIn?.Transcript;
        Brush Res(string key) => (Brush)FindResource(key);
        switch (entry.Kind)
        {
            case SessionEntryKind.You:
                Turns.Add(document, isMine: true, entry.Text, this);
                state.ReplyStart = null;
                state.Group = null;
                shown?.ScrollToEnd();
                return;
            case SessionEntryKind.Note:
            case SessionEntryKind.Tool:
                StartReply(state, shown);
                if (state.Group == null)
                {
                    state.Group = new StepGroup(Res("VioletBrush"), Res("InkSoftBrush"), (FontFamily)FindResource("MonoFont"), this);
                    document.Blocks.Add(state.Group.Block);
                }
                if (entry.Kind == SessionEntryKind.Tool) state.Group.AddStep(entry.Text);
                else state.Group.AddNote(entry.Text);
                return;
            case SessionEntryKind.Answer:
                StartReply(state, shown);
                state.Group?.DropNote(entry.Text);
                CodeRenderer.Append(document, entry.Text);
                state.Group = null;
                return;
            case SessionEntryKind.Done:
                state.Group = null;
                document.Blocks.Add(new Paragraph(new Run("✓ done · " + entry.Text))
                {
                    FontSize = 11.5, Foreground = Res("InkSoftBrush"), Margin = new Thickness(0, 2, 0, 8),
                });
                return;
            case SessionEntryKind.Error:
                state.Group = null;
                state.Failed = true;
                document.Blocks.Add(new Paragraph(new Run(entry.Text))
                {
                    Foreground = Res("CoralInkBrush"), Margin = new Thickness(0, 4, 0, 8),
                });
                return;
        }
    }

    // the "Claude" label, once per turn; on screen, the view goes to it the first time
    private void StartReply(SessionState state, RichTextBox? shown)
    {
        if (state.ReplyStart != null) return;
        state.ReplyStart = Turns.Add(state.Document, isMine: false, "", this);
        if (shown != null && state.Loaded && state.Working) Turns.ScrollToStart(shown, state.ReplyStart);
    }
}

/// <summary>The status light of a session: green and pulsing while Claude works, red after a failed or stopped turn.</summary>
public static class Lights
{
    public static void Set(Ellipse light, SessionState? state, FrameworkElement resources)
    {
        light.BeginAnimation(UIElement.OpacityProperty, null);
        light.Opacity = 1;
        if (state == null || (!state.Working && !state.Failed))
        {
            light.Visibility = Visibility.Collapsed;
            return;
        }
        light.Visibility = Visibility.Visible;
        light.Fill = state.Working ? Frozen(0x2E, 0xB8, 0x5C) : Frozen(0xE5, 0x48, 0x2E);
        light.Stroke = (Brush)resources.FindResource("InkBrush");
        light.StrokeThickness = 1.2;
        light.ToolTip = state.Working ? $"Claude is working ({state.Steps} steps)" : "The last turn failed or was stopped";
        if (state.Working)
        {
            light.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The work of a turn - tool steps and the notes Claude wrote on the way - folded into one line
/// ("⚙ 3 steps · 2 notes · last: ...") that opens to list it all in order.
/// </summary>
internal sealed class StepGroup
{
    private readonly StackPanel _items = new() { Margin = new Thickness(20, 4, 0, 2) };
    private readonly TextBlock _header;
    private readonly Brush _accent, _soft;
    private readonly FontFamily _mono;
    private readonly FrameworkElement _resources;
    private readonly List<(string Text, UIElement Element)> _notes = [];
    private int _steps;
    private string _last = "";

    public BlockUIContainer Block { get; }

    public StepGroup(Brush accent, Brush soft, FontFamily mono, FrameworkElement resources)
    {
        _accent = accent;
        _soft = soft;
        _mono = mono;
        _resources = resources;
        _header = new TextBlock { FontFamily = mono, FontSize = 12, Foreground = soft, TextTrimming = TextTrimming.CharacterEllipsis };
        var expander = new Expander { Header = _header, Content = _items, IsExpanded = false };
        Block = new BlockUIContainer(expander) { Margin = new Thickness(0, 2, 0, 8) };
    }

    public void AddStep(string step)
    {
        int space = step.IndexOf("  ", StringComparison.Ordinal);
        string tool = space > 0 ? step[..space] : step;
        string detail = space > 0 ? step[(space + 2)..] : "";
        var line = new TextBlock { FontFamily = _mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        line.Inlines.Add(new Run(tool) { FontWeight = FontWeights.Bold, Foreground = _accent });
        if (detail.Length > 0) line.Inlines.Add(new Run("  " + detail) { Foreground = _soft });
        _items.Children.Add(line);
        _steps++;
        _last = tool + (detail.Length > 0 ? " " + detail : "");
        Update();
    }

    // a note Claude wrote while working: shown in the fold, rendered like the answer but quieter
    public void AddNote(string text)
    {
        var viewer = new FlowDocumentScrollViewer
        {
            Document = CodeRenderer.Render(text), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false, Opacity = 0.75,
            FontSize = 13, Margin = new Thickness(0, 2, 0, 4), Focusable = false,
        };
        viewer.Document.FontSize = 13;
        viewer.Document.Foreground = (Brush)_resources.FindResource("InkBrush");
        var element = new Border
        {
            BorderBrush = (Brush)_resources.FindResource("LineBrush"), BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 3, 0, 3), Child = viewer,
        };
        _items.Children.Add(element);
        _notes.Add((text, element));
        Update();
    }

    // the answer was also streamed as the last note: keep it once, as the answer
    public void DropNote(string text)
    {
        int i = _notes.FindLastIndex(n => n.Text.Trim() == text.Trim());
        if (i < 0) return;
        _items.Children.Remove(_notes[i].Element);
        _notes.RemoveAt(i);
        Update();
    }

    private void Update()
    {
        _header.Inlines.Clear();
        var parts = new List<string>();
        if (_steps > 0) parts.Add($"{_steps} step{(_steps == 1 ? "" : "s")}");
        if (_notes.Count > 0) parts.Add($"{_notes.Count} note{(_notes.Count == 1 ? "" : "s")}");
        _header.Inlines.Add(new Run("⚙ " + (parts.Count > 0 ? string.Join(" · ", parts) : "working")) { FontWeight = FontWeights.Bold, Foreground = _accent });
        if (_steps > 0 && _last.Length > 0) _header.Inlines.Add(new Run($"  ·  last: {_last}"));
        Block.Child.Visibility = _steps == 0 && _notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
