using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DevBreak;

public partial class App : Application
{
    private const int PopupMinute = 55;
    public const int SnoozeMinutes = 15;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly QuestionPool _pool = new();
    private Forms.NotifyIcon? _tray;
    private QuestionWindow? _open;
    private DateTime? _lastShownHour;
    private DateTime? _snoozedUntil;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!Topics.IsSetUp)
        {
            // first run: Claude Code must work, then the user picks their topics
            string? problem = await ClaudeProblemAsync();
            if (problem != null)
            {
                MessageBox.Show(problem, "Dev break", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            new TopicsWindow(isFirstRun: true).ShowDialog();
        }
        Topics.Changed += () =>
        {
            _pool.FillAll(Level.At(DateTime.Now.TimeOfDay));
            LearnPath.PrepareAll();
        };

        if (e.Args.Length > 0 && e.Args[0] == "--now")
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            Ask();
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Ask me now", null, (_, _) => Ask());
        menu.Items.Add("Learn path", null, (_, _) => ShowLearn());
        menu.Items.Add(KnowledgeMenu());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Text = $"Dev break - a question at :{PopupMinute} every hour",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) Ask();
        };

        _pool.FillAll(Level.At(DateTime.Now.TimeOfDay));
        LearnPath.PrepareAll();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    public void Snooze() => _snoozedUntil = DateTime.Now.AddMinutes(SnoozeMinutes);

    private void Tick()
    {
        DateTime now = DateTime.Now;
        // keeps the queues full, and fills the next level's queues once the time of day moves on
        _pool.FillAll(Level.At(now.TimeOfDay));
        if (_snoozedUntil is { } until)
        {
            if (now < until) return;
            _snoozedUntil = null;
            Ask();
            return;
        }
        DateTime hour = now.Date.AddHours(now.Hour);
        if (now.Minute >= PopupMinute && hour != _lastShownHour)
        {
            _lastShownHour = hour;
            _tray?.ShowBalloonTip(5000, "Dev break", "Time for a 5-minute brain break.", Forms.ToolTipIcon.Info);
            Ask();
        }
    }

    private void Ask()
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new QuestionWindow(_pool);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    /// <summary>Why the claude CLI cannot answer (not installed, or not logged in), or null when it works.</summary>
    private static async Task<string?> ClaudeProblemAsync()
    {
        try
        {
            await QuestionGenerator.RunClaudeAsync("Reply with just: OK");
            return null;
        }
        catch (Win32Exception)
        {
            return "Claude Code is not installed.\n\nInstall Claude Code, run \"claude\" once and log in, then start Dev break again.";
        }
        catch (Exception ex)
        {
            return "Claude Code is installed but did not answer - is it logged in?\n\nRun \"claude\" once and log in, "
                + "then start Dev break again.\n\n" + ex.Message;
        }
    }

    /// <summary>Opens the window (or brings it to the front) on the learn path.</summary>
    private void ShowLearn()
    {
        Ask();
        _open?.ShowLearn();
    }

    /// <summary>"Knowledge" submenu: what new questions know about the user. The choice is saved in settings.json.</summary>
    private static Forms.ToolStripMenuItem KnowledgeMenu()
    {
        var knowledge = new Forms.ToolStripMenuItem("Knowledge about me");
        foreach ((KnowledgeSource source, string label) in new[]
                 {
                     (KnowledgeSource.Claude, "From my Claude history (summary once a day)"),
                     (KnowledgeSource.File, "From profile.txt"),
                     (KnowledgeSource.None, "None (generic questions)"),
                 })
        {
            var item = new Forms.ToolStripMenuItem(label) { Checked = Knowledge.Source == source, Tag = source };
            item.Click += (_, _) =>
            {
                Knowledge.SetSource(source);
                foreach (Forms.ToolStripMenuItem other in knowledge.DropDownItems)
                {
                    other.Checked = other == item;
                }
            };
            knowledge.DropDownItems.Add(item);
        }
        // the question window can change it too
        knowledge.DropDownOpening += (_, _) =>
        {
            foreach (Forms.ToolStripMenuItem item in knowledge.DropDownItems)
            {
                item.Checked = (KnowledgeSource)item.Tag! == Knowledge.Source;
            }
        };
        return knowledge;
    }

    /// <summary>The app icon (Assets\DevBreak.ico) at the tray's size: the small sizes are the simplified violet "?".</summary>
    private static Icon TrayIcon()
    {
        using Stream stream = GetResourceStream(new Uri("pack://application:,,,/Assets/DevBreak.ico")).Stream;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
