using System;
using System.Drawing;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--now")
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            Ask();
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Ask me now", null, (_, _) => Ask());
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

    private static Icon TrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new SolidBrush(System.Drawing.Color.FromArgb(0x2E, 0x7D, 0x32));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 17, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("?", font, Brushes.White, new RectangleF(0, 0, 32, 32), centered);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
