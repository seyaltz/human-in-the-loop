using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;

namespace DevBreak;

/// <summary>A follow-up conversation with Claude about one question and its answer.</summary>
public partial class ChatWindow : Window
{
    private readonly Question _question;
    private readonly string _mine;
    private readonly List<(bool IsMine, string Text)> _history = [];

    public ChatWindow(string topic, Question question, string mine)
    {
        _question = question;
        _mine = mine;
        InitializeComponent();
        Title = $"Dev break - discuss - {topic}";
        TopicText.Text = $"Discuss: {topic}";
        Transcript.Document = CodeRenderer.Render("");
        AddMessage(false, "Ask anything about this question or its answer: why, what if, a deeper example, or check an idea.");
        Loaded += (_, _) => InputBox.Focus();
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
        string text = InputBox.Text.Trim();
        if (text.Length == 0 || !SendButton.IsEnabled) return;
        InputBox.Clear();
        _history.Add((true, text));
        AddMessage(true, text);
        SetThinking(true);
        try
        {
            string reply = (await QuestionGenerator.ChatAsync(_question, _mine, _history)).Trim();
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
        document.Blocks.Add(new Paragraph(new Run(isMine ? "You" : "Claude") { FontWeight = FontWeights.SemiBold })
        {
            Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 8, 0, 2),
            Foreground = isMine ? System.Windows.Media.Brushes.SteelBlue : System.Windows.Media.Brushes.SeaGreen,
        });
        CodeRenderer.Append(document, text);
        Transcript.ScrollToEnd();
    }
}
