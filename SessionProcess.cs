using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>How a session's process is started: its folder and id, new or resumed, and the settings it starts with.</summary>
/// <param name="Mode">the permission mode: bypassPermissions, default, acceptEdits or plan</param>
/// <param name="Effort">low, medium, high, xhigh, or null for the CLI's own (auto)</param>
/// <param name="ResumeAt">resume the session cut after this message (a conversation rewind)</param>
/// <param name="Fork">resume into a new session (a branch), keeping the old one as it is</param>
/// <param name="ForkId">with Fork: the new session's id (measured: --fork-session takes --session-id for it)</param>
public sealed record SessionLaunch(string Folder, string Id, bool IsNew, string Mode, string? Model, string? Effort,
    string? ResumeAt = null, bool Fork = false, string? ForkId = null);

/// <summary>A tool Claude wants to run that the CLI asks about (can_use_tool): Ask mode, a plan to approve, Claude's questions.</summary>
public sealed record PermissionAsk(string RequestId, string Tool, JsonElement Input, JsonElement? Suggestions);

/// <summary>
/// A Sessions pane's claude process, kept open for the session's turns, speaking the CLI's stream-json protocol - the
/// one the Agent SDK speaks - so the pane can do what the terminal does: answer permission questions, stop a turn and
/// keep the session (interrupt), switch mode and model, rewind files, ask an aside. Measured (CLI 2.1.311): a message
/// written while a turn runs joins that turn; interrupt ends the turn with error_during_execution and the process
/// stays; rewind_files needs CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING=1; --replay-user-messages echoes each message
/// with the uuid that rewinding needs. Its output is read all the time, not only during a turn, so questions and
/// events between turns are seen.
/// </summary>
public sealed class SessionProcess : IDisposable
{
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _controls = [];
    private readonly HashSet<string> _openAsks = [];
    private TaskCompletionSource<bool>? _turn;
    private string? _lastUuid;
    private int _next;
    private double _costSoFar;
    private bool _closed;

    public SessionLaunch Launch { get; }

    /// <summary>The model it runs now: the one it started with, then the one set_model switched it to.</summary>
    public string? Model { get; set; }

    /// <summary>The reply to initialize: the commands, the models and more.</summary>
    public JsonElement InitReply { get; private set; }

    /// <summary>The session this process writes (a branch gets a new one): from its init line.</summary>
    public string? SessionId { get; private set; }

    public bool Usable => !_closed && !_process.HasExited;
    public bool Working => _turn != null;
    public DateTime LastActivity { get; private set; } = DateTime.Now;

    /// <summary>What the last turn cost. Measured: a result's total_cost_usd is the process's total so far (0.0047, 0.0101,
    /// 0.0106 over three turns), so a turn's cost is the difference.</summary>
    public double LastTurnCost { get; private set; }

    /// <summary>A step of the turn, as ClaudeSessions.ReadLine reports them (notes, tools, the answer, done, an error).</summary>
    public event Action<SessionEntry>? Entry;

    /// <summary>A message of the user, echoed with its uuid, and the uuid of the line before it (where a rewind of its conversation resumes).</summary>
    public event Action<string, string?>? UserMessage;

    /// <summary>The result line that ends a turn.</summary>
    public event Action<JsonElement>? Result;

    public event Action<PermissionAsk>? Ask;

    /// <summary>A system line: its subtype and the line (task_started, task_notification, commands_changed, ...).</summary>
    public event Action<string, JsonElement>? SystemEvent;

    /// <summary>The process ended; the last of what it wrote to stderr.</summary>
    public event Action<string>? Exited;

    private SessionProcess(Process process, SessionLaunch launch)
    {
        _process = process;
        Launch = launch;
        Model = launch.Model;
    }

