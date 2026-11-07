using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Toki.LauncherChecks")]

namespace Toki;

internal static class PrimaryViews
{
    public static readonly string[] Names = { "Shortcuts", "Chat", "Reminders", "Email", "Usage", "Settings" };
    public static int PageIndex(string view) => view switch
    {
        "Chat" => 0, "Reminders" => 1, "Email" => 2, "Usage" => 3, "Settings" => 4, _ => 5
    };
    public static Point PopupLocation(Rectangle anchor, Size popup, Rectangle workArea)
    {
        var x = anchor.Right + 10;
        if (x + popup.Width > workArea.Right) x = anchor.Left - popup.Width - 10;
        return new Point(Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - popup.Width)),
            Math.Clamp(anchor.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - popup.Height)));
    }
}

internal static class BrandAssets
{
    private static readonly Lazy<Bitmap> robot = new(() =>
    {
        using var stream = typeof(BrandAssets).Assembly.GetManifestResourceStream("Toki.Assets.robot-head.png")
            ?? throw new InvalidOperationException("The TOKI robot asset is missing.");
        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded);
    });
    public static Bitmap Robot => robot.Value;
    // Keep the asset's original tilt and proportions, even if Windows resizes a window.
    public static Rectangle FitRobot(Size bounds)
    {
        var scale = Math.Min((double)bounds.Width / Robot.Width, (double)bounds.Height / Robot.Height);
        var width = Math.Max(1, (int)Math.Round(Robot.Width * scale));
        var height = Math.Max(1, (int)Math.Round(Robot.Height * scale));
        return new Rectangle((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);
    }
    public static Icon CreateIcon()
    {
        using var stream = typeof(BrandAssets).Assembly.GetManifestResourceStream("Toki.Assets.toki.ico")
            ?? throw new InvalidOperationException("The TOKI app icon is missing.");
        using var icon = new Icon(stream, 64, 64);
        return (Icon)icon.Clone();
    }
}

