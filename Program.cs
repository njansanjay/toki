using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Toki;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var app = new TokiApp(args.Contains("--open", StringComparer.OrdinalIgnoreCase));
        if (args.Contains("--launcher", StringComparer.OrdinalIgnoreCase)) app.ShowLauncher();
        Application.Run(app);
    }
}

internal sealed class TokiApp : ApplicationContext
{
    internal readonly AppData Data = Store.Load();
    private readonly NotifyIcon tray;
    private readonly OrbForm orb;
    private ChatForm? chat;
    private ShortcutLauncherForm? launcher;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    private bool paused;

    public TokiApp(bool openChat = false)
    {
        var oauthFile = Path.Combine(AppContext.BaseDirectory, "oauth.local.json");
        if (File.Exists(oauthFile))
        {
            try
            {
                using var oauth = JsonDocument.Parse(File.ReadAllText(oauthFile));
                var configuredId = oauth.RootElement.GetProperty("google_client_id").GetString() ?? "";
                if (configuredId.Length > 0 && (Data.GoogleClientId.Length == 0 || Data.GoogleClientId == configuredId))
                {
                    var changed = Data.GoogleClientId.Length == 0;
                    Data.GoogleClientId = configuredId;
                    if (Data.EncryptedGoogleClientSecret.Length == 0 && oauth.RootElement.TryGetProperty("google_client_secret_protected", out var secret))
                    {
                        var protectedSecret = secret.GetString() ?? "";
                        if (Store.Unprotect(protectedSecret).Length > 0)
                        {
                            Data.EncryptedGoogleClientSecret = protectedSecret;
                            changed = true;
                        }
                    }
                    if (changed) Save();
                }
            }
            catch { }
        }
        orb = new OrbForm(this);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open TOKI", null, (_, _) => Open());
        menu.Items.Add("App shortcuts", null, (_, _) => ShowLauncher());
        menu.Items.Add("New chat", null, (_, _) => { Data.Messages.Clear(); Save(); Open(); chat?.RefreshChat(); });
        menu.Items.Add("Add reminder", null, (_, _) => { Open(); chat?.SelectTab(1); });
        menu.Items.Add("Email assistant", null, (_, _) => { Open(); chat?.SelectTab(2); });
        menu.Items.Add("Usage monitor", null, (_, _) => { Open(); chat?.SelectTab(3); });
        menu.Items.Add("Settings", null, (_, _) => { Open(); chat?.SelectTab(4); });
        menu.Items.Add("Pause notifications", null, (_, _) => { paused = !paused; });
        menu.Items.Add("Restart TOKI", null, (_, _) => { Application.Restart(); Exit(); });
        menu.Items.Add("Exit", null, (_, _) => Exit());
        tray = new NotifyIcon { Icon = TokiIcon.Create(), Text = "TOKI", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Open();
        orb.Show();
        if (openChat) Open();
        timer.Tick += (_, _) => CheckReminders();
        timer.Start();
        CheckReminders();
    }

    internal void Save() => Store.Save(Data);
    internal bool SetOpenHotkey(string value) => orb.SetOpenHotkey(value);
    internal void Open(int page = 0)
    {
        launcher?.Hide();
        if (chat is null || chat.IsDisposed) chat = new ChatForm(this);
        chat.SelectTab(page);
        if (chat.WindowState == FormWindowState.Minimized) chat.WindowState = FormWindowState.Normal;
        chat.Show();
        chat.Activate();
    }
    internal void TogglePrimary()
    {
        var page = PrimaryViews.PageIndex(Data.PrimaryView);
        if (page != 5) { Open(page); return; }
        if (launcher?.Visible == true) launcher.Hide(); else ShowLauncher();
    }
    internal void ShowLauncher()
    {
        if (launcher is null || launcher.IsDisposed) launcher = new ShortcutLauncherForm(this);
        launcher.ShowNear(orb.Bounds);
    }
    internal void OpenShortcutSettings() { Open(4); chat?.FocusShortcutSettings(); }
    internal void RefreshShortcuts() { chat?.RefreshShortcutGallery(); launcher?.RefreshShortcuts(); }
    internal void ApplyAppearance()
    {
        orb.ApplyAppearance();
        if (chat != null) chat.TopMost = Data.TopMost;
        if (launcher != null) launcher.TopMost = Data.TopMost;
    }
    internal void Notify(string title, string message)
    {
        if (paused || !Data.Notifications) return;
        tray.BalloonTipTitle = title;
        tray.BalloonTipText = message;
        tray.ShowBalloonTip(5000);
        System.Media.SystemSounds.Asterisk.Play();
    }
    private void CheckReminders()
    {
        var changed = false;
        foreach (var r in Data.Reminders.Where(x => !x.Completed && x.Due <= DateTime.Now).ToList())
        {
            Notify("TOKI reminder", r.Text);
            if (r.Recurrence == "Daily") r.Due = DateTime.Now.Date.AddDays(1).Add(r.Due.TimeOfDay);
            else if (r.Recurrence == "Weekly") { do { r.Due = r.Due.AddDays(7); } while (r.Due <= DateTime.Now); }
            else r.Completed = true;
            changed = true;
        }
        var u = Data.Usage;
        if (u.Reset <= DateTime.Now && u.Limit > 0)
        {
            u.Used = 0; u.WarningSent = false; u.CriticalSent = false;
            u.Reset = u.Reset.AddMonths(1); changed = true;
        }
        if (u.Limit > 0)
        {
            var percent = u.Used * 100.0 / u.Limit;
            if (percent >= u.Critical && !u.CriticalSent) { Notify("TOKI manual usage estimate", "Your manually entered ChatGPT usage is approaching its limit."); u.CriticalSent = true; changed = true; }
            else if (percent >= u.Warning && !u.WarningSent) { Notify("TOKI manual usage estimate", $"Your manually entered usage has reached about {percent:0}%."); u.WarningSent = true; changed = true; }
        }
        if (changed) Save();
    }
    internal void Exit()
    {
        timer.Stop(); Save(); tray.Visible = false; tray.Dispose(); launcher?.Dispose(); orb.Close(); chat?.Dispose(); ExitThread();
    }
}

internal sealed class OrbForm : Form
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    private const int HotkeyId = 0x544F;
    private const int WmHotkey = 0x0312;
    private readonly TokiApp app;
    private string registeredHotkey = "Off";
    private Point dragOrigin;
    private Point windowOrigin;
    private bool moved;
    private bool pointerDown;
    private Point restingLocation;
    private double floatPhase;
    private readonly Stopwatch floatClock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer floatTimer = new() { Interval = 16 };
    private readonly LayeredRobot robotRenderer = new();
    public OrbForm(TokiApp app)
    {
        this.app = app;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = app.Data.TopMost;
        Text = "TOKI floating robot";
        AccessibleName = "TOKI floating robot";
        Icon = BrandAssets.CreateIcon(); Cursor = Cursors.Hand;
        var size = Math.Clamp(app.Data.OrbSize, 40, 192);
        MinimumSize = new Size(size, size); MaximumSize = MinimumSize;
        Size = new Size(size, size);
        Location = new Point(app.Data.OrbX, app.Data.OrbY);
        restingLocation = Location;
        var menu = new ContextMenuStrip();
        menu.Items.Add("One-touch view", null, (_, _) => app.TogglePrimary());
        menu.Items.Add("Open full TOKI", null, (_, _) => app.Open(5));
        menu.Items.Add("Settings", null, (_, _) => app.Open(4));
        menu.Items.Add("Exit", null, (_, _) => app.Exit());
        ContextMenuStrip = menu;
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { pointerDown = true; dragOrigin = Cursor.Position; windowOrigin = Location; moved = false; } };
        MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) { var delta = new Size(Cursor.Position.X - dragOrigin.X, Cursor.Position.Y - dragOrigin.Y); if (Math.Abs(delta.Width) + Math.Abs(delta.Height) > 4) moved = true; if (moved) Location = windowOrigin + delta; } };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) { pointerDown = false; if (moved) { ClampToScreen(); restingLocation = Location; floatPhase = 0; app.Data.OrbX = Left; app.Data.OrbY = Top; app.Save(); } else app.TogglePrimary(); } };
        MouseCaptureChanged += (_, _) => { if (Control.MouseButtons == MouseButtons.None) pointerDown = false; };
        floatTimer.Tick += (_, _) => AnimateFloat();
    }
    protected override CreateParams CreateParams { get { var parameters = base.CreateParams; parameters.ExStyle |= 0x00080000; return parameters; } }
    protected override void OnShown(EventArgs e) { base.OnShown(e); ClampToScreen(); restingLocation = Location; robotRenderer.Render(this); floatClock.Restart(); floatTimer.Start(); }
    private void AnimateFloat()
    {
        var elapsed = Math.Min(floatClock.Elapsed.TotalSeconds, 0.1); floatClock.Restart();
        // Pause under the pointer so a moving target never gets in the way of a click or drag.
        if (!app.Data.FloatAnimation || !SystemInformation.UIEffectsEnabled || pointerDown || Bounds.Contains(Cursor.Position) || ContextMenuStrip?.Visible == true) return;
        floatPhase = (floatPhase + elapsed * Math.PI * 2 / 3.8) % (Math.PI * 2);
        robotRenderer.Render(this, LayeredRobot.FloatOffset(floatPhase));
    }
    private void ClampToScreen()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }
    public void ApplyAppearance()
    {
        TopMost = app.Data.TopMost;
        var size = Math.Clamp(app.Data.OrbSize, 40, 192);
        MaximumSize = Size.Empty; MinimumSize = new Size(size, size); MaximumSize = MinimumSize; Size = MinimumSize;
        Location = restingLocation; ClampToScreen(); restingLocation = Location; floatPhase = 0;
        robotRenderer.Dispose();
        if (IsHandleCreated) robotRenderer.Render(this);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetOpenHotkey(app.Data.OpenHotkey);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (registeredHotkey != "Off") UnregisterHotKey(Handle, HotkeyId);
        registeredHotkey = "Off";
        base.OnHandleDestroyed(e);
    }
    public bool SetOpenHotkey(string shortcut)
    {
        if (!IsHandleCreated) return true;
        var old = registeredHotkey;
        if (old != "Off") UnregisterHotKey(Handle, HotkeyId);
        registeredHotkey = "Off";
        if (shortcut == "Off") return true;
        var modifiers = shortcut == "Ctrl+Shift+Space" ? 0x4000u | 0x0002u | 0x0004u : 0x4000u | 0x0002u | 0x0001u;
        var key = shortcut == "Ctrl+Shift+Space" ? (uint)Keys.Space : (uint)Keys.T;
        if (RegisterHotKey(Handle, HotkeyId, modifiers, key)) { registeredHotkey = shortcut; return true; }
        if (old != "Off")
        {
            var oldModifiers = old == "Ctrl+Shift+Space" ? 0x4000u | 0x0002u | 0x0004u : 0x4000u | 0x0002u | 0x0001u;
            var oldKey = old == "Ctrl+Shift+Space" ? (uint)Keys.Space : (uint)Keys.T;
            if (RegisterHotKey(Handle, HotkeyId, oldModifiers, oldKey)) registeredHotkey = old;
        }
        return false;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId) app.TogglePrimary();
        base.WndProc(ref m);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // The robot is rendered with per-pixel transparency by LayeredRobot.
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { floatTimer.Stop(); floatTimer.Dispose(); robotRenderer.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class ChatForm : Form
{
    [DllImport("user32.dll", SetLastError = true)] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private static readonly Color Canvas = Color.FromArgb(13, 17, 27);
    private static readonly Color Surface = Color.FromArgb(22, 28, 42);
    private static readonly Color InputSurface = Color.FromArgb(32, 40, 57);
    private static readonly Color Accent = Color.FromArgb(113, 119, 255);
    private static readonly Color Muted = Color.FromArgb(154, 168, 190);
    private readonly TokiApp app;
    private readonly Panel pageHost = new() { Dock = DockStyle.Fill, BackColor = Canvas };
    private readonly List<Panel> pages = new();
    private int selectedPage;
    private readonly FlowLayoutPanel history = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Canvas };
    private readonly TextBox input = new() { Dock = DockStyle.Fill, Multiline = true, BorderStyle = BorderStyle.None, PlaceholderText = "Ask TOKI anything..." };
    private readonly FlowLayoutPanel reminders = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Label usageStatus = new() { AutoSize = true };
    private readonly Label hotkeyHint = new() { AutoSize = true, ForeColor = Color.FromArgb(159, 171, 194), Anchor = AnchorStyles.Right };
    private readonly TextBox keyBox = new() { UseSystemPasswordChar = true, Width = 280 };
    private readonly TextBox modelBox = new() { Width = 280 };
    private readonly TextBox urlBox = new() { Width = 280 };
    private readonly ComboBox providerBox = new() { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly EmailService email;
    private ComboBox? accountBox;
    private Label? accountEmpty;
    private readonly AccountAvatar accountAvatar = new() { Size = new Size(38, 38) };
    private readonly Label accountStatus = new() { Width = 335, Height = 42, ForeColor = Color.White, Text = "No email account connected" };
    private readonly Label connectionStatus = new() { Width = 390, Height = 42, ForeColor = Color.LightSkyBlue, Text = "Choose an account to connect." };
    private Button? gmailButton;
    private Button? outlookButton;
    private Button? cancelConnectionButton;
    private CancellationTokenSource? connectionCancellation;
    private readonly Button[] navigation = new Button[6];
    private readonly ShortcutGrid shortcutGallery = new() { Dock = DockStyle.Fill };
    private FlowLayoutPanel? settingsScroll;
    private SurfaceCard? shortcutSettingsCard;
    private readonly Label sectionTitle = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 18), ForeColor = Color.White };
    private readonly Label sectionDescription = new() { AutoSize = true, ForeColor = Muted };
    private DateTime lastChatRefresh;

    public ChatForm(TokiApp app)
    {
        this.app = app;
        email = new EmailService(app);
        Text = "TOKI  ·  Desktop assistant"; Size = new Size(1080, 760); MinimumSize = new Size(780, 580);
        StartPosition = FormStartPosition.CenterScreen; TopMost = app.Data.TopMost;
        BackColor = Canvas; ForeColor = Color.White; Icon = TokiIcon.Create();
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        pages.AddRange(new[] { MakeChat(), MakeReminders(), MakePremiumEmail(), MakeUsage(), MakeSettings(), MakeShortcuts() });
        foreach (var page in pages) { page.Dock = DockStyle.Fill; page.Visible = false; pageHost.Controls.Add(page); }
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Canvas, Margin = Padding.Empty, Padding = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 212)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 24, 37), Padding = new Padding(14, 22, 14, 18) };
        var logo = new BrandWordmark { Dock = DockStyle.Top, Height = 72 };
        sidebar.Controls.Add(logo);
        var navHost = new FlowLayoutPanel { Location = new Point(14, 106), Size = new Size(184, 308), FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = sidebar.BackColor };
        var names = new[] { "Chat", "Reminders", "Email", "Usage", "Settings", "Shortcuts" };
        var symbols = new[] { "✦", "◷", "✉", "◕", "⚙", "▦" };
        foreach (var i in new[] { 5, 0, 1, 2, 3, 4 })
        {
            var index = i;
            var nav = new Button { Text = $"  {symbols[i]}     {names[i]}", Width = 180, Height = 42, FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 10), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 7) };
            nav.FlatAppearance.BorderSize = 0;
            nav.Click += (_, _) => SelectTab(index);
            navigation[i] = nav; navHost.Controls.Add(nav);
        }
        sidebar.Controls.Add(navHost);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 92, BackColor = sidebar.BackColor };
        footer.Controls.Add(new Label { Dock = DockStyle.Top, Height = 34, Text = "●  Ready to help", ForeColor = Color.FromArgb(116, 218, 171), TextAlign = ContentAlignment.MiddleLeft });
        hotkeyHint.Text = app.Data.OpenHotkey == "Off" ? "Ready when you are" : app.Data.OpenHotkey + "  to open";
        hotkeyHint.Dock = DockStyle.Bottom; hotkeyHint.Height = 32; hotkeyHint.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(hotkeyHint); sidebar.Controls.Add(footer);
        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Canvas, Padding = new Padding(20, 16, 20, 16) };
        workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 88)); workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
        sectionTitle.Location = new Point(4, 7); sectionDescription.Location = new Point(5, 46);
        heading.Controls.Add(sectionTitle); heading.Controls.Add(sectionDescription);
        workspace.Controls.Add(heading, 0, 0); workspace.Controls.Add(pageHost, 0, 1);
        shell.Controls.Add(sidebar, 0, 0); shell.Controls.Add(workspace, 1, 0); Controls.Add(shell);
        ApplyTheme(this);
        SelectTab(0);
        RefreshChat(); RefreshReminders(); RefreshUsage();
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { var enabled = 1; DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)); } catch { }
    }
    private static void ApplyTheme(Control root)
    {
        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case TextBox box: box.BackColor = InputSurface; box.ForeColor = Color.White; box.BorderStyle = BorderStyle.FixedSingle; break;
                case RichTextBox box: box.BackColor = Surface; box.ForeColor = Color.White; break;
                case ComboBox box:
                    box.BackColor = InputSurface; box.ForeColor = Color.White; box.FlatStyle = FlatStyle.Flat;
                    box.DrawMode = DrawMode.OwnerDrawFixed; box.ItemHeight = 25;
                    box.DrawItem += (_, e) =>
                    {
                        if (e.Index < 0 || e.Index >= box.Items.Count) return;
                        using var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Color.FromArgb(58, 68, 104) : InputSurface);
                        e.Graphics.FillRectangle(fill, e.Bounds);
                        TextRenderer.DrawText(e.Graphics, box.Items[e.Index]?.ToString() ?? "", box.Font, e.Bounds, Color.White,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    };
                    break;
                case ListBox box: box.BackColor = InputSurface; box.ForeColor = Color.White; box.BorderStyle = BorderStyle.None; break;
                case NumericUpDown number: number.BackColor = InputSurface; number.ForeColor = Color.White; break;
                case DateTimePicker date: date.CalendarMonthBackground = InputSurface; date.CalendarForeColor = Color.White; break;
            }
            ApplyTheme(control);
        }
    }
    private void UpdateNavigation()
    {
        var descriptions = new[] { "Your space to think, write, and get things done.", "Stay on top of what matters.", "Write and send with confidence.", "A clear view of your manual usage estimate.", "Make TOKI work the way you do.", "Your favorite apps, one click away." };
        var selected = selectedPage;
        sectionTitle.Text = pages[selected].Text;
        sectionDescription.Text = descriptions[selected];
        for (var i = 0; i < navigation.Length; i++)
        {
            navigation[i].BackColor = i == selected ? Color.FromArgb(40, 48, 78) : Color.FromArgb(18, 24, 37);
            navigation[i].ForeColor = i == selected ? Color.White : Muted;
            navigation[i].FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 48, 65);
        }
    }
    public void SelectTab(int index)
    {
        if (index < 0 || index >= pages.Count) return;
        if (pages.Count > 0) pages[selectedPage].Visible = false;
        selectedPage = index; pages[index].Visible = true; pages[index].BringToFront(); UpdateNavigation();
    }
    private Panel Page(string title, Control content) { var p = new Panel { Text = title, BackColor = Canvas, ForeColor = ForeColor, Padding = new Padding(4) }; p.Controls.Add(content); return p; }
    private static Button Button(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Padding = new Padding(10, 6, 10, 6), Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(0, 0, 8, 8) };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(135, 141, 255);
        b.Click += click; return b;
    }
    private static Button SecondaryButton(string text, EventHandler click)
    {
        var b = Button(text, click); b.BackColor = InputSurface; b.ForeColor = Color.FromArgb(226, 231, 241);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 57, 79); return b;
    }
    private Panel MakeShortcuts()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        actions.Controls.Add(Button("+ Add / manage apps", (_, _) => FocusShortcutSettings()));
        actions.Controls.Add(SecondaryButton("Compact launcher", (_, _) => app.ShowLauncher()));
        var status = new Label { Dock = DockStyle.Fill, ForeColor = Muted, Text = "Hover an icon for its name. Add apps from Settings.", TextAlign = ContentAlignment.MiddleLeft };
        shortcutGallery.ShortcutClicked += shortcut =>
        {
            try { ShortcutService.Open(shortcut); status.Text = "Opened " + shortcut.Name + "."; }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        layout.Controls.Add(actions, 0, 0); layout.Controls.Add(shortcutGallery, 0, 1); layout.Controls.Add(status, 0, 2);
        RefreshShortcutGallery();
        return Page("Shortcuts", layout);
    }
    internal void RefreshShortcutGallery() => shortcutGallery.SetShortcuts(app.Data.AppShortcuts);
    internal void FocusShortcutSettings()
    {
        SelectTab(4);
        if (settingsScroll != null && shortcutSettingsCard != null)
            settingsScroll.ScrollControlIntoView(shortcutSettingsCard);
    }
    private Panel MakeChat()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Canvas };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 112)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        history.Margin = new Padding(4, 8, 4, 10);
        history.Padding = new Padding(8, 8, 8, 8);
        history.Resize += (_, _) => RefreshChat();
        panel.Controls.Add(history, 0, 0);
        var composer = new SurfaceCard { Dock = DockStyle.Fill, Padding = new Padding(16, 13, 16, 10), Margin = new Padding(4, 0, 4, 8) };
        var sendRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Surface };
        sendRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sendRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        sendRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); sendRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        input.BackColor = Surface; input.ForeColor = Color.White; input.Margin = new Padding(0, 4, 0, 2);
        sendRow.Controls.Add(input, 0, 0); sendRow.SetColumnSpan(input, 2);
        sendRow.Controls.Add(new Label { Text = "Enter to send  ·  Shift+Enter for a new line", Dock = DockStyle.Fill, ForeColor = Muted, Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Surface };
        var sendButton = Button("Send  ↗", async (_, _) => await Send()); sendButton.Width = 82; sendButton.Height = 34; sendButton.Padding = Padding.Empty;
        var voiceButton = SecondaryButton("Voice", (_, _) => StartVoiceTyping()); voiceButton.Width = 75; voiceButton.Height = 34; voiceButton.Padding = Padding.Empty;
        controls.Controls.Add(sendButton); controls.Controls.Add(voiceButton); sendRow.Controls.Add(controls, 1, 1);
        composer.Controls.Add(sendRow);
        input.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await Send(); } };
        panel.Controls.Add(composer, 0, 1);
        var quick = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Canvas, Padding = new Padding(4, 3, 0, 0) };
        quick.Controls.Add(SecondaryButton("+ Reminder", (_, _) => SelectTab(1))); quick.Controls.Add(SecondaryButton("✉ Email", (_, _) => SelectTab(2)));
        quick.Controls.Add(SecondaryButton("▦ Shortcuts", (_, _) => SelectTab(5)));
        panel.Controls.Add(quick, 0, 2);
        return Page("Chat", panel);
    }
    private void ShowAppsMenu(Control anchor)
    {
        var menu = new ContextMenuStrip();
        if (app.Data.AppShortcuts.Count == 0)
            menu.Items.Add("Add app shortcuts in Settings", null, (_, _) => SelectTab(4));
        foreach (var shortcut in app.Data.AppShortcuts)
        {
            var item = shortcut;
            menu.Items.Add(item.Name, null, (_, _) =>
            {
                try
                {
                    if (!File.Exists(item.Path)) throw new FileNotFoundException("Shortcut target was not found.", item.Path);
                    Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "TOKI"); }
            });
        }
        menu.Show(anchor, 0, anchor.Height);
    }
    public void RefreshChat()
    {
        if (history.IsDisposed) return;
        history.SuspendLayout();
        while (history.Controls.Count > 0) { var old = history.Controls[0]; history.Controls.RemoveAt(0); old.Dispose(); }
        var rowWidth = Math.Max(330, history.ClientSize.Width - 34);
        if (app.Data.Messages.Count == 0)
        {
            var welcome = new SurfaceCard { Width = Math.Min(610, rowWidth - 12), Height = 160, Padding = new Padding(22), Margin = new Padding(4, 16, 0, 0) };
            welcome.Controls.Add(new Label { Text = "Ask TOKI anything\n\nWrite a question, set a reminder, draft an email, or open an app.\nTry “remind me to study at 7 PM” or “open Calculator”.", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11), ForeColor = Color.FromArgb(225, 232, 244) });
            history.Controls.Add(welcome);
        }
        if (app.Data.Messages.Count > 60)
            history.Controls.Add(new Label { Text = "Showing the latest 60 messages", Width = rowWidth, Height = 35, ForeColor = Muted, TextAlign = ContentAlignment.MiddleCenter });
        foreach (var m in app.Data.Messages.TakeLast(60))
        {
            var user = m.Role == "user";
            var bubbleWidth = Math.Min(650, Math.Max(280, (int)(rowWidth * .79)));
            var bodyWidth = bubbleWidth - 32;
            var bodyHeight = TextRenderer.MeasureText(m.Content, Font, new Size(bodyWidth, 10000), TextFormatFlags.WordBreak).Height + 7;
            var bubbleHeight = Math.Max(76, Math.Min(1000, bodyHeight + 52));
            var row = new Panel { Width = rowWidth, Height = bubbleHeight + 11, BackColor = Canvas, Margin = new Padding(0, 0, 0, 2) };
            var bubble = new SurfaceCard { Width = bubbleWidth, Height = bubbleHeight, Padding = new Padding(16, 12, 16, 12), BackColor = user ? Color.FromArgb(36, 43, 77) : Surface,
                Location = new Point(user ? rowWidth - bubbleWidth - 4 : 4, 3) };
            bubble.Controls.Add(new Label { Text = m.Content, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(234, 238, 247), Font = new Font("Segoe UI", 10.5f), TextAlign = ContentAlignment.TopLeft });
            bubble.Controls.Add(new Label { Text = $"{(user ? "YOU" : "TOKI")}  ·  {m.At:g}", Dock = DockStyle.Top, Height = 30, ForeColor = user ? Color.FromArgb(177, 185, 255) : Color.FromArgb(112, 220, 196), Font = new Font("Segoe UI Semibold", 9) });
            var copyMenu = new ContextMenuStrip(); copyMenu.Items.Add("Copy message", null, (_, _) => Clipboard.SetText(m.Content));
            bubble.ContextMenuStrip = copyMenu;
            foreach (Control child in bubble.Controls) child.ContextMenuStrip = copyMenu;
            row.Controls.Add(bubble); history.Controls.Add(row);
        }
        history.ResumeLayout();
        if (history.Controls.Count > 0) history.ScrollControlIntoView(history.Controls[^1]);
        lastChatRefresh = DateTime.UtcNow;
    }
    private async Task Send()
    {
        var prompt = input.Text.Trim(); if (prompt.Length == 0) return;
        input.Clear(); app.Data.Messages.Add(new ChatMessage { Content = prompt }); RefreshChat(); app.Save();
        var local = TryLocalAction(prompt);
        if (local != null) { Reply(local); return; }
        var key = Store.Unprotect(app.Data.EncryptedKey);
        if (key.Length == 0) { Reply("Add an API key in Settings to use AI chat. You can still create reminders and open supported apps."); return; }
        Reply("Thinking...");
        try
        {
            var messages = app.Data.Messages.Where(x => x.Content != "Thinking...").TakeLast(20).Select(x => new { role = x.Role, content = x.Content }).ToArray();
            var payload = JsonSerializer.Serialize(new { model = app.Data.Model, messages, stream = true });
            using var request = new HttpRequestMessage(HttpMethod.Post, app.Data.BaseUrl.TrimEnd('/') + "/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"Provider returned {(int)response.StatusCode}: {error[..Math.Min(error.Length, 280)]}");
            }
            var answer = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..]; if (data == "[DONE]") break;
                try
                {
                    using var part = JsonDocument.Parse(data);
                    var chunk = part.RootElement.GetProperty("choices")[0].GetProperty("delta");
                    if (chunk.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        answer.Append(content.GetString());
                        app.Data.Messages[^1].Content = answer.ToString();
                        if ((DateTime.UtcNow - lastChatRefresh).TotalMilliseconds > 280) RefreshChat();
                    }
                }
                catch (JsonException) { }
            }
            app.Data.Messages[^1].Content = answer.Length == 0 ? "No response." : answer.ToString(); app.Save(); RefreshChat(); Speak(app.Data.Messages[^1].Content);
        }
        catch (Exception ex) { app.Data.Messages[^1].Content = "AI request failed: " + ex.Message; app.Save(); RefreshChat(); }
    }
    private void Reply(string text) { app.Data.Messages.Add(new ChatMessage { Role = "assistant", Content = text }); app.Save(); RefreshChat(); Speak(text); }
    private void StartVoiceTyping()
    {
        input.Focus();
        const uint up = 0x0002;
        keybd_event(0x5B, 0, 0, UIntPtr.Zero); keybd_event(0x48, 0, 0, UIntPtr.Zero);
        keybd_event(0x48, 0, up, UIntPtr.Zero); keybd_event(0x5B, 0, up, UIntPtr.Zero);
    }
    private void Speak(string message)
    {
        if (!app.Data.SpeakResponses) return;
        _ = Task.Run(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice"); if (type == null) return;
                var voice = Activator.CreateInstance(type); if (voice == null) return;
                type.InvokeMember("Rate", System.Reflection.BindingFlags.SetProperty, null, voice, new object[] { app.Data.SpeechRate });
                type.InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, voice, new object[] { message, 0 });
                Marshal.ReleaseComObject(voice);
            }
            catch { }
        });
    }
    private string? TryLocalAction(string prompt)
    {
        if (prompt.StartsWith("remind me", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(prompt, @"(?:at|on)\s+(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm))", RegexOptions.IgnoreCase);
            if (match.Success && DateTime.TryParse(match.Groups["time"].Value, out var parsed))
            {
                var due = DateTime.Today.Add(parsed.TimeOfDay); if (due <= DateTime.Now) due = due.AddDays(1);
                var recurrence = "None";
                var weekday = Regex.Match(prompt, @"every\s+(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)", RegexOptions.IgnoreCase);
                if (weekday.Success)
                {
                    recurrence = "Weekly";
                    var target = Enum.Parse<DayOfWeek>(weekday.Groups[1].Value, true);
                    due = DateTime.Today.AddDays(((int)target - (int)DateTime.Today.DayOfWeek + 7) % 7).Add(parsed.TimeOfDay);
                    if (due <= DateTime.Now) due = due.AddDays(7);
                }
                else if (Regex.IsMatch(prompt, @"\bevery\s+day\b", RegexOptions.IgnoreCase)) recurrence = "Daily";
                else if (prompt.Contains("tomorrow", StringComparison.OrdinalIgnoreCase)) due = DateTime.Today.AddDays(1).Add(parsed.TimeOfDay);
                var text = prompt[..match.Index].Replace("Remind me", "", StringComparison.OrdinalIgnoreCase);
                text = Regex.Replace(text, @"\bevery\s+(?:day|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\b", "", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"\btomorrow\b", "", RegexOptions.IgnoreCase).Trim(' ', ',', '.');
                app.Data.Reminders.Add(new Reminder { Text = text, Due = due, Recurrence = recurrence }); app.Save(); RefreshReminders();
                return $"Reminder set for {due:f}: {text}";
            }
            SelectTab(1); return "Choose a date and time in Reminders to finish setting this reminder.";
        }
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["calculator"] = "calc.exe", ["file explorer"] = "explorer.exe", ["task manager"] = "taskmgr.exe", ["downloads"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), ["vs code"] = "code", ["chrome"] = "chrome" };
        if (prompt.StartsWith("open ", StringComparison.OrdinalIgnoreCase))
        {
            var target = prompt[5..].Trim().TrimEnd('.');
            var custom = app.Data.AppShortcuts.FirstOrDefault(x => x.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (custom != null)
            {
                if (!File.Exists(custom.Path)) return $"The shortcut for {custom.Name} no longer points to an existing app. Update it in Settings.";
                try { ShortcutService.Open(custom); return $"Opened {custom.Name}."; }
                catch (Exception ex) { return $"Could not open {custom.Name}: {ex.Message}"; }
            }
            if (!targets.TryGetValue(target, out var path)) return "I can open Calculator, File Explorer, Task Manager, Downloads, VS Code, or Chrome.";
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return $"Opened {target}."; }
            catch (Exception ex) { return $"Could not open {target}: {ex.Message}"; }
        }
        if (prompt.Equals("take a screenshot", StringComparison.OrdinalIgnoreCase) || prompt.Equals("screenshot", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bounds = SystemInformation.VirtualScreen;
                using var bitmap = new Bitmap(bounds.Width, bounds.Height);
                using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TOKI");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                return "Screenshot saved to " + path;
            }
            catch (Exception ex) { return "Screenshot failed: " + ex.Message; }
        }
        if (prompt.Equals("read clipboard", StringComparison.OrdinalIgnoreCase))
            return Clipboard.ContainsText() ? Clipboard.GetText() : "The clipboard has no text.";
        if (prompt.StartsWith("copy to clipboard ", StringComparison.OrdinalIgnoreCase))
        {
            Clipboard.SetText(prompt[18..]); return "Copied to clipboard.";
        }
        if (prompt.StartsWith("compose email", StringComparison.OrdinalIgnoreCase))
        {
            SelectTab(2); return "The email composer is open. Review the recipient, subject, and body before sending in your mail app.";
        }
        return null;
    }
    private Panel MakeReminders()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Canvas };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 162)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var editorCard = new SurfaceCard { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 12), Margin = new Padding(4, 4, 4, 12) };
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 3, BackColor = Surface };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        editor.Controls.Add(new Label { Text = "Create a reminder", Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 12), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        editor.SetColumnSpan(editor.GetControlFromPosition(0, 0)!, 3);
        var title = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "What should I remind you about?", Margin = new Padding(0, 3, 10, 3) };
        var due = new DateTimePicker { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "dd MMM yyyy  hh:mm tt", Value = DateTime.Now.AddHours(1), Margin = new Padding(0, 3, 10, 3) };
        var repeat = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 0, 3) };
        repeat.Items.AddRange(new object[] { "None", "Daily", "Weekly" }); repeat.SelectedIndex = 0;
        editor.Controls.Add(title, 0, 1); editor.Controls.Add(due, 1, 1); editor.Controls.Add(repeat, 2, 1);
        var add = Button("Add reminder", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text) || due.Value <= DateTime.Now) { MessageBox.Show("Enter a description and a future date."); return; }
            app.Data.Reminders.Add(new Reminder { Text = title.Text.Trim(), Due = due.Value, Recurrence = repeat.Text }); app.Save(); title.Clear(); RefreshReminders();
        });
        add.Dock = DockStyle.Left; add.Height = 36; add.Margin = new Padding(0, 8, 0, 0);
        editor.Controls.Add(add, 0, 2); editorCard.Controls.Add(editor);
        root.Controls.Add(editorCard, 0, 0);
        root.Controls.Add(new Label { Text = "UPCOMING & COMPLETED", Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 9), ForeColor = Muted, Padding = new Padding(6, 12, 0, 0) }, 0, 1);
        reminders.BackColor = Canvas; reminders.Padding = new Padding(4, 0, 8, 0);
        reminders.Resize += (_, _) => ResizeReminderCards();
        root.Controls.Add(reminders, 0, 2);
        return Page("Reminders", root);
    }
    private void ResizeReminderCards()
    {
        var width = Math.Max(340, reminders.ClientSize.Width - 30);
        foreach (Control card in reminders.Controls) card.Width = width;
    }
    private void RefreshReminders()
    {
        reminders.SuspendLayout(); reminders.Controls.Clear();
        if (app.Data.Reminders.Count == 0)
            reminders.Controls.Add(new Label { Text = "No reminders yet. Add one above or ask TOKI in Chat.", AutoSize = true, ForeColor = Muted, Padding = new Padding(10, 10, 0, 0) });
        foreach (var r in app.Data.Reminders.OrderBy(x => x.Completed).ThenBy(x => x.Due).ToList())
        {
            var row = new SurfaceCard { Width = Math.Max(340, reminders.ClientSize.Width - 30), Height = 88, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(17, 11, 15, 10) };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = r.Completed ? 100 : 250, BackColor = Surface, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            row.Controls.Add(actions);
            var detail = new Label { Text = $"{r.Text}\n{r.Due:ddd, dd MMM yyyy · h:mm tt}   ·   {(r.Completed ? "Completed" : r.Recurrence)}", Dock = DockStyle.Fill, ForeColor = r.Completed ? Muted : Color.White, Font = new Font("Segoe UI", 10), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            row.Controls.Add(detail);
            if (!r.Completed)
            {
                actions.Controls.Add(SecondaryButton("Snooze", (_, _) => { r.Due = DateTime.Now.AddMinutes(10); app.Save(); RefreshReminders(); }));
                actions.Controls.Add(SecondaryButton("Done", (_, _) => { r.Completed = true; app.Save(); RefreshReminders(); }));
            }
            actions.Controls.Add(SecondaryButton("Delete", (_, _) => { app.Data.Reminders.Remove(r); app.Save(); RefreshReminders(); }));
            reminders.Controls.Add(row);
        }
        reminders.ResumeLayout(); ResizeReminderCards();
    }
    private Panel MakeEmail()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false };
        panel.Controls.Add(new Label { Text = "Email composer", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        var identityRow = new FlowLayoutPanel { Width = 390, Height = 46, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        identityRow.Controls.Add(accountAvatar); identityRow.Controls.Add(accountStatus); panel.Controls.Add(identityRow);
        accountBox = new ComboBox { Width = 390, DropDownStyle = ComboBoxStyle.DropDownList };
        accountBox.SelectedIndexChanged += (_, _) =>
        {
            var selected = SelectedAccount();
            app.Data.ActiveEmailAccountId = selected?.Id; app.Save(); UpdateAccountDisplay();
        };
        RefreshAccounts(); panel.Controls.Add(new Label { Text = "Change active account", AutoSize = true }); panel.Controls.Add(accountBox);
        gmailButton = Button("Add / change Gmail account", async (_, _) => await ConnectEmail("Gmail"));
        outlookButton = Button("Add / change Outlook account", async (_, _) => await ConnectEmail("Outlook"));
        cancelConnectionButton = Button("Cancel sign-in", (_, _) => connectionCancellation?.Cancel());
        cancelConnectionButton.Enabled = false;
        panel.Controls.Add(gmailButton); panel.Controls.Add(outlookButton); panel.Controls.Add(cancelConnectionButton); panel.Controls.Add(connectionStatus);
        panel.Controls.Add(Button("Disconnect selected", (_, _) =>
        {
            var account = SelectedAccount(); if (account == null) return;
            if (MessageBox.Show(this, $"Remove {account.Address} from TOKI?", "Disconnect", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            app.Data.Accounts.Remove(account); app.Data.ActiveEmailAccountId = null; app.Save(); RefreshAccounts();
        }));
        var recipient = new TextBox { Width = 390, PlaceholderText = "Recipient email" };
        var subject = new TextBox { Width = 390, PlaceholderText = "Subject" };
        var body = new TextBox { Width = 390, Height = 240, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Write or edit your message" };
        panel.Controls.Add(recipient); panel.Controls.Add(subject); panel.Controls.Add(body);
        panel.Controls.Add(Button("Read latest inbox", async (_, _) => await ShowInbox(false)));
        panel.Controls.Add(Button("Read unread inbox", async (_, _) => await ShowInbox(true)));
        panel.Controls.Add(Button("Send from connected account", async (_, _) =>
        {
            var account = SelectedAccount(); if (account == null) { MessageBox.Show("Connect or select an account first."); return; }
            if (!Regex.IsMatch(recipient.Text.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) { MessageBox.Show("Enter a valid recipient address."); return; }
            if (MessageBox.Show($"From: {account.Address}\nTo: {recipient.Text}\nSubject: {subject.Text}\n\n{body.Text}", "Review and approve email", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { await email.SendAsync(account, recipient.Text.Trim(), subject.Text, body.Text); app.Notify("TOKI email", "Email sent."); MessageBox.Show("Email sent."); }
            catch (Exception ex) { app.Notify("TOKI email", "Email sending failed."); MessageBox.Show(ex.Message); }
        }));
        panel.Controls.Add(Button("Open in default mail app", (_, _) =>
        {
            if (!Regex.IsMatch(recipient.Text.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) { MessageBox.Show("Enter a valid recipient address."); return; }
            var uri = $"mailto:{Uri.EscapeDataString(recipient.Text.Trim())}?subject={Uri.EscapeDataString(subject.Text)}&body={Uri.EscapeDataString(body.Text)}";
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }));
        panel.Controls.Add(new Label { Text = "OAuth app client IDs are configured in Settings. TOKI never asks for your email password. Connected sending always requires preview approval.", Width = 390, Height = 60 });
        return Page("Email", panel);
    }
    private Panel MakePremiumEmail()
    {
        var gallery = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, BackColor = Canvas };
        var accountCard = new SurfaceCard { Height = 394, Padding = new Padding(20), Margin = new Padding(8) };
        var composeCard = new SurfaceCard { Height = 606, Padding = new Padding(20), Margin = new Padding(8) };
        var accountContent = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Surface };
        accountContent.Controls.Add(new Label { Text = "Connected accounts", Height = 34, Width = 440, Font = new Font("Segoe UI Semibold", 13), ForeColor = Color.White });
        var identity = new FlowLayoutPanel { Height = 56, Width = 440, BackColor = Surface, WrapContents = false };
        identity.Controls.Add(accountAvatar); identity.Controls.Add(accountStatus); accountContent.Controls.Add(identity);
        accountContent.Controls.Add(new Label { Text = "Active sender", Height = 26, Width = 440, ForeColor = Color.FromArgb(161, 175, 202) });
        accountBox = new ComboBox { Width = 440, Height = 38, DropDownStyle = ComboBoxStyle.DropDownList };
        accountEmpty = new Label { Text = "No account selected", Width = 440, Height = 34, BackColor = InputSurface, ForeColor = Muted, Padding = new Padding(10, 7, 0, 0) };
        accountBox.SelectedIndexChanged += (_, _) =>
        {
            var selected = SelectedAccount(); app.Data.ActiveEmailAccountId = selected?.Id; app.Save(); UpdateAccountDisplay();
        };
        accountContent.Controls.Add(accountBox); accountContent.Controls.Add(accountEmpty); RefreshAccounts();
        var connectionButtons = new FlowLayoutPanel { Width = 440, Height = 54, BackColor = Surface, WrapContents = false };
        gmailButton = SecondaryButton("+ Gmail", async (_, _) => await ConnectEmail("Gmail"));
        outlookButton = SecondaryButton("+ Outlook", async (_, _) => await ConnectEmail("Outlook"));
        connectionButtons.Controls.Add(gmailButton); connectionButtons.Controls.Add(outlookButton); accountContent.Controls.Add(connectionButtons);
        connectionStatus.Text = SelectedAccount() == null ? "Choose an account to connect." : "Your account is ready to use.";
        accountContent.Controls.Add(connectionStatus);
        var accountActions = new FlowLayoutPanel { Width = 440, Height = 48, BackColor = Surface, WrapContents = false };
        cancelConnectionButton = SecondaryButton("Cancel sign-in", (_, _) => connectionCancellation?.Cancel()); cancelConnectionButton.Enabled = false;
        accountActions.Controls.Add(cancelConnectionButton);
        accountActions.Controls.Add(SecondaryButton("Disconnect", (_, _) =>
        {
            var account = SelectedAccount(); if (account == null) return;
            if (MessageBox.Show(this, $"Remove {account.Address} from TOKI?", "Disconnect", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            app.Data.Accounts.Remove(account); app.Data.ActiveEmailAccountId = null; app.Save(); RefreshAccounts();
            connectionStatus.Text = "Account disconnected.";
        }));
        accountContent.Controls.Add(accountActions); accountCard.Controls.Add(accountContent);

        var compose = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Surface };
        compose.Controls.Add(new Label { Text = "Compose email", Height = 36, Width = 440, Font = new Font("Segoe UI Semibold", 13), ForeColor = Color.White });
        compose.Controls.Add(new Label { Text = "Write, review, and send from your connected account.", Height = 34, Width = 440, ForeColor = Color.FromArgb(161, 175, 202) });
        var recipient = new TextBox { Width = 440, Height = 36, PlaceholderText = "To · recipient@example.com" };
        var subject = new TextBox { Width = 440, Height = 36, PlaceholderText = "Subject" };
        var body = new TextBox { Width = 440, Height = 240, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Write your message..." };
        compose.Controls.Add(recipient); compose.Controls.Add(subject); compose.Controls.Add(body);
        var readActions = new FlowLayoutPanel { Width = 440, Height = 53, BackColor = Surface, WrapContents = false };
        readActions.Controls.Add(SecondaryButton("Latest inbox", async (_, _) => await ShowInbox(false)));
        readActions.Controls.Add(SecondaryButton("Unread inbox", async (_, _) => await ShowInbox(true))); compose.Controls.Add(readActions);
        var sendActions = new FlowLayoutPanel { Width = 440, Height = 53, BackColor = Surface, WrapContents = false };
        sendActions.Controls.Add(Button("Review & send", async (_, _) =>
        {
            var account = SelectedAccount(); if (account == null) { MessageBox.Show(this, "Connect or select an account first.", "TOKI"); return; }
            if (!Regex.IsMatch(recipient.Text.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) { MessageBox.Show(this, "Enter a valid recipient address.", "TOKI"); return; }
            if (MessageBox.Show(this, $"From: {account.Address}\nTo: {recipient.Text}\nSubject: {subject.Text}\n\n{body.Text}", "Review and approve email", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { await email.SendAsync(account, recipient.Text.Trim(), subject.Text, body.Text); app.Notify("TOKI email", "Email sent."); MessageBox.Show(this, "Email sent.", "TOKI"); }
            catch (Exception ex) { app.Notify("TOKI email", "Email sending failed."); MessageBox.Show(this, ex.Message, "TOKI"); }
        }));
        sendActions.Controls.Add(SecondaryButton("Open in mail app", (_, _) =>
        {
            if (!Regex.IsMatch(recipient.Text.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) { MessageBox.Show(this, "Enter a valid recipient address.", "TOKI"); return; }
            var uri = $"mailto:{Uri.EscapeDataString(recipient.Text.Trim())}?subject={Uri.EscapeDataString(subject.Text)}&body={Uri.EscapeDataString(body.Text)}";
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "TOKI"); }
        }));
        compose.Controls.Add(sendActions);
        compose.Controls.Add(new Label { Text = "TOKI asks you to approve every email before sending.", Width = 440, Height = 32, ForeColor = Color.FromArgb(161, 175, 202) });
        composeCard.Controls.Add(compose);
        gallery.Controls.Add(accountCard); gallery.Controls.Add(composeCard);
        void ArrangeCards()
        {
            var width = Math.Max(430, gallery.ClientSize.Width);
            var sideBySide = width >= 790;
            var cardWidth = sideBySide ? Math.Min(520, (width - 56) / 2) : Math.Min(620, width - 36);
            var groupWidth = sideBySide ? 2 * (cardWidth + 16) : cardWidth + 16;
            gallery.Padding = new Padding(Math.Max(8, (width - groupWidth) / 2), 12, 8, 12);
            accountCard.Width = cardWidth; composeCard.Width = cardWidth;
            var innerWidth = cardWidth - 48;
            identity.Width = innerWidth; accountStatus.Width = innerWidth - 52; accountBox.Width = innerWidth;
            accountEmpty.Width = innerWidth;
            connectionStatus.Width = innerWidth; connectionButtons.Width = innerWidth; accountActions.Width = innerWidth;
            recipient.Width = innerWidth; subject.Width = innerWidth; body.Width = innerWidth;
            readActions.Width = innerWidth; sendActions.Width = innerWidth;
        }
        gallery.Resize += (_, _) => ArrangeCards();
        return Page("Email", gallery);
    }
    private EmailAccount? SelectedAccount() => accountBox?.SelectedIndex >= 0 && accountBox.SelectedIndex < app.Data.Accounts.Count ? app.Data.Accounts[accountBox.SelectedIndex] : null;
    private void RefreshAccounts()
    {
        if (accountBox == null) return;
        accountBox.Items.Clear(); foreach (var a in app.Data.Accounts) accountBox.Items.Add($"{a.Provider} • {a.Address}");
        accountBox.Visible = accountBox.Items.Count > 0;
        if (accountEmpty != null) accountEmpty.Visible = accountBox.Items.Count == 0;
        if (accountBox.Items.Count > 0)
        {
            var selected = app.Data.Accounts.FindIndex(a => a.Id == app.Data.ActiveEmailAccountId);
            accountBox.SelectedIndex = selected >= 0 ? selected : 0;
        }
        UpdateAccountDisplay();
    }
    private void UpdateAccountDisplay()
    {
        var selected = SelectedAccount();
        accountAvatar.Provider = selected?.Provider;
        accountStatus.Text = selected == null ? "No email account connected" : $"Connected to {selected.Provider}\n{selected.Address}";
    }
    private async Task ConnectEmail(string provider)
    {
        if (connectionCancellation != null) return;
        connectionCancellation = new CancellationTokenSource();
        gmailButton!.Enabled = false; outlookButton!.Enabled = false; cancelConnectionButton!.Enabled = true;
        connectionStatus.Text = "Opening browser sign-in...";
        try
        {
            var progress = new Progress<string>(message => connectionStatus.Text = message);
            var account = await email.ConnectAsync(provider, progress, connectionCancellation.Token);
            RefreshAccounts(); connectionStatus.Text = $"Connected: {account.Address}";
            MessageBox.Show(this, $"Connected {account.Address}.", "TOKI email");
        }
        catch (OperationCanceledException)
        {
            connectionStatus.Text = "Sign-in stopped or timed out. Check the browser, then try again.";
            MessageBox.Show(this, connectionStatus.Text, "TOKI email");
        }
        catch (Exception ex)
        {
            connectionStatus.Text = "Sign-in failed. See details.";
            MessageBox.Show(this, "Sign-in failed: " + ex.Message, "TOKI email");
        }
        finally
        {
            connectionCancellation.Dispose(); connectionCancellation = null;
            gmailButton.Enabled = true; outlookButton.Enabled = true; cancelConnectionButton.Enabled = false;
        }
    }
    private async Task ShowInbox(bool unread)
    {
        var account = SelectedAccount(); if (account == null) { MessageBox.Show("Connect or select an account first."); return; }
        try { var messages = await email.InboxAsync(account, unread); MessageBox.Show(messages.Count == 0 ? "No messages found." : string.Join("\n\n────────\n\n", messages), unread ? "Unread inbox" : "Latest inbox"); }
        catch (Exception ex) { MessageBox.Show("Inbox failed: " + ex.Message); }
    }
    private Panel MakeUsage()
    {
        var gallery = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false, BackColor = Canvas };
        var card = new SurfaceCard { Height = 550, Padding = new Padding(24), Margin = new Padding(4, 4, 4, 12) };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Surface };
        panel.Controls.Add(new Label { Text = "Manual usage tracker", Width = 600, Height = 38, Font = new Font("Segoe UI Semibold", 14), ForeColor = Color.White });
        panel.Controls.Add(new Label { Text = "Live ChatGPT subscription usage is unavailable. Enter your own limit and used count to see an estimate. API usage is separate.", Width = 600, Height = 58, ForeColor = Muted });
        var plan = new TextBox { Width = 250, PlaceholderText = "Plan (optional)", Text = app.Data.Usage.Plan };
        var limit = new NumericUpDown { Width = 140, Maximum = 1000000, Value = Math.Clamp(app.Data.Usage.Limit, 0, 1000000) };
        var used = new NumericUpDown { Width = 140, Maximum = 1000000, Value = Math.Clamp(app.Data.Usage.Used, 0, 1000000) };
        var reset = new DateTimePicker { Width = 220, Value = app.Data.Usage.Reset > DateTime.Now ? app.Data.Usage.Reset : DateTime.Now.AddMonths(1) };
        var warning = new NumericUpDown { Width = 100, Minimum = 1, Maximum = 100, Value = Math.Clamp(app.Data.Usage.Warning, 1, 100) };
        var critical = new NumericUpDown { Width = 100, Minimum = 1, Maximum = 100, Value = Math.Clamp(app.Data.Usage.Critical, 1, 100) };
        panel.Controls.Add(new Label { Text = "Plan", AutoSize = true }); panel.Controls.Add(plan);
        panel.Controls.Add(new Label { Text = "Manual limit", AutoSize = true }); panel.Controls.Add(limit);
        panel.Controls.Add(new Label { Text = "Manual used count", AutoSize = true }); panel.Controls.Add(used);
        panel.Controls.Add(new Label { Text = "Reset date", AutoSize = true }); panel.Controls.Add(reset);
        panel.Controls.Add(new Label { Text = "Warning %", AutoSize = true }); panel.Controls.Add(warning);
        panel.Controls.Add(new Label { Text = "Critical %", AutoSize = true }); panel.Controls.Add(critical);
        panel.Controls.Add(Button("Save estimate", (_, _) =>
        {
            if (critical.Value <= warning.Value) { MessageBox.Show("Critical threshold must exceed warning threshold."); return; }
            var u = app.Data.Usage; u.Plan = plan.Text; u.Limit = (int)limit.Value; u.Used = (int)used.Value; u.Reset = reset.Value;
            u.Warning = (int)warning.Value; u.Critical = (int)critical.Value; u.WarningSent = false; u.CriticalSent = false;
            app.Save(); RefreshUsage();
        }));
        usageStatus.Font = new Font("Segoe UI Semibold", 11); usageStatus.ForeColor = Color.White;
        panel.Controls.Add(usageStatus);
        var link = new LinkLabel { Text = "Open ChatGPT settings ↗", AutoSize = true, LinkColor = Color.FromArgb(147, 170, 255) };
        link.Click += (_, _) => Process.Start(new ProcessStartInfo("https://chatgpt.com/settings/usage") { UseShellExecute = true });
        panel.Controls.Add(link);
        card.Controls.Add(panel); gallery.Controls.Add(card);
        gallery.Resize += (_, _) => { card.Width = Math.Min(720, Math.Max(420, gallery.ClientSize.Width - 28)); gallery.Padding = new Padding(Math.Max(4, (gallery.ClientSize.Width - card.Width - 8) / 2), 0, 0, 0); };
        return Page("Usage", gallery);
    }
    private void RefreshUsage()
    {
        var u = app.Data.Usage;
        usageStatus.Text = u.Limit <= 0 ? "No manual usage estimate set." : $"Manual estimate: {u.Used}/{u.Limit} ({100.0 * u.Used / u.Limit:0}%)";
    }
    private Panel MakeSettings()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false, BackColor = Canvas };
        settingsScroll = panel;
        var cards = new List<SurfaceCard>();
        FlowLayoutPanel Section(string title, int height)
        {
            var card = new SurfaceCard { Height = height, Padding = new Padding(22, 18, 22, 14), Margin = new Padding(4, 4, 4, 12) };
            if (title == "App shortcuts") shortcutSettingsCard = card;
            var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Surface };
            content.Controls.Add(new Label { Text = title, Width = 600, Height = 38, Font = new Font("Segoe UI Semibold", 13), ForeColor = Color.White });
            card.Controls.Add(content); panel.Controls.Add(card); cards.Add(card); return content;
        }
        var launcherSection = Section("One-touch access", 390);
        var shortcutSection = Section("App shortcuts", 480);
        var generalSection = Section("General & voice", 450);
        var aiSection = Section("AI provider", 390);
        var emailSection = Section("Email connections", 260);
        var privacySection = Section("Privacy & data", 230);
        launcherSection.Controls.Add(new Label { Text = "Choose what opens when you click the floating robot or press your TOKI hotkey.", Width = 610, Height = 42, ForeColor = Muted });
        launcherSection.Controls.Add(new Label { Text = "Floating click opens", AutoSize = true });
        var primary = new ComboBox { Width = 390, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Floating click opens" };
        primary.Items.AddRange(PrimaryViews.Names);
        primary.SelectedItem = PrimaryViews.Names.Contains(app.Data.PrimaryView) ? app.Data.PrimaryView : "Shortcuts";
        var primaryStatus = new Label { Text = "Shortcuts opens the compact icon launcher. ☰ opens the full TOKI app.", Width = 610, Height = 38, ForeColor = Muted };
        primary.SelectedIndexChanged += (_, _) =>
        {
            app.Data.PrimaryView = primary.Text; app.Save();
            primaryStatus.Text = "Saved. The floating robot now opens " + primary.Text + ".";
        };
        launcherSection.Controls.Add(primary); launcherSection.Controls.Add(primaryStatus);
        var floatAnimation = new CheckBox { Text = "Gentle floating animation", Checked = app.Data.FloatAnimation, AutoSize = true };
        floatAnimation.CheckedChanged += (_, _) => { app.Data.FloatAnimation = floatAnimation.Checked; app.Save(); app.ApplyAppearance(); };
        launcherSection.Controls.Add(floatAnimation);
        launcherSection.Controls.Add(new Label { Text = "Robot size (40–192 pixels, applies immediately)", AutoSize = true });
        var size = new NumericUpDown { Minimum = 40, Maximum = 192, Increment = 4, Value = Math.Clamp(app.Data.OrbSize, 40, 192), Width = 140, AccessibleName = "Floating robot size in pixels" };
        size.ValueChanged += (_, _) => { app.Data.OrbSize = (int)size.Value; app.Save(); app.ApplyAppearance(); };
        launcherSection.Controls.Add(size);
        launcherSection.Controls.Add(SecondaryButton("Preview one-touch view", (_, _) => app.TogglePrimary()));
        panel.Resize += (_, _) =>
        {
            var cardWidth = Math.Min(740, Math.Max(470, panel.ClientSize.Width - 28));
            panel.Padding = new Padding(Math.Max(4, (panel.ClientSize.Width - cardWidth - 8) / 2), 0, 0, 0);
            foreach (var card in cards)
            {
                card.Width = cardWidth;
                foreach (Control content in card.Controls)
                    foreach (Control item in content.Controls)
                    {
                        if (item is TextBox or ComboBox or ListBox) item.Width = cardWidth - 54;
                        else if (item is Label label && !label.AutoSize) label.Width = cardWidth - 54;
                    }
            }
        };
        var googleClient = new TextBox { Width = 280, PlaceholderText = "Google OAuth client ID", Text = app.Data.GoogleClientId };
        string? importedGoogleSecret = null;
        string? importedGoogleClientId = null;
        var microsoftClient = new TextBox { Width = 280, PlaceholderText = "Microsoft OAuth client ID", Text = app.Data.MicrosoftClientId };
        providerBox.Items.AddRange(new object[] { "OpenAI", "OpenAI-compatible" }); providerBox.SelectedItem = app.Data.Provider;
        if (providerBox.SelectedIndex < 0) providerBox.SelectedIndex = 0;
        modelBox.Text = app.Data.Model; urlBox.Text = app.Data.BaseUrl;
        aiSection.Controls.Add(providerBox);
        aiSection.Controls.Add(new Label { Text = "Model", AutoSize = true }); aiSection.Controls.Add(modelBox);
        aiSection.Controls.Add(new Label { Text = "API base URL (HTTPS)", AutoSize = true }); aiSection.Controls.Add(urlBox);
        aiSection.Controls.Add(new Label { Text = "API key (leave blank to keep existing)", AutoSize = true }); aiSection.Controls.Add(keyBox);
        aiSection.Controls.Add(new Label { Text = "ChatGPT subscriptions and API billing are separate. TOKI only uses an API when you configure a key and send a chat request.", Width = 610, Height = 52, ForeColor = Muted });
        emailSection.Controls.Add(new Label { Text = "Connect Gmail or Outlook from the Email page after adding the provider's OAuth client ID.", Width = 610, Height = 38, ForeColor = Muted });
        emailSection.Controls.Add(googleClient); emailSection.Controls.Add(microsoftClient);
        emailSection.Controls.Add(SecondaryButton("Import Google desktop OAuth JSON", (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Google OAuth JSON (*.json)|*.json", Title = "Select Google Desktop OAuth credentials" };
            if (picker.ShowDialog() != DialogResult.OK) return;
            try
            {
                using var credentials = JsonDocument.Parse(File.ReadAllText(picker.FileName));
                var installed = credentials.RootElement.GetProperty("installed");
                googleClient.Text = installed.GetProperty("client_id").GetString() ?? "";
                if (googleClient.Text.Length == 0) throw new InvalidOperationException("No client ID found.");
                importedGoogleSecret = installed.GetProperty("client_secret").GetString();
                if (string.IsNullOrWhiteSpace(importedGoogleSecret)) throw new InvalidOperationException("No client secret found in the Desktop app OAuth JSON.");
                importedGoogleClientId = googleClient.Text;
                MessageBox.Show(this, "Google Desktop OAuth credentials imported. Click Save settings, then connect Gmail in Email.", "TOKI");
            }
            catch (Exception ex) { MessageBox.Show("This must be a Google Desktop app OAuth JSON file: " + ex.Message); }
        }));
        var startup = new CheckBox { Text = "Start with Windows", Checked = app.Data.StartWithWindows, AutoSize = true };
        var top = new CheckBox { Text = "Always on top", Checked = app.Data.TopMost, AutoSize = true };
        var notifications = new CheckBox { Text = "Notifications and sound", Checked = app.Data.Notifications, AutoSize = true };
        var speak = new CheckBox { Text = "Speak TOKI responses", Checked = app.Data.SpeakResponses, AutoSize = true };
        var speechRate = new NumericUpDown { Minimum = -10, Maximum = 10, Value = Math.Clamp(app.Data.SpeechRate, -10, 10), Width = 100 };
        var hotkey = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
        hotkey.Items.AddRange(new object[] { "Ctrl+Alt+T", "Ctrl+Shift+Space", "Off" });
        hotkey.SelectedItem = app.Data.OpenHotkey;
        if (hotkey.SelectedIndex < 0) hotkey.SelectedIndex = 0;
        generalSection.Controls.Add(startup); generalSection.Controls.Add(top); generalSection.Controls.Add(notifications);
        generalSection.Controls.Add(speak); generalSection.Controls.Add(new Label { Text = "Speech speed", AutoSize = true }); generalSection.Controls.Add(speechRate);
        generalSection.Controls.Add(new Label { Text = "Keyboard shortcut to open TOKI", AutoSize = true }); generalSection.Controls.Add(hotkey);
        generalSection.Controls.Add(Button("Save settings", (_, _) =>
        {
            if (!Uri.TryCreate(urlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https") { MessageBox.Show("Use an HTTPS API base URL."); return; }
            if (!app.SetOpenHotkey(hotkey.Text)) { MessageBox.Show(this, "Windows is already using that shortcut. Choose another one.", "TOKI"); return; }
            app.Data.OpenHotkey = hotkey.Text;
            hotkeyHint.Text = hotkey.Text == "Off" ? "Ready when you are" : hotkey.Text + "  to open";
            app.Data.Provider = providerBox.Text; app.Data.Model = modelBox.Text.Trim(); app.Data.BaseUrl = uri.ToString().TrimEnd('/');
            if (keyBox.Text.Length > 0) { app.Data.EncryptedKey = Store.Protect(keyBox.Text); keyBox.Clear(); }
            app.Data.TopMost = top.Checked; app.Data.Notifications = notifications.Checked; app.Data.OrbSize = (int)size.Value;
            app.Data.SpeakResponses = speak.Checked; app.Data.SpeechRate = (int)speechRate.Value;
            var newGoogleClientId = googleClient.Text.Trim();
            if (importedGoogleSecret != null && newGoogleClientId != importedGoogleClientId)
            {
                MessageBox.Show(this, "The Google client ID changed after importing the JSON. Import the matching Desktop OAuth JSON again.", "TOKI");
                return;
            }
            if (importedGoogleSecret != null) app.Data.EncryptedGoogleClientSecret = Store.Protect(importedGoogleSecret);
            else if (!newGoogleClientId.Equals(app.Data.GoogleClientId, StringComparison.Ordinal)) app.Data.EncryptedGoogleClientSecret = "";
            app.Data.GoogleClientId = newGoogleClientId; app.Data.MicrosoftClientId = microsoftClient.Text.Trim();
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (startup.Checked) run?.SetValue("TOKI", $"\"{Environment.ProcessPath}\""); else run?.DeleteValue("TOKI", false);
                app.Data.StartWithWindows = startup.Checked;
            }
            catch (Exception ex) { MessageBox.Show("Startup setting failed: " + ex.Message); }
            app.Save(); app.ApplyAppearance(); MessageBox.Show("Settings saved and applied.");
        }));
        privacySection.Controls.Add(SecondaryButton("Delete API key", (_, _) => { app.Data.EncryptedKey = ""; app.Save(); MessageBox.Show("API key deleted."); }));
        privacySection.Controls.Add(SecondaryButton("Clear chat history", (_, _) => { if (MessageBox.Show("Delete local chat history?", "TOKI", MessageBoxButtons.YesNo) == DialogResult.Yes) { app.Data.Messages.Clear(); app.Save(); RefreshChat(); } }));
        shortcutSection.Controls.Add(new Label { Text = "Add an .exe or Windows .lnk. Its app icon appears in the floating launcher and Shortcuts page. Hover an icon to see its name.", Width = 610, Height = 48, ForeColor = Muted });
        var shortcuts = new ListBox { Width = 390, Height = 108 };
        void RefreshShortcuts()
        {
            shortcuts.Items.Clear();
            foreach (var item in app.Data.AppShortcuts) shortcuts.Items.Add($"{item.Name}  ·  {Path.GetFileName(item.Path)}");
        }
        RefreshShortcuts(); shortcutSection.Controls.Add(shortcuts);
        var shortcutName = new TextBox { Width = 390, PlaceholderText = "Name, e.g. Photoshop" };
        var shortcutPath = new TextBox { Width = 390, PlaceholderText = "Selected application", ReadOnly = true };
        shortcutSection.Controls.Add(shortcutName); shortcutSection.Controls.Add(shortcutPath);
        shortcutSection.Controls.Add(SecondaryButton("Choose application or shortcut", (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Applications and Windows shortcuts (*.exe;*.lnk)|*.exe;*.lnk", Title = "Choose an application" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            shortcutPath.Text = picker.FileName;
            if (shortcutName.TextLength == 0) shortcutName.Text = Path.GetFileNameWithoutExtension(picker.FileName);
        }));
        shortcutSection.Controls.Add(Button("Add app shortcut", (_, _) =>
        {
            var name = shortcutName.Text.Trim();
            if (name.Length == 0) { MessageBox.Show(this, "Enter a name for this application.", "TOKI"); return; }
            string validatedPath;
            try { validatedPath = ShortcutService.ValidatePath(shortcutPath.Text); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "TOKI"); return; }
            if (app.Data.AppShortcuts.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "That shortcut name already exists.", "TOKI"); return; }
            app.Data.AppShortcuts.Add(new AppShortcut { Name = name, Path = validatedPath }); app.Save(); RefreshShortcuts(); app.RefreshShortcuts(); shortcutName.Clear(); shortcutPath.Clear();
        }));
        shortcutSection.Controls.Add(SecondaryButton("Remove selected shortcut", (_, _) =>
        {
            if (shortcuts.SelectedIndex < 0) return;
            app.Data.AppShortcuts.RemoveAt(shortcuts.SelectedIndex); app.Save(); RefreshShortcuts(); app.RefreshShortcuts();
        }));
        privacySection.Controls.Add(new Label { Text = "Local data: " + Store.DirectoryPath, Width = 610, Height = 42, ForeColor = Muted });
        return Page("Settings", panel);
    }
}

internal sealed class AccountAvatar : Control
{
    private string? provider;
    public string? Provider { get => provider; set { provider = value; Invalidate(); } }
    public AccountAvatar() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(provider switch
        {
            "Gmail" => Color.FromArgb(66, 133, 244),
            "Outlook" => Color.FromArgb(0, 120, 212),
            _ => Color.FromArgb(75, 80, 95)
        });
        e.Graphics.FillEllipse(fill, 2, 2, Width - 4, Height - 4);
        using var font = new Font("Segoe UI", 15, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, provider == null ? "?" : provider[0].ToString(), font,
            ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class SurfaceCard : Panel
{
    public SurfaceCard()
    {
        BackColor = Color.FromArgb(27, 33, 49);
        DoubleBuffered = true;
    }
    private static GraphicsPath Outline(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 24 || Height < 24) return;
        using var outline = Outline(new Rectangle(0, 0, Width, Height), 14);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 24 || Height < 24) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = Outline(new Rectangle(0, 0, Width - 1, Height - 1), 14);
        using var border = new Pen(Color.FromArgb(58, 68, 93));
        e.Graphics.DrawPath(border, outline);
    }
}

internal static class TokiIcon
{
    public static Icon Create() => BrandAssets.CreateIcon();
}
