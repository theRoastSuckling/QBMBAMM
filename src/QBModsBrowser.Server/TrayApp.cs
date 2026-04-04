using System.Windows.Forms;
using Serilog;

// Owns the system-tray icon, context menu, and WinForms message pump for the server process.
internal sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    // Creates the tray icon and wires up the dark-themed context menu.
    // Clicking Exit calls Application.Exit(), causing Run() to return so the caller can shut down.
    public TrayApp(string appUrl)
    {
        _notifyIcon = new NotifyIcon
        {
            Icon    = TryGetAppIcon(),
            Text    = "QB Mods Browser",
            Visible = true,
        };

        // Left-click opens the browser directly.
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenBrowser(appUrl);
        };

        // Right-click context menu with a dark theme and larger item font.
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), Font = new Font("Segoe UI", 11f) };

        // Non-interactive explanation shown at the top of the context menu.
        var infoItem = new ToolStripLabel
        {
            Text      = "The QBMBAMM backend runs\nhere in the system tray.\nSingle left click the icon to\nopen the webapp again.",
            Font      = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(150, 150, 150),
            Padding   = new Padding(10, 8, 10, 8),
        };
        menu.Items.Add(infoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => OpenBrowser(appUrl));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _notifyIcon.Visible = false;
            Application.Exit();
        });

        _notifyIcon.ContextMenuStrip = menu;
    }

    // Blocks on the WinForms message pump; returns when Application.Exit() is called.
    public void Run() => Application.Run();

    // Hides and disposes the tray icon.
    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    // Extracts the icon embedded in the running exe; falls back to the system application icon.
    static Icon? TryGetAppIcon()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
                return Icon.ExtractAssociatedIcon(exePath);
        }
        catch { }
        return SystemIcons.Application;
    }

    // Opens the given URL in the system default browser.
    static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open browser at {Url}", url);
        }
    }
}

// Dark color table used by DarkMenuRenderer to theme ContextMenuStrip.
internal sealed class DarkMenuColorTable : ProfessionalColorTable
{
    static readonly Color Background = Color.FromArgb(30, 30, 30);
    static readonly Color Hover      = Color.FromArgb(55, 55, 55);
    static readonly Color Border     = Color.FromArgb(60, 60, 60);
    static readonly Color Separator  = Color.FromArgb(60, 60, 60);

    public override Color MenuItemSelected                  => Hover;
    public override Color MenuItemBorder                    => Hover;
    public override Color MenuItemSelectedGradientBegin     => Hover;
    public override Color MenuItemSelectedGradientEnd       => Hover;
    public override Color MenuItemPressedGradientBegin      => Hover;
    public override Color MenuItemPressedGradientEnd        => Hover;
    public override Color ToolStripDropDownBackground       => Background;
    public override Color ImageMarginGradientBegin          => Background;
    public override Color ImageMarginGradientMiddle         => Background;
    public override Color ImageMarginGradientEnd            => Background;
    public override Color MenuBorder                        => Border;
    public override Color SeparatorDark                     => Separator;
    public override Color SeparatorLight                    => Separator;
}

// Custom renderer that applies the dark color table and paints item text in light gray.
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    static readonly Color TextColor = Color.FromArgb(220, 220, 220);

    // Inherit all structural colors from DarkMenuColorTable.
    public DarkMenuRenderer() : base(new DarkMenuColorTable()) { }

    // Override text paint so it reads on the dark background.
    // ToolStripLabel items (e.g. the info header) keep their own ForeColor instead.
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item is ToolStripLabel ? e.Item.ForeColor : TextColor;
        base.OnRenderItemText(e);
    }
}
