using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DevBreak;

/// <summary>A session's permission mode, model and effort as the user set them in its pane (null: the CLI's own).</summary>
public sealed class SessionSetting
{
    public string Mode { get; set; } = SessionSettings.Bypass;
    public string? Model { get; set; }
    public string? Effort { get; set; }

    /// <summary>
    /// The conversation rewinds made here: the message cut from and the line the session went on from (null: its
    /// start). The file keeps the cut branch; reading the session back leaves it out.
    /// </summary>
    public List<RewindCut> Rewinds { get; set; } = [];
}

public sealed record RewindCut(string From, string? At);

/// <summary>
/// The mode, model and effort of each session, kept next to the app in session-settings.json (the user's own data,
/// never in the repo) so a session reopens as it was left. A session never set starts in Bypass - Dev break's
/// behaviour before modes existed, the user's choice - with the CLI's default model and effort.
/// </summary>
public static class SessionSettings
{
    public const string Bypass = "bypassPermissions";

    /// <summary>The modes in the order Shift+Tab cycles them, with their labels.</summary>
    public static readonly (string Mode, string Label)[] Modes =
    [
        (Bypass, "Bypass"), ("default", "Ask"), ("acceptEdits", "Accept edits"), ("plan", "Plan"),
    ];

    /// <summary>The effort levels the CLI takes (--effort); null is "auto", the CLI's own.</summary>
    public static readonly string?[] Efforts = [null, "low", "medium", "high", "xhigh"];

    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "session-settings.json");
    private static Dictionary<string, SessionSetting>? _all;

    private static Dictionary<string, SessionSetting> All
    {
        get
        {
            if (_all != null) return _all;
            try
            {
                _all = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, SessionSetting>>(File.ReadAllText(FilePath)) ?? []
                    : [];
            }
            catch (Exception)
            {
                _all = [];
            }
            return _all;
        }
    }

    public static SessionSetting Of(string id)
    {
        lock (All)
        {
            if (!All.TryGetValue(id, out SessionSetting? setting)) All[id] = setting = new SessionSetting();
            return setting;
        }
    }

    /// <summary>The session's rewind cuts, without adding a setting for it (safe off the UI thread).</summary>
    public static List<RewindCut> RewindsOf(string id)
    {
        lock (All) return All.TryGetValue(id, out SessionSetting? setting) ? [.. setting.Rewinds] : [];
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // kept for this run only
        }
    }

    public static string LabelOf(string mode) => Array.Find(Modes, m => m.Mode == mode).Label ?? mode;
}
