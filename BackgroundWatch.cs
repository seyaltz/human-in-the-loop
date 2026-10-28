using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevBreak;

/// <summary>
/// What a background task is doing, read on this machine - its output file, and the Cloud Build or Cloud Deploy rollout
/// it waits on - so its card in the chat moves without asking Claude anything.
/// </summary>
public static class BackgroundWatch
{
    private static readonly Regex Uuid = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.Compiled);

    /// <summary>
    /// The task's output file, or null while there is none. Measured: the CLI writes it to
    /// <c>%TEMP%\claude\&lt;project&gt;\&lt;session&gt;\tasks\&lt;task id&gt;.output</c>; the project part is the folder, encoded.
    /// </summary>
    public static string? OutputFile(string claudeTemp, string sessionId, string taskId)
    {
        if (!Directory.Exists(claudeTemp)) return null;
        foreach (string project in Directory.EnumerateDirectories(claudeTemp))
        {
            string file = Path.Combine(project, sessionId, "tasks", taskId + ".output");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>The last lines of the output, without blanks; a progress line (rewritten with \r) as it ends.</summary>
    public static List<string> Tail(string text, int lines) =>
        [.. text.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => l[(l.LastIndexOf('\r') + 1)..].Trim()).Where(l => l.Length > 0).TakeLast(lines)];

    /// <summary>The last bytes of a file another process is still writing.</summary>
    public static string ReadEnd(string file, int bytes = 16 * 1024)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Max(0, stream.Length - bytes), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Flag(string command, string name) =>
        Regex.Match(command, $@"--{name}[= ]""?([^\s""';)]+)") is { Success: true } m ? m.Groups[1].Value : "";

    /// <summary>The Cloud Build a task runs or waits on: its id (in the command, else in the output), project and region.</summary>
    public static (string Id, string Project, string Region)? CloudBuild(string command, string output)
    {
        if (!command.Contains("gcloud builds", StringComparison.Ordinal)) return null;
        Match id = Uuid.Match(command);
        if (!id.Success) id = Uuid.Match(output);
        return id.Success ? (id.Value, Flag(command, "project"), Flag(command, "region")) : null;
    }

    /// <summary>The steps of a build (<c>gcloud builds describe --format=json</c>): by id, else the builder's last name.</summary>
    public static List<(string Name, string Status)> BuildSteps(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("steps", out JsonElement steps)) return [];
        return [.. steps.EnumerateArray().Select(s => (
            s.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? "" :
            s.TryGetProperty("name", out JsonElement n) ? (n.GetString() ?? "").Split('/')[^1] : "step",
            s.TryGetProperty("status", out JsonElement st) ? st.GetString() ?? "QUEUED" : "QUEUED"))];
    }

    /// <summary>The Cloud Deploy rollout a task waits on; a <c>$var</c> rollout name is read from where it was set.</summary>
    public static (string Rollout, string Release, string Pipeline, string Project, string Region)? Rollout(string command)
    {
        Match m = Regex.Match(command, @"gcloud deploy rollouts describe\s+(\S+)");
        if (!m.Success) return null;
        string rollout = m.Groups[1].Value;
        if (rollout.StartsWith('$'))
        {
            string name = rollout.TrimStart('$').Trim('{', '}', '"');
            rollout = Regex.Match(command, $@"\b{Regex.Escape(name)}=""?([^\s;""]+)") is { Success: true } v ? v.Groups[1].Value : "";
        }
        return rollout.Length > 0
            ? (rollout, Flag(command, "release"), Flag(command, "delivery-pipeline"), Flag(command, "project"), Flag(command, "region"))
            : null;
    }

    public static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
