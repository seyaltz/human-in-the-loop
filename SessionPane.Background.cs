using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DevBreak;

public partial class SessionPane
{
    // a background task started: its card goes in the chat and follows it - the output file every 2 s, the Cloud Build
    // or rollout it waits on every 15 s - all read on this machine, nothing asked of Claude
    private void ShowBackground(SessionState state, string id, string toolUseId, string what)
    {
        if (state.Cards.ContainsKey(id)) return;
        string command = state.Log?.Steps.FirstOrDefault(s => s.Id == toolUseId)?.Command ?? "";
        var card = new BackgroundCard(state.Info.Id, id, what, command, this);
        state.Cards[id] = card;
        state.Document.Blocks.Add(card.Block);
        state.ShownIn?.Transcript.ScrollToEnd();
    }

    private static void EndBackground(SessionState state, string id, string status, string summary)
    {
        if (state.Cards.TryGetValue(id, out BackgroundCard? card)) card.End(status, summary);
    }
}

/// <summary>
/// One background task in the chat: folded to a live line ("⧗ build the client · 3:20 · Step 4/9 : RUN npm ci"), open
/// to its command, the last lines of its output and the steps of the build or rollout it waits on.
/// </summary>
internal sealed class BackgroundCard
{
    private const int TailLines = 12;
    private static readonly string ClaudeTemp = Path.Combine(Path.GetTempPath(), "claude");

    private readonly string _session, _task, _what, _command;
    private readonly DateTime _start = DateTime.Now;
    private DateTime? _endAt;
    private readonly TextBlock _header, _output, _cloud;
    private readonly DispatcherTimer _timer;
    private string? _file;
    private string _last = "", _cloudLine = "", _endText = "";
    private DateTime _cloudAt = DateTime.MinValue;
    private bool _polling, _ended;

    public BlockUIContainer Block { get; }

    public BackgroundCard(string session, string task, string what, string command, FrameworkElement resources)
    {
        (_session, _task, _what, _command) = (session, task, what, command);
        Brush Res(string key) => (Brush)resources.FindResource(key);
        var mono = new FontFamily("Cascadia Mono, Consolas");
        _header = new TextBlock { FontFamily = mono, FontSize = 12, Foreground = Res("VioletDarkBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        _output = new TextBlock { FontFamily = mono, FontSize = 11.5, Foreground = Res("InkBrush"), TextWrapping = TextWrapping.Wrap };
        _cloud = new TextBlock { FontFamily = mono, FontSize = 11.5, Foreground = Res("InkBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Margin = new Thickness(20, 4, 0, 2) };
        if (command.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                FontFamily = mono, FontSize = 11.5, Foreground = Res("InkSoftBrush"), TextWrapping = TextWrapping.Wrap,
                Text = "$ " + command, Margin = new Thickness(0, 0, 0, 4),
            });
        }
        body.Children.Add(_cloud);
        body.Children.Add(_output);
        var border = new Border
        {
            Background = Res("VioletTintBrush"), BorderBrush = Res("VioletBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4, 10, 6),
            Child = new Expander { Header = _header, Content = body, IsExpanded = false },
        };
        Block = new BlockUIContainer(border) { Margin = new Thickness(0, 4, 0, 8) };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    // measured: task_updated ends it first, with no summary; task_notification follows with one ("... (exit code 0)")
    public void End(string status, string summary)
    {
        if (_ended && summary.Length == 0) return;
        bool first = !_ended;
        _ended = true;
        _endAt ??= DateTime.Now;
        _timer.Stop();
        _endText = (status == "completed" ? "✓ " : "✗ ") + (summary.Length > 0 ? summary : status);
        Refresh();
        if (first) _ = PollCloud(force: true);
    }

    private void Refresh()
    {
        try
        {
            _file ??= BackgroundWatch.OutputFile(ClaudeTemp, _session, _task);
            if (_file != null)
            {
                List<string> tail = BackgroundWatch.Tail(BackgroundWatch.ReadEnd(_file), TailLines);
                _output.Text = string.Join("\n", tail);
                _last = tail.LastOrDefault() ?? "";
            }
        }
        catch (IOException)
        {
            // the CLI is writing it; the next tick reads it
        }
        string when = BackgroundWatch.Clock((_endAt ?? DateTime.Now) - _start);
        _header.Text = _ended
            ? $"{_endText} · {when}" + (_cloudLine.Length > 0 ? " · " + _cloudLine : "")
            : $"⧗ {_what} · {when}" + (_cloudLine.Length > 0 ? " · " + _cloudLine : _last.Length > 0 ? " · " + _last : "");
        if (!_ended && DateTime.Now - _cloudAt >= TimeSpan.FromSeconds(15)) _ = PollCloud(force: false);
    }

    // the build or rollout the task waits on, from gcloud on this machine
    private async Task PollCloud(bool force)
    {
        if (_polling && !force) return;
        _cloudAt = DateTime.Now;
        string output = "";
        try
        {
            output = _file != null ? BackgroundWatch.ReadEnd(_file) : "";
        }
        catch (IOException)
        {
        }
        string? query = null;
        Func<string, string>? show = null;
        if (BackgroundWatch.CloudBuild(_command, output) is { } build)
        {
            query = $"gcloud builds describe {build.Id}{Where(build.Project, build.Region)} --format=\"json(status,steps[].id,steps[].name,steps[].status)\"";
            show = json =>
            {
                List<(string Name, string Status)> steps = BackgroundWatch.BuildSteps(json);
                int done = steps.Count(s => s.Status == "SUCCESS");
                (string Name, string Status) now = steps.FirstOrDefault(s => s.Status is "WORKING" or "FAILURE" or "TIMEOUT" or "CANCELLED");
                _cloud.Text = "Cloud Build " + build.Id[..8] + "\n" + string.Join("\n", steps.Select(s => $"{Mark(s.Status)} {s.Name}  {s.Status.ToLowerInvariant()}"));
                return $"build {done}/{steps.Count}" + (now.Name != null ? $" · {now.Name} {now.Status.ToLowerInvariant()}" : "");
            };
        }
        else if (BackgroundWatch.Rollout(_command) is { } rollout)
        {
            query = $"gcloud deploy rollouts describe {rollout.Rollout} --release={rollout.Release} --delivery-pipeline={rollout.Pipeline}" +
                    $"{Where(rollout.Project, rollout.Region)} --format=\"value(state)\"";
            show = text =>
            {
                string state = text.Trim().Split('\n')[0].Trim();
                _cloud.Text = $"Cloud Deploy rollout {rollout.Rollout}: {state.ToLowerInvariant()}";
                return "rollout " + state.ToLowerInvariant();
            };
        }
        if (query == null || show == null) return;
        _polling = true;
        try
        {
            (int code, string result) = await ShellCommand.RunAsync(Path.GetTempPath(), query, CancellationToken.None);
            if (code == 0 && result.Trim().Length > 0)
            {
                _cloudLine = show(result);
                _cloud.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            // no gcloud, no login: the card keeps the output tail
        }
        finally
        {
            _polling = false;
        }
        Refresh();
    }

    private static string Where(string project, string region) =>
        (project.Length > 0 ? " --project=" + project : "") + (region.Length > 0 ? " --region=" + region : "");

    private static string Mark(string status) => status switch
    {
        "SUCCESS" => "✓",
        "WORKING" => "⧗",
        "QUEUED" or "PENDING" => "·",
        _ => "✗",
    };
}
