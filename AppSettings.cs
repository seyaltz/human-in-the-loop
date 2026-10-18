using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DevBreak;

/// <summary>The few choices kept in settings.json next to the exe; writing one value keeps the others.</summary>
public static class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static string? Get(string key) => Read().TryGetValue(key, out string? value) ? value : null;

    public static void Set(string key, string value)
    {
        Dictionary<string, string> all = Read();
        all[key] = value;
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(all));
    }

    private static Dictionary<string, string> Read()
    {
        if (!File.Exists(SettingsPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
