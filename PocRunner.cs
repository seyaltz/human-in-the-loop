using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>
/// Runs a proof of concept's first code block on this machine: the code is written to a fresh temp folder and run
/// with the language's own tool (java, python, node, bash, dotnet). Languages without a local tool cannot run.
/// </summary>
public sealed class PocRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);
    private static Task<string>? _runtimes;
    private static readonly Regex Fence = new(@"```[ \t]*([^\s`]*)[^\n]*\n([\s\S]*?)```");
    private static readonly Regex JavaClass = new(@"public\s+(?:final\s+)?class\s+(\w+)");

    private readonly string _fileName;
    private readonly string _command;
    private readonly IReadOnlyList<string> _args;

    /// <summary>The code of the first fenced block, or the whole proof of concept if it has none.</summary>
    public string Code { get; }

    /// <summary>The block's language as Claude wrote it, e.g. "java"; empty if it gave none.</summary>
    public string Language { get; }

    /// <summary>Why the code cannot run here, or null when it can.</summary>
    public string? NotRunnable { get; }

    public PocRunner(string poc)
    {
        Match block = Fence.Match(poc);
        Code = block.Success ? block.Groups[2].Value : poc;
        Language = block.Success ? block.Groups[1].Value.ToLowerInvariant() : "";

        (string FileName, string? Command, string[] Args)? how = Language switch
        {
            "java" => ($"{JavaClassName()}.java", Find("java"), ["{file}"]),
            "python" or "py" or "python3" => ("poc.py", Find("python") ?? Find("py"), ["-I", "{file}"]),
            "javascript" or "js" or "node" => (IsEsm() ? "poc.mjs" : "poc.js", Find("node"), ["{file}"]),
            "bash" or "sh" or "shell" => ("poc.sh", Find("bash", @"C:\Program Files\Git\bin\bash.exe"), ["{file}"]),
            "csharp" or "cs" or "c#" => ("poc.cs", Dotnet(), ["run", "{file}"]),
            _ => null,
        };
        if (how is not { } h)
        {
            _fileName = _command = "";
            _args = [];
            NotRunnable = Language.Length == 0 ? "The proof of concept has no code block to run."
                : $"Running {Language} is not supported (only Java, Python, JavaScript, Bash and C#).";
            return;
        }
        _fileName = h.FileName;
        _command = h.Command ?? "";
        _args = h.Args;
        NotRunnable = h.Command == null ? $"No {Language} runtime found on this machine." : null;
    }

    /// <summary>
    /// Runs the code and streams its output (stdout and stderr together) to <paramref name="output"/>.
    /// Returns the exit code, or -1 when it was stopped: after <paramref name="timeout"/> (default 90 seconds)
    /// or when <paramref name="cancel"/> fires.
    /// </summary>
    public async Task<int> RunAsync(Action<string> output, CancellationToken cancel, TimeSpan? timeout = null)
    {
        TimeSpan limitAfter = timeout ?? DefaultTimeout;
        if (NotRunnable != null) throw new InvalidOperationException(NotRunnable);

        string folder = Path.Combine(Path.GetTempPath(), "devbreak-poc", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, _fileName);
        await File.WriteAllTextAsync(file, Code, new UTF8Encoding(false), cancel);

        var start = new ProcessStartInfo(_command)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in _args)
        {
            start.ArgumentList.Add(arg == "{file}" ? file : arg);
        }
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";

        output($"> {Path.GetFileName(_command)} {string.Join(" ", start.ArgumentList).Replace(folder + Path.DirectorySeparatorChar, "")}\n");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {_command}");
        process.StandardInput.Close();
        Task stdout = Pump(process.StandardOutput, output);
        Task stderr = Pump(process.StandardError, output);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(limitAfter);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            output(cancel.IsCancellationRequested ? "\n[stopped]\n" : $"\n[stopped after {limitAfter.TotalSeconds:0} seconds]\n");
            return -1;
        }
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    /// <summary>Runs the code and collects its output; for checking a proof of concept before anyone sees it.</summary>
    public async Task<(int Exit, string Output)> CaptureAsync(TimeSpan timeout)
    {
        var output = new StringBuilder();
        // stdout and stderr are pumped on different threads
        int exit = await RunAsync(text => { lock (output) output.Append(text); }, CancellationToken.None, timeout);
        lock (output) return (exit, output.ToString());
    }

    /// <summary>
    /// The runtimes on this machine with their versions, e.g. "Java 17.0.14, Python 3.11.9", so a proof of concept can
    /// be written for them. Found once per run of the app.
    /// </summary>
    public static Task<string> RuntimesAsync() => _runtimes ??= FindRuntimesAsync();

    private static async Task<string> FindRuntimesAsync()
    {
        Task<string?>[] found =
        [
            VersionAsync("Java", Find("java"), "-version", @"version ""([^""]+)"""),
            VersionAsync("Python", Find("python") ?? Find("py"), "--version", @"Python (\S+)"),
            VersionAsync("JavaScript (Node)", Find("node"), "--version", @"v(\S+)"),
            VersionAsync("Bash", Find("bash", @"C:\Program Files\Git\bin\bash.exe"), "--version", @"version (\S+)"),
            VersionAsync("C# (.NET SDK, file-based app run with `dotnet run poc.cs`)", Dotnet(), "--version", @"^(\d+\.\S+)"),
        ];
        var versions = new List<string>();
        foreach (Task<string?> version in found)
        {
            if (await version is { } v) versions.Add(v);
        }
        return string.Join(", ", versions);
    }

    /// <summary>"Name version" from running <paramref name="exe"/> <paramref name="arg"/>, or null if it is missing or fails.</summary>
    private static async Task<string?> VersionAsync(string name, string? exe, string arg, string pattern)
    {
        if (exe == null) return null;
        try
        {
            var start = new ProcessStartInfo(exe, arg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (process.ExitCode != 0) return null;
            Match match = Regex.Match(await stdout + await stderr, pattern, RegexOptions.Multiline);
            return match.Success ? $"{name} {match.Groups[1].Value}" : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task Pump(StreamReader reader, Action<string> output)
    {
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            output(new string(buffer, 0, read));
        }
    }

    // the java source launcher runs the first class in the file; naming the file after the public class keeps javac happy
    private string JavaClassName()
    {
        Match match = JavaClass.Match(Code);
        return match.Success ? match.Groups[1].Value : "Main";
    }

    private bool IsEsm() => Regex.IsMatch(Code, @"^\s*(import\s|export\s)", RegexOptions.Multiline);

    /// <summary>A file-based C# app needs the .NET 10 SDK: the one in DOTNET_ROOT first, then dotnet on the PATH.</summary>
    private static string? Dotnet()
    {
        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        string? inRoot = root == null ? null : Path.Combine(root, "dotnet.exe");
        return inRoot != null && File.Exists(inRoot) ? inRoot : Find("dotnet");
    }

    /// <summary>The first of <paramref name="preferred"/> that exists, else <paramref name="name"/>.exe on the PATH.</summary>
    private static string? Find(string name, params string[] preferred)
    {
        foreach (string path in preferred)
        {
            if (File.Exists(path)) return path;
        }
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            string candidate = Path.Combine(dir.Trim('"'), name + ".exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
