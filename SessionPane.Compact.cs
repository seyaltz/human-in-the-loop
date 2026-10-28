using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DevBreak;

public partial class SessionPane
{
    // the CLI compacting the conversation (measured, stream-json): {"subtype":"status","status":"compacting"} when it
    // starts, {"subtype":"status","status":null,"compact_result":"success"} when it ends, then {"subtype":
    // "compact_boundary","compact_metadata":{"trigger","pre_tokens","post_tokens","duration_ms"}}. Nothing else shows
    // while it runs (30-60 s on a big session), so the chat gets a live line for it.
    private void OnCompactEvent(SessionState state, string subtype, JsonElement line)
    {
        if (subtype == "status" && Str(line, "status") == "compacting")
        {
            state.Compacting?.Stop();
            state.Compacting = new CompactLine(state, this);
        }
        else if (subtype == "status" && Str(line, "compact_result") is { Length: > 0 } result && result != "success")
        {
            (state.Compacting ?? new CompactLine(state, this)).End("✗ Compaction " + result + (Str(line, "compact_error") is { Length: > 0 } why ? ": " + why : ""));
            state.Compacting = null;
        }
        else if (subtype == "compact_boundary")
        {
            line.TryGetProperty("compact_metadata", out JsonElement meta);
            DateTime now = DateTime.Now;
            state.Compactions.Add(now);
            state.Compactions.RemoveAll(at => now - at > Compaction.Window);
            (state.Compacting ?? new CompactLine(state, this)).End(Compaction.Done(Num(meta, "pre_tokens"), Num(meta, "post_tokens"),
                Num(meta, "duration_ms"), Str(meta, "trigger"), state.Compactions.Count));
            state.Compacting = null;
            state.Notify();
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}

/// <summary>The words of the compaction line.</summary>
internal static class Compaction
{
    /// <summary>Compactions this close together are counted as one run of them.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public static string Running(TimeSpan elapsed) => $"⧗ Compacting the conversation · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";

    /// <param name="recent">compactions in the last <see cref="Window"/>, this one included</param>
    public static string Done(double pre, double post, double ms, string trigger, int recent)
    {
        string size = pre > 0 && post > 0 ? $" {K(pre)} → {K(post)}" : "";
        string took = ms > 0 ? $" in {Math.Round(ms / 1000):0} s" : "";
        string why = trigger == "manual" ? " (/compact)" : trigger.Length > 0 ? " (auto)" : "";
        string text = $"✓ Compacted{size}{took}{why}";
        if (recent >= 2) text += $" · {recent} in the last {Window.TotalMinutes:0} min";
        if (recent >= 3)
        {
            text += " - a loop: what loads again after each one fills the context back to the limit, so little work gets done " +
                "between them. Start fresh from a handover, or raise autoCompactWindow (a running session keeps the old value until its process restarts).";
        }
        return text;
    }

    private static string K(double tokens) => $"{Math.Round(tokens / 1000):0}k";
}

/// <summary>One compaction in the chat: a line that counts up while it runs and then says what it did.</summary>
internal sealed class CompactLine
{
    private readonly DateTime _start = DateTime.Now;
    private readonly Run _text;
    private readonly DispatcherTimer _timer;

    public CompactLine(SessionState state, FrameworkElement resources)
    {
        _text = new Run(Compaction.Running(TimeSpan.Zero));
        state.Document.Blocks.Add(new Paragraph(_text)
        {
            FontSize = 11.5, Foreground = (Brush)resources.FindResource("VioletDarkBrush"), Margin = new Thickness(0, 0, 0, 8),
        });
        state.ShownIn?.Transcript.ScrollToEnd();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _text.Text = Compaction.Running(DateTime.Now - _start);
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void End(string text)
    {
        _timer.Stop();
        _text.Text = text;
    }
}
