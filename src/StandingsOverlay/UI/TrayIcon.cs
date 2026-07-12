using System.Drawing;
using System.Windows.Forms;
using StandingsOverlay.Config;

namespace StandingsOverlay.UI;

/// <summary>
/// System-tray icon — the only UI chrome the app has. The overlay window itself is
/// click-through, so this is how the user moves it (edit mode), sees whether the app is
/// alive/connected (tooltip), makes it start with Windows, and exits.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _drawnIcon;
    private readonly ToolStripMenuItem _edit;

    public event Action<bool>? EditModeToggled;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIcon(bool demoMode)
    {
        _drawnIcon = DrawIcon();

        var menu = new ContextMenuStrip();

        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke())
        {
            Font = new Font(menu.Font, System.Drawing.FontStyle.Bold), // default action, emphasized
        };

        _edit = new ToolStripMenuItem("Move overlays (drag to reposition)") { CheckOnClick = true };
        _edit.CheckedChanged += (_, _) => EditModeToggled?.Invoke(_edit.Checked);

        var autostart = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled(),
        };
        autostart.CheckedChanged += (_, _) => AutoStart.Set(autostart.Checked);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_edit);
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = _drawnIcon,
            Text = "Standings Overlay" + (demoMode ? " (demo)" : " — waiting for iRacing"),
            Visible = true,
            ContextMenuStrip = menu,
        };
        // Double-clicking the tray icon opens settings, the Windows-standard "primary action".
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    /// <summary>Insert an "update available" entry at the top of the menu. Called at most once
    /// per run (UI thread), only when UpdateCheck found a newer release.</summary>
    public void ShowUpdateAvailable(string tag, string url)
    {
        var menu = _icon.ContextMenuStrip!;
        var item = new ToolStripMenuItem($"Update available — {tag}")
        {
            Font = new Font(menu.Font, System.Drawing.FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 200, 165),   // the app accent, readable on the menu
        };
        item.Click += (_, _) => UpdateCheck.OpenReleasePage(url);
        menu.Items.Insert(0, item);
        menu.Items.Insert(1, new ToolStripSeparator());
    }

    /// <summary>Reflect edit mode toggled from elsewhere (the settings window) without re-firing.</summary>
    public void ReflectEditMode(bool on)
    {
        if (_edit.Checked == on) return;
        _edit.Checked = on;   // CheckedChanged re-invokes EditModeToggled(on), which is idempotent
    }

    private string _lastStatus = "";

    /// <summary>Tooltip status; call from the UI thread. Max 63 chars (NotifyIcon limit).</summary>
    public void SetStatus(string status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;
        var text = "Standings Overlay — " + status;
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    /// <summary>Tiny leaderboard glyph drawn at runtime so we don't need an .ico asset.</summary>
    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(33, 33, 41));
        using var accent = new SolidBrush(Color.FromArgb(0, 255, 208));
        using var dim = new SolidBrush(Color.FromArgb(160, 160, 170));
        g.FillRectangle(accent, 2, 3, 12, 2);
        g.FillRectangle(dim, 2, 7, 9, 2);
        g.FillRectangle(dim, 2, 11, 11, 2);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawnIcon.Dispose();
    }
}