internal sealed class BrandWordmark : Control
{
    public BrandWordmark()
    {
        DoubleBuffered = true; ForeColor = Color.White;
        Font = new Font("Segoe UI Semibold", 22, FontStyle.Bold);
        AccessibleName = "TOKI"; AccessibleRole = AccessibleRole.Graphic;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var size = Math.Max(1, Math.Min(Height - 8, 58));
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var bounds = BrandAssets.FitRobot(new Size(size, size));
        bounds.Offset(0, (Height - size) / 2);
        e.Graphics.DrawImage(BrandAssets.Robot, bounds);
        TextRenderer.DrawText(e.Graphics, "toki", Font, new Rectangle(size + 4, 0, Math.Max(0, Width - size - 4), Height), ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

internal static class ShortcutService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();

    public static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose an application or Windows shortcut.");
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an .exe application or a Windows .lnk shortcut.");
        if (!File.Exists(fullPath)) throw new FileNotFoundException("This application was moved or removed. Update its shortcut in Settings.");
        return fullPath;
    }
    public static void Open(AppShortcut shortcut)
    {
        var path = ValidatePath(shortcut.Path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    // Shell icon discovery can touch disk or resolve a shortcut. Always run off the UI thread.
    public static Task<Bitmap> LoadIconAsync(string path) => Task.Run(() =>
    {
        var comResult = CoInitializeEx(IntPtr.Zero, 0);
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (File.Exists(path) && SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) != IntPtr.Zero)
            {
                handle = info.Icon;
                if (handle != IntPtr.Zero)
                {
                    using var icon = Icon.FromHandle(handle);
                    return icon.ToBitmap();
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException) { }
        finally
        {
            if (handle != IntPtr.Zero) DestroyIcon(handle);
            if (comResult >= 0) CoUninitialize();
        }
        return SystemIcons.Application.ToBitmap();
    });
}

internal sealed class ShortcutTile : Button
{
    private readonly string targetPath;
    private Bitmap? appIcon;
    private bool loading;
    private bool hovered;
    public ShortcutTile(AppShortcut shortcut)
    {
        targetPath = shortcut.Path;
        AccessibleName = shortcut.Name;
        AccessibleDescription = "Open " + shortcut.Name;
        Size = new Size(64, 64); Margin = new Padding(6);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = Color.FromArgb(29, 37, 55); Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (loading || appIcon != null) return;
        loading = true;
        try
        {
            var bitmap = await ShortcutService.LoadIconAsync(targetPath);
            if (IsDisposed) bitmap.Dispose();
            else { appIcon = bitmap; Invalidate(); }
        }
        catch { /* Keep the local fallback glyph if Windows cannot resolve an icon. */ }
        finally { loading = false; }
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = new GraphicsPath();
        const int d = 20;
        outline.AddArc(1, 1, d, d, 180, 90); outline.AddArc(Width - d - 2, 1, d, d, 270, 90);
        outline.AddArc(Width - d - 2, Height - d - 2, d, d, 0, 90); outline.AddArc(1, Height - d - 2, d, d, 90, 90); outline.CloseFigure();
        using var fill = new SolidBrush(hovered || Focused ? Color.FromArgb(48, 60, 88) : BackColor);
        e.Graphics.FillPath(fill, outline);
        if (Focused) { using var border = new Pen(Color.FromArgb(132, 159, 255), 2); e.Graphics.DrawPath(border, outline); }
        if (appIcon != null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var side = Math.Min(40, Math.Min(Width, Height) - 20);
            e.Graphics.DrawImage(appIcon, new Rectangle((Width - side) / 2, (Height - side) / 2, side, side));
        }
        else TextRenderer.DrawText(e.Graphics, "▦", Font, ClientRectangle, Color.LightSteelBlue, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) appIcon?.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class ShortcutGrid : UserControl
{
    private readonly FlowLayoutPanel icons = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(2) };
    private readonly Label empty = new() { Dock = DockStyle.Fill, Text = "Your apps, one click away.\n\nAdd an app in Settings to see its icon here.", ForeColor = Color.LightSteelBlue, TextAlign = ContentAlignment.MiddleCenter };
    private readonly ToolTip tips = new() { InitialDelay = 350, AutoPopDelay = 5000 };
    public event Action<AppShortcut>? ShortcutClicked;
    public ShortcutGrid()
    {
        BackColor = Color.FromArgb(13, 17, 27); icons.BackColor = BackColor;
        Controls.Add(icons); Controls.Add(empty);
    }
    public void SetShortcuts(IEnumerable<AppShortcut> shortcuts)
    {
        icons.SuspendLayout(); tips.RemoveAll();
        while (icons.Controls.Count > 0) { var item = icons.Controls[0]; icons.Controls.RemoveAt(0); item.Dispose(); }
        foreach (var shortcut in shortcuts)
        {
            var tile = new ShortcutTile(shortcut);
            tips.SetToolTip(tile, shortcut.Name);
            tile.Click += (_, _) => ShortcutClicked?.Invoke(shortcut);
            icons.Controls.Add(tile);
        }
        empty.Visible = icons.Controls.Count == 0; icons.Visible = !empty.Visible;
        icons.ResumeLayout();
    }
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
}

internal sealed class LauncherActionButton : Button
{
    private readonly bool menu;
    private bool hovered;
    public LauncherActionButton(bool menu)
    {
        this.menu = menu;
        Size = new Size(42, 42); Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(13, 17, 27));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (hovered || (Focused && ShowFocusCues))
        {
            using var background = new SolidBrush(Color.FromArgb(40, 50, 73));
            e.Graphics.FillEllipse(background, 2, 2, Width - 4, Height - 4);
        }
        using var pen = new Pen(Color.FromArgb(231, 237, 249), 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var x = (Width - 18) / 2f; var y = (Height - 18) / 2f;
        if (menu)
        {
            for (var line = 0; line < 3; line++) e.Graphics.DrawLine(pen, x, y + line * 9, x + 18, y + line * 9);
        }
        else
        {
            e.Graphics.DrawLine(pen, x, y, x + 18, y + 18);
            e.Graphics.DrawLine(pen, x + 18, y, x, y + 18);
        }
    }
}

internal sealed class ShortcutLauncherForm : Form
{
    private readonly TokiApp app;
    private readonly ShortcutGrid grid = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 48, Visible = false, ForeColor = Color.LightSteelBlue, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolTip tips = new();
    public ShortcutLauncherForm(TokiApp app)
    {
        this.app = app;
        Text = "TOKI · Shortcuts"; Icon = BrandAssets.CreateIcon();
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(13, 17, 27); ForeColor = Color.White; Font = new Font("Segoe UI", 10);
        Padding = new Padding(14); KeyPreview = true; DoubleBuffered = true;
        var header = new Panel { Dock = DockStyle.Top, Height = 60 };
        header.Controls.Add(new BrandWordmark { Dock = DockStyle.Left, Width = 135 });
        var actions = new Panel { Dock = DockStyle.Right, Width = 90 };
        var expand = ActionButton(true, "Open full TOKI workspace", (_, _) => { Hide(); app.Open(5); });
        var close = ActionButton(false, "Close shortcuts", (_, _) => Hide());
        expand.Location = new Point(0, 9); close.Location = new Point(48, 9);
        actions.Controls.Add(expand); actions.Controls.Add(close); header.Controls.Add(actions);
        grid.ShortcutClicked += shortcut =>
        {
            try { ShortcutService.Open(shortcut); Hide(); }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                if (!status.Visible)
                {
                    var area = Screen.FromRectangle(Bounds).WorkingArea;
                    Height = Math.Min(Height + 48, area.Height);
                    Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
                }
                status.Visible = true; tips.SetToolTip(status, ex.Message);
            }
        };
        Controls.Add(grid); Controls.Add(status); Controls.Add(header);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Hide(); } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
    private Button ActionButton(bool menu, string name, EventHandler action)
    {
        var button = new LauncherActionButton(menu) { AccessibleName = name };
        tips.SetToolTip(button, name); button.Click += action; return button;
    }
    public void ShowNear(Rectangle anchor)
    {
        RefreshShortcuts();
        var area = Screen.FromRectangle(anchor).WorkingArea;
        var rows = Math.Max(1, (app.Data.AppShortcuts.Count + 3) / 4);
        Size = new Size(Math.Min(350, area.Width), Math.Min(Math.Min(480, 100 + rows * 76), area.Height));
        Location = PrimaryViews.PopupLocation(anchor, Size, area);
        TopMost = app.Data.TopMost; Show(); Activate();
    }
    public void RefreshShortcuts()
    {
        grid.SetShortcuts(app.Data.AppShortcuts);
        status.Text = ""; status.Visible = false;
    }
    private GraphicsPath RoundedOutline()
    {
        var path = new GraphicsPath();
        var d = Math.Min(40, Math.Min(Width - 2, Height - 2));
        path.AddArc(0, 0, d, d, 180, 90); path.AddArc(Width - d - 1, 0, d, d, 270, 90);
        path.AddArc(Width - d - 1, Height - d - 1, d, d, 0, 90); path.AddArc(0, Height - d - 1, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 3 || Height < 3) return;
        using var outline = RoundedOutline();
        var oldRegion = Region; Region = new Region(outline); oldRegion?.Dispose();
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Color.FromArgb(64, 79, 110));
        using var outline = RoundedOutline(); e.Graphics.DrawPath(border, outline);
    }
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
}

internal sealed class LayeredRobot : IDisposable
{
    private readonly Dictionary<int, IntPtr> frames = new();
    private Bitmap? scaledRobot;
    private Size canvasSize;
    private IntPtr memory;
    private IntPtr originalBitmap;
    private int displayedFrame = int.MinValue;
    public static float FloatOffset(double phase) => (float)(Math.Sin(phase) * 3);
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr dc, ref Point destination, ref Size size,
        IntPtr sourceDC, ref Point source, int key, ref Blend blend, int flags);
    public void Render(Form window, float offset = 0)
    {
        if (canvasSize != window.Size || scaledRobot == null)
        {
            Dispose(); canvasSize = window.Size;
            scaledRobot = new Bitmap(Math.Max(1, window.Width - 8), Math.Max(1, window.Height - 8), PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(scaledRobot);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(BrandAssets.Robot, BrandAssets.FitRobot(scaledRobot.Size));
            memory = CreateCompatibleDC(IntPtr.Zero);
        }
        // 1/8-pixel frames retain smooth motion without rebuilding or moving the HWND every tick.
        var frame = (int)Math.Round(Math.Clamp(offset, -3, 3) * 8);
        if (frame == displayedFrame) return;
        if (!frames.TryGetValue(frame, out var handle))
        {
            using var bitmap = new Bitmap(canvasSize.Width, canvasSize.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(scaledRobot, new RectangleF(4, 4 + frame / 8f, scaledRobot.Width, scaledRobot.Height));
            }
            handle = bitmap.GetHbitmap(Color.FromArgb(0)); frames.Add(frame, handle);
        }
        var previous = SelectObject(memory, handle);
        if (originalBitmap == IntPtr.Zero) originalBitmap = previous;
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var destination = window.Location; var size = window.Size; var source = Point.Empty;
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(window.Handle, dc, ref destination, ref size, memory, ref source, 0, ref blend, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            displayedFrame = frame;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
    public void Dispose()
    {
        if (memory != IntPtr.Zero)
        {
            if (originalBitmap != IntPtr.Zero) SelectObject(memory, originalBitmap);
            foreach (var bitmap in frames.Values) DeleteObject(bitmap);
            DeleteDC(memory);
        }
        frames.Clear(); scaledRobot?.Dispose(); scaledRobot = null;
        memory = IntPtr.Zero; originalBitmap = IntPtr.Zero; displayedFrame = int.MinValue;
    }
}
