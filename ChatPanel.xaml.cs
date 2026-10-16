using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace DevBreak;

/// <summary>
/// A follow-up conversation with Claude, shown beside the quiz or the TL;DR in the main window: about one question
/// and its answer, or about a TL;DR.
/// </summary>
public partial class ChatPanel : UserControl
{
    private readonly Func<IReadOnlyList<(bool IsMine, string Text)>, Task<string>> _reply;
    private readonly List<(bool IsMine, string Text)> _history = [];

    /// <summary>The close button was pressed; the owner removes the panel.</summary>
    public event Action? CloseRequested;

    public ChatPanel(string topic, Question question, string mine)
        : this(topic, "Ask anything about this question or its answer: why, what if, a deeper example, or check an idea.",
               history => QuestionGenerator.ChatAsync(question, mine, history))
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
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private async void Send()
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0 || !SendButton.IsEnabled) return;
        InputBox.Clear();
        _history.Add((true, text));
        AddMessage(true, text);
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

    private void AddMessage(bool isMine, string text)
    {
        FlowDocument document = Transcript.Document;
        document.Blocks.Add(new Paragraph(new Run(isMine ? "You" : "Claude") { FontWeight = FontWeights.Bold })
        {
            Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 10, 0, 2),
            Foreground = (System.Windows.Media.Brush)FindResource(isMine ? "VioletBrush" : "CoralInkBrush"),
        });
        CodeRenderer.Append(document, text);
        Transcript.ScrollToEnd();
    }
}
