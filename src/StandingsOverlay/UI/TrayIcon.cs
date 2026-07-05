using System.Drawing;
using System.Windows.Forms;

namespace StandingsOverlay.UI;

/// <summary>
/// System-tray icon — the only UI chrome the app has. The overlay window itself is
/// click-through, so this is how the user moves it (edit mode) and exits.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _drawnIcon;

    public event Action<bool>? EditModeToggled;
    public event Action? ExitRequested;

    public TrayIcon(bool demoMode)
    {
        _drawnIcon = DrawIcon();

        var menu = new ContextMenuStrip();
        var edit = new ToolStripMenuItem("Edit mode (drag to move)") { CheckOnClick = true };
        edit.CheckedChanged += (_, _) => EditModeToggled?.Invoke(edit.Checked);
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(edit);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = _drawnIcon,
            Text = "Standings Overlay" + (demoMode ? " (demo)" : ""),
            Visible = true,
            ContextMenuStrip = menu,
        };
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