    /// <summary>Starts the process in the session's folder and initializes it.</summary>
    public static async Task<SessionProcess> StartAsync(SessionLaunch launch)
    {
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = launch.Folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "1";
        foreach (string arg in Arguments(launch)) start.ArgumentList.Add(arg);
        Process process = await Task.Run(() => Process.Start(start)) ?? throw new InvalidOperationException("could not start claude");
        var session = new SessionProcess(process, launch);
        // a resumed session's results go on from its recorded total
        if (!launch.IsNew) session._costSoFar = ClaudeSessions.LastCost(launch.Id);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (session._stderr)
            {
                session._stderr.AppendLine(e.Data);
                if (session._stderr.Length > 8000) session._stderr.Remove(0, session._stderr.Length - 8000);
            }
        };
        process.BeginErrorReadLine();
        _ = Task.Run(session.ReadAsync);
        try
        {
            session.InitReply = await session.ControlAsync("initialize", timeout: InitTimeout);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    internal static List<string> Arguments(SessionLaunch launch)
    {
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--permission-prompt-tool", "stdio", "--replay-user-messages", "--permission-mode", launch.Mode,
        };
        if (!string.IsNullOrEmpty(launch.Model)) args.AddRange(["--model", launch.Model]);
        if (!string.IsNullOrEmpty(launch.Effort)) args.AddRange(["--effort", launch.Effort]);
        if (launch.IsNew)
        {
            args.AddRange(["--session-id", launch.Id]);
        }
        else
        {
            args.AddRange(["--resume", launch.Id]);
            if (launch.ResumeAt != null) args.AddRange(["--resume-session-at", launch.ResumeAt]);
            if (launch.Fork) args.Add("--fork-session");
            if (launch.Fork && launch.ForkId != null) args.AddRange(["--session-id", launch.ForkId]);
        }
        return args;
    }

    /// <summary>A turn: the message goes in; true when it ended with its result line, false when the process went away.</summary>
    public async Task<bool> TurnAsync(string text)
    {
        var turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _turn = turn;
        try
        {
            await WriteAsync(UserLine(text));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _turn = null;
            return false;
        }
        return await turn.Task;
    }

    /// <summary>A message written now, while a turn runs: the CLI takes it into that turn.</summary>
    public Task SendNowAsync(string text) => WriteAsync(UserLine(text));

