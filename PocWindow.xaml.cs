using System;
using System.Threading;
using System.Windows;

namespace DevBreak;

/// <summary>Shows a question's proof of concept, with buttons to copy its code and run it on this machine.</summary>
public partial class PocWindow : Window
{
    private readonly PocRunner _runner;
    private CancellationTokenSource? _running;

    public PocWindow(string topic, Question question)
    {
        InitializeComponent();
        Title = $"Dev break - proof of concept - {topic}";
        TopicText.Text = $"Proof of concept: {topic}";
        PocBox.Document = CodeRenderer.Render(question.Poc);
        _runner = new PocRunner(question.Poc);
        RunButton.IsEnabled = _runner.NotRunnable == null;
        RunButton.ToolTip = _runner.NotRunnable ?? $"Run this {_runner.Language} code on this machine";
        RunNote.Text = _runner.NotRunnable ?? "Run executes the code on your machine, in a temp folder.";
        if (question.Check is { } check)
        {
            string tries = check.Attempts == 1 ? "" : $" after {check.Attempts - 1} fix(es) by Claude";
            RunNote.Text = check.Passed
                ? $"Checked: ran without errors on this machine{tries}. Its output is below; Run runs it again."
                : $"Could not get it to run cleanly here ({check.Attempts} tries). The last output is below.";
            ShowOutput(check.Output);
            ExitText.Text = check.Passed ? "from the check" : "from the last try";
        }
        Closed += (_, _) => _running?.Cancel();
    }

    private void ShowOutput(string text)
    {
        OutputPanel.Visibility = Visibility.Visible;
        OutputRow.Height = new GridLength(1, GridUnitType.Star);
        OutputBox.Text = text;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_runner.Code);
        CopiedText.Visibility = Visibility.Visible;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        // the same button stops a run
        if (_running != null)
        {
            _running.Cancel();
            return;
        }

        _running = new CancellationTokenSource();
        ShowOutput("");
        ExitText.Text = "running...";
        RunningBar.Visibility = Visibility.Visible;
        RunButton.Content = "Stop";
        try
        {
            int exit = await _runner.RunAsync(text => Dispatcher.BeginInvoke(() =>
            {
                OutputBox.AppendText(text);
                OutputBox.ScrollToEnd();
            }), _running.Token);
            ExitText.Text = exit == -1 ? "" : $"exit code {exit}";
        }
        catch (Exception ex)
        {
            OutputBox.AppendText("\nCould not run it: " + ex.Message);
            ExitText.Text = "";
        }
        _running = null;
        RunningBar.Visibility = Visibility.Collapsed;
        RunButton.Content = "Run";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
