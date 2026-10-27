using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>A command the CLI takes after "/": a built-in, a skill or a command of the user's, a project's or a plugin's.</summary>
public sealed record SlashCommand(string Name, string Description, string ArgumentHint, IReadOnlyList<string> Aliases, bool Builtin)
{
    /// <summary>Where it comes from: "built-in", the plugin's name, or "yours" (the user's or the project's).</summary>
    public string Source => Builtin ? "built-in" : Name.Contains(':') ? Name[..Name.IndexOf(':')] : "yours";
}

/// <summary>
/// The "/" commands the user's claude CLI takes in a folder - its own list, the one the CLI's "/" menu shows, with each
/// one's description and argument hint. It comes from the reply to the stream-json initialize request: measured, 247
/// commands in 1.8 s, with no model call and no session file written. Read once per folder while Dev break runs.
/// </summary>
public static class SlashCommands
{
    // measured: a -p turn's init line names these as terminal_slash_commands - they do nothing in a turn run here
    private static readonly HashSet<string> TerminalOnly = ["doctor", "color", "focus", "reload-plugins"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<string, Task<IReadOnlyList<SlashCommand>>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder's commands; read the first time (again after a failed read).</summary>
    public static Task<IReadOnlyList<SlashCommand>> For(string folder)
    {
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(folder, out Task<IReadOnlyList<SlashCommand>>? task) || task.IsFaulted || task.IsCanceled)
            {
                Loaded[folder] = task = Task.Run(() => ReadAsync(folder));
            }
            return task;
        }
    }

    /// <summary>
    /// The commands matching what was typed after "/", best first: a name or an alias that starts with it, then a plugin's
    /// command whose part after "plugin:" does, then a name that holds it, then a description that holds it.
    /// </summary>
    public static List<SlashCommand> Match(IReadOnlyList<SlashCommand> commands, string typed)
    {
        string q = typed.Trim();
        int Rank(SlashCommand c)
        {
            if (q.Length == 0) return 0;
            string local = c.Name.Contains(':') ? c.Name[(c.Name.IndexOf(':') + 1)..] : c.Name;
            if (c.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) || c.Aliases.Any(a => a.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 0;
            if (local.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
            if (c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
            if (c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)) return 3;
            return -1;
        }
        return commands.Select(c => (Command: c, Rank: Rank(c))).Where(m => m.Rank >= 0)
            .OrderBy(m => m.Rank).ThenBy(m => m.Command.Name, StringComparer.OrdinalIgnoreCase).Select(m => m.Command).ToList();
    }

    /// <summary>The command a message starts with ("/name ..."), if the CLI knows it by that name or an alias.</summary>
    public static SlashCommand? Find(IReadOnlyList<SlashCommand> commands, string message)
    {
        if (!message.StartsWith('/')) return null;
        int end = message.IndexOfAny([' ', '\t', '\r', '\n']);
        string name = end < 0 ? message[1..] : message[1..end];
        return commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    // starts claude the way a session's open process starts, asks it to initialize, keeps the commands of its reply, ends it
    private static async Task<IReadOnlyList<SlashCommand>> ReadAsync(string folder)
    {
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose" })
        {
            start.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start claude");
        try
        {
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            await process.StandardInput.WriteLineAsync("""{"type":"control_request","request_id":"devbreak_commands","request":{"subtype":"initialize"}}""");
            await process.StandardInput.FlushAsync();
            using var limit = new CancellationTokenSource(Timeout);
            while (await process.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                if (line.StartsWith('{') && line.Contains("\"control_response\"", StringComparison.Ordinal)) return Parse(line);
            }
            throw new InvalidOperationException("claude exited before it listed its commands");
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    // {"type":"control_response","response":{"subtype":"success","response":{"commands":[{"name","description","argumentHint","aliases"?,"builtin"?}]}}}
    private static List<SlashCommand> Parse(string line)
    {
        var commands = new List<SlashCommand>();
        using JsonDocument doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("response", out JsonElement outer) || !outer.TryGetProperty("response", out JsonElement inner)
            || !inner.TryGetProperty("commands", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return commands;
        foreach (JsonElement c in list.EnumerateArray())
        {
            string name = Str(c, "name");
            if (name.Length == 0 || TerminalOnly.Contains(name)) continue;
            string[] aliases = c.TryGetProperty("aliases", out JsonElement a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : [];
            bool builtin = c.TryGetProperty("builtin", out JsonElement b) && b.ValueKind == JsonValueKind.True;
            commands.Add(new SlashCommand(name, Str(c, "description"), Str(c, "argumentHint"), aliases, builtin));
        }
        return commands;
    }

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
}