    /// <summary>A control request; its reply's response object (an error reply throws).</summary>
    public async Task<JsonElement> ControlAsync(string subtype, Dictionary<string, object?>? fields = null, TimeSpan? timeout = null)
    {
        string id = "devbreak_" + Interlocked.Increment(ref _next);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_controls)
        {
            if (_closed) throw new InvalidOperationException("the claude process has ended");
            _controls[id] = reply;
        }
        var request = new Dictionary<string, object?>(fields ?? []) { ["subtype"] = subtype };
        await WriteAsync(JsonSerializer.Serialize(new { type = "control_request", request_id = id, request }));
        using var limit = new CancellationTokenSource(timeout ?? ControlTimeout);
        using (limit.Token.Register(() => reply.TrySetException(new TimeoutException($"claude did not answer {subtype}"))))
        {
            try
            {
                return await reply.Task;
            }
            finally
            {
                lock (_controls) _controls.Remove(id);
            }
        }
    }

    /// <summary>Answers a permission question once (a second answer, or one after the process ended, is dropped).</summary>
    public void Answer(PermissionAsk ask, object response)
    {
        lock (_openAsks)
        {
            if (!_openAsks.Remove(ask.RequestId)) return;
        }
        string line = JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { subtype = "success", request_id = ask.RequestId, response },
        });
        _ = WriteAsync(line).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Lets the tool run, as asked or with changed input; with the CLI's suggested rules ("always allow").</summary>
    public static object Allow(JsonElement input, JsonElement? permissions = null) =>
        permissions is { ValueKind: JsonValueKind.Array } p
            ? new { behavior = "allow", updatedInput = input, updatedPermissions = p }
            : new { behavior = "allow", updatedInput = input };

    public static object Deny(string message) => new { behavior = "deny", message };

    /// <summary>The questions still waiting for an answer.</summary>
    public bool IsOpen(PermissionAsk ask)
    {
        lock (_openAsks) return _openAsks.Contains(ask.RequestId);
    }

    private static string UserLine(string text) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = new[] { new { type = "text", text } } },
    });

    private async Task WriteAsync(string line)
    {
        await _write.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync();
            LastActivity = DateTime.Now;
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                LastActivity = DateTime.Now;
                if (line.StartsWith('{')) Handle(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // the process went away under the read
        }
        Close();
        string err;
        lock (_stderr) err = _stderr.ToString().Trim();
        Exited?.Invoke(err.Length > 600 ? err[^600..] : err);
    }

    private void Handle(string line)
    {
        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        string type = Str(root, "type");
        switch (type)
        {
            case "control_response":
                Replied(root);
                return;
            case "control_request":
                Requested(root);
                return;
            case "user":
                // a message of the user echoed back (not a tool result): where each turn starts, for rewinding
                if (root.TryGetProperty("isReplay", out JsonElement replay) && replay.ValueKind == JsonValueKind.True
                    && Str(root, "uuid") is { Length: > 0 } uuid && !IsToolResult(root))
                {
                    UserMessage?.Invoke(uuid, _lastUuid);
                }
                break;
            case "system":
                string subtype = Str(root, "subtype");
                if (subtype == "init" && Str(root, "session_id") is { Length: > 0 } id) SessionId = id;
                SystemEvent?.Invoke(subtype, root);
                break;
        }
        if (Str(root, "uuid") is { Length: > 0 } seen && type is "user" or "assistant") _lastUuid = seen;
        if (type == "result" && root.TryGetProperty("total_cost_usd", out JsonElement total) && total.ValueKind == JsonValueKind.Number)
        {
            // a total below the recorded one: this process did not carry the session's total on, it counts from 0
            if (total.GetDouble() < _costSoFar) _costSoFar = 0;
            LastTurnCost = total.GetDouble() - _costSoFar;
            _costSoFar = total.GetDouble();
        }
        if (ClaudeSessions.ReadLine(line, e => Entry?.Invoke(e.Kind == SessionEntryKind.Done ? e with { Text = Done(root) } : e), null))
        {
            Result?.Invoke(root);
            TaskCompletionSource<bool>? turn = _turn;
            _turn = null;
            turn?.TrySetResult(true);
        }
    }

    private void Replied(JsonElement root)
    {
        if (!root.TryGetProperty("response", out JsonElement response)) return;
        TaskCompletionSource<JsonElement>? reply;
        lock (_controls)
        {
            if (!_controls.Remove(Str(response, "request_id"), out reply)) return;
        }
        if (Str(response, "subtype") == "error") reply.TrySetException(new InvalidOperationException(Str(response, "error")));
        else reply.TrySetResult(response.TryGetProperty("response", out JsonElement inner) ? inner : default);
    }

    private void Requested(JsonElement root)
    {
        string id = Str(root, "request_id");
        if (!root.TryGetProperty("request", out JsonElement request)) return;
        if (Str(request, "subtype") != "can_use_tool")
        {
            // nothing else is asked of a process started like this; an answer keeps it from waiting
            _ = WriteAsync(JsonSerializer.Serialize(new
            {
                type = "control_response",
                response = new { subtype = "error", request_id = id, error = "not supported by Dev break" },
            })).ContinueWith(_ => { }, TaskScheduler.Default);
            return;
        }
        JsonElement input = request.TryGetProperty("input", out JsonElement i) ? i : default;
        JsonElement? suggestions = request.TryGetProperty("permission_suggestions", out JsonElement s) ? s : null;
        lock (_openAsks) _openAsks.Add(id);
        Ask?.Invoke(new PermissionAsk(id, Str(request, "tool_name"), input, suggestions));
    }

    // the done line with this turn's own cost
    private string Done(JsonElement root)
    {
        double seconds = root.TryGetProperty("duration_ms", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() / 1000 : 0;
        return $"{seconds:0.#} s · ${LastTurnCost:0.00}";
    }

    private static bool IsToolResult(JsonElement root) =>
        root.TryGetProperty("message", out JsonElement m) && m.TryGetProperty("content", out JsonElement c)
        && c.ValueKind == JsonValueKind.Array && c.EnumerateArray().Any(b => Str(b, "type") == "tool_result");

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    // no more answers will come: every waiting call ends
    private void Close()
    {
        List<TaskCompletionSource<JsonElement>> waiting;
        lock (_controls)
        {
            _closed = true;
            waiting = [.. _controls.Values];
            _controls.Clear();
        }
        foreach (TaskCompletionSource<JsonElement> reply in waiting) reply.TrySetException(new IOException("the claude process has ended"));
        lock (_openAsks) _openAsks.Clear();
        TaskCompletionSource<bool>? turn = _turn;
        _turn = null;
        turn?.TrySetResult(false);
    }

    /// <summary>Ends the process and everything it started.</summary>
    public void Dispose()
    {
        try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        Close();
    }
}
