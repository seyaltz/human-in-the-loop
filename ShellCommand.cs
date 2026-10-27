using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>
/// The terminal's "!" (bash mode) for a Sessions pane: measured, "!cmd" sent to claude -p is not run as a command -
/// Claude reads it and runs Bash itself, a model turn - so Dev break runs it, in the session's folder, the way the
/// CLI's Bash tool does on Windows (Git Bash when it is there, else PowerShell), and hands the output to the next
/// message, as the CLI puts it into the conversation.
/// </summary>
public static class ShellCommand
{
    private const int MaxOutput = 4000;
    private const string Start = "[I ran: ";
    private const string End = "[/I ran]";

    private static readonly string? Bash = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
    }.FirstOrDefault(File.Exists);

    /// <summary>The shell a command runs in, for the hint.</summary>
    public static string ShellName => Bash != null ? "Git Bash" : "PowerShell";

    /// <summary>Runs a command in the folder; its exit code and output (stdout and stderr in order, the last 4,000 characters).</summary>
    public static async Task<(int Code, string Output)> RunAsync(string folder, string command, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(Bash ?? "powershell.exe")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Bash != null)
        {
            start.ArgumentList.Add("-lc");
        }
        else
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
        }
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start the shell");
        var output = new StringBuilder();
        void Take(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (output)
            {
                output.AppendLine(e.Data);
                if (output.Length > MaxOutput * 4) output.Remove(0, output.Length - MaxOutput * 2);
            }
        }
        process.OutputDataReceived += Take;
        process.ErrorDataReceived += Take;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        string text;
        lock (output) text = output.ToString().TrimEnd();
        if (text.Length > MaxOutput) text = "…" + text[^MaxOutput..];
        return (process.ExitCode, text);
    }

    /// <summary>A command and its output as the next message carries them, before what the user wrote.</summary>
    public static string Block(string command, int code, string output) =>
        $"{Start}{command}]\n```\n{output}\n```\nexit code {code}\n{End}\n\n";

    private static readonly Regex Ran = new(@"\A\[I ran: (?<cmd>[^\n]*)\]\n```\n(?<out>.*?)\n```\nexit code (?<code>-?\d+)\n\[/I ran\]\n\n", RegexOptions.Singleline);

    /// <summary>The commands a message starts with and the rest of it; a message without them comes back whole.</summary>
    public static (List<(string Command, int Code, string Output)> Ran, string Said) Split(string message)
    {
        var ran = new List<(string, int, string)>();
        string rest = message.Replace("\r\n", "\n");
        while (Ran.Match(rest) is { Success: true } m)
        {
            ran.Add((m.Groups["cmd"].Value, int.Parse(m.Groups["code"].Value), m.Groups["out"].Value));
            rest = rest[m.Length..];
        }
        return ran.Count == 0 ? (ran, message) : (ran, rest);
    }
}

/// <summary>
/// The files and folders an "@" can name in a session's folder, as relative paths: the git-tracked files when the
/// folder is a git repo (what the CLI suggests too), else a walk that skips build and tool folders. Read once per
/// folder while the app runs, at most 5,000.
/// </summary>
public static class FileMentions
{
    private const int Max = 5000;
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build", "target", "publish" };
    private static readonly Dictionary<string, Task<List<string>>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    public static Task<List<string>> For(string folder)
    {
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(folder, out Task<List<string>>? task) || task.IsFaulted)
            {
                Loaded[folder] = task = Task.Run(() => Read(folder));
            }
            return task;
        }
    }

    private static List<string> Read(string folder)
    {
        List<string> files = Directory.Exists(Path.Combine(folder, ".git")) ? GitFiles(folder) : [];
        if (files.Count == 0) files = Walk(folder);
        // the folders the files are in, as "dir/"
        IEnumerable<string> dirs = files.SelectMany(Parents).Distinct(StringComparer.OrdinalIgnoreCase);
        return [.. dirs.Concat(files).Take(Max)];
    }

    private static IEnumerable<string> Parents(string path)
    {
        for (int i = path.IndexOf('/'); i > 0; i = path.IndexOf('/', i + 1)) yield return path[..(i + 1)];
    }

    private static List<string> GitFiles(string folder)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("ls-files");
            using Process process = Process.Start(start)!;
            var files = new List<string>();
            while (process.StandardOutput.ReadLine() is { } line && files.Count < Max) files.Add(line);
            process.WaitForExit(10000);
            return files;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<string> Walk(string folder)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0 && files.Count < Max)
        {
            string dir = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    files.Add(Path.GetRelativePath(folder, file).Replace('\\', '/'));
                    if (files.Count >= Max) break;
                }
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if (!Skip.Contains(Path.GetFileName(sub))) pending.Push(sub);
                }
            }
            catch (Exception)
            {
                // a folder we may not read
            }
        }
        return files;
    }

    /// <summary>The paths matching what was typed after "@": the name starting with it first, then the path holding it.</summary>
    public static List<string> Match(IReadOnlyList<string> paths, string typed)
    {
        string q = typed.Replace('\\', '/');
        int Rank(string p)
        {
            if (q.Length == 0) return p.Count(c => c == '/') <= 1 ? 0 : 1;
            string name = p.TrimEnd('/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
            if (p.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
            return p.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
        }
        return paths.Select(p => (Path: p, Rank: Rank(p))).Where(m => m.Rank >= 0)
            .OrderBy(m => m.Rank).ThenBy(m => m.Path.Length).Select(m => m.Path).ToList();
    }
}
