using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DevBreak;

/// <summary>
/// What Sessions learns: lessons drafted after a turn that needed retries or a correction, kept or dismissed by the
/// user, and how often the grader found each criterion violated per folder. Kept in session-lessons.json next to the
/// exe (the user's own data, git-ignored); a saved lesson itself goes into the user's Claude Code setup.
/// </summary>
public static class SessionLessons
{
    private static readonly object Gate = new();
    private static Store? _store;

    private static string FileName => Path.Combine(AppContext.BaseDirectory, "session-lessons.json");

    /// <summary>Counts one more violation of <paramref name="criterion"/> in <paramref name="folder"/>; returns the count.</summary>
    public static int CountViolation(string folder, string criterion)
    {
        lock (Gate)
        {
            Store store = Load();
            string key = folder.TrimEnd('\\').ToLowerInvariant() + "|" + criterion.Trim().ToLowerInvariant();
            int count = store.Violations.GetValueOrDefault(key) + 1;
            store.Violations[key] = count;
            Save(store);
            return count;
        }
    }

    private static Store Load()
    {
        if (_store != null) return _store;
        try
        {
            _store = File.Exists(FileName) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(FileName)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _store = null;
        }
        return _store ??= new Store();
    }

    private static void Save(Store store)
    {
        try
        {
            File.WriteAllText(FileName, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory for this run
        }
    }

    public sealed class Store
    {
        public Dictionary<string, int> Violations { get; set; } = [];
    }
}
