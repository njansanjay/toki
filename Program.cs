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
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TokiApp());
    }
}

internal sealed class TokiApp : ApplicationContext
{
    internal readonly AppData Data = Store.Load();
    private readonly NotifyIcon tray;
    private readonly OrbForm orb;
    private ChatForm? chat;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    private bool paused;

    public TokiApp()
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
        menu.Items.Add("New chat", null, (_, _) => { Data.Messages.Clear(); Save(); Open(); chat?.RefreshChat(); });
        menu.Items.Add("Add reminder", null, (_, _) => { Open(); chat?.SelectTab(1); });
        menu.Items.Add("Email assistant", null, (_, _) => { Open(); chat?.SelectTab(2); });
        menu.Items.Add("Usage monitor", null, (_, _) => { Open(); chat?.SelectTab(3); });
        menu.Items.Add("Settings", null, (_, _) => { Open(); chat?.SelectTab(4); });
        menu.Items.Add("Pause notifications", null, (_, _) => { paused = !paused; });
        menu.Items.Add("Restart TOKI", null, (_, _) => { Application.Restart(); Exit(); });
        menu.Items.Add("Exit", null, (_, _) => Exit());
        tray = new NotifyIcon { Icon = SystemIcons.Information, Text = "TOKI", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Open();
        orb.Show();
        timer.Tick += (_, _) => CheckReminders();
        timer.Start();
        CheckReminders();
    }

    internal void Save() => Store.Save(Data);
    internal bool SetOpenHotkey(string value) => orb.SetOpenHotkey(value);
    internal void Open()
    {
        if (chat is null || chat.IsDisposed) chat = new ChatForm(this);
        chat.Show();
        chat.Activate();
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
        timer.Stop(); Save(); tray.Visible = false; tray.Dispose(); orb.Close(); chat?.Dispose(); ExitThread();
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
    public OrbForm(TokiApp app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = app.Data.TopMost;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        var size = Math.Clamp(app.Data.OrbSize, 40, 120);
        Size = new Size(size, size);
        Location = new Point(app.Data.OrbX, app.Data.OrbY);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => app.Open());
        menu.Items.Add("Settings", null, (_, _) => { app.Open(); });
        menu.Items.Add("Exit", null, (_, _) => app.Exit());
        ContextMenuStrip = menu;
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { dragOrigin = Cursor.Position; windowOrigin = Location; moved = false; } };
        MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) { var delta = new Size(Cursor.Position.X - dragOrigin.X, Cursor.Position.Y - dragOrigin.Y); if (Math.Abs(delta.Width) + Math.Abs(delta.Height) > 4) moved = true; if (moved) Location = windowOrigin + delta; } };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) { if (moved) { app.Data.OrbX = Left; app.Data.OrbY = Top; app.Save(); } else app.Open(); } };
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
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId) app.Open();
        base.WndProc(ref m);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(112, 82, 255), Color.FromArgb(0, 202, 215), 45);
        e.Graphics.FillEllipse(brush, 2, 2, Width - 4, Height - 4);
        using var font = new Font("Segoe UI", Width * .22f, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, "T", font, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class ChatForm : Form
{
    [DllImport("user32.dll", SetLastError = true)] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private static readonly Color Canvas = Color.FromArgb(17, 20, 32);
    private static readonly Color Surface = Color.FromArgb(27, 33, 49);
    private static readonly Color InputSurface = Color.FromArgb(38, 45, 64);
    private static readonly Color Accent = Color.FromArgb(115, 96, 246);
    private readonly TokiApp app;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly RichTextBox history = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(24, 27, 38), ForeColor = Color.White, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10) };
    private readonly TextBox input = new() { Dock = DockStyle.Fill, PlaceholderText = "Ask TOKI anything..." };
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
    private readonly AccountAvatar accountAvatar = new() { Size = new Size(38, 38) };
    private readonly Label accountStatus = new() { Width = 335, Height = 42, ForeColor = Color.White, Text = "No email account connected" };
    private readonly Label connectionStatus = new() { Width = 390, Height = 42, ForeColor = Color.LightSkyBlue, Text = "Choose an account to connect." };
    private Button? gmailButton;
    private Button? outlookButton;
    private Button? cancelConnectionButton;
    private CancellationTokenSource? connectionCancellation;

    public ChatForm(TokiApp app)
    {
        this.app = app;
        email = new EmailService(app);
        Text = "TOKI  •  Desktop assistant"; Size = new Size(650, 780); MinimumSize = new Size(510, 620);
        StartPosition = FormStartPosition.CenterScreen; TopMost = app.Data.TopMost;
        BackColor = Canvas; ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(108, 42);
        tabs.DrawItem += (_, e) =>
        {
            var selected = e.Index == tabs.SelectedIndex;
            using var fill = new SolidBrush(selected ? Surface : Canvas);
            e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Font, e.Bounds,
                selected ? Color.White : Color.FromArgb(157, 168, 190), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected)
            {
                using var accent = new SolidBrush(Accent);
                e.Graphics.FillRectangle(accent, e.Bounds.Left + 15, e.Bounds.Bottom - 3, e.Bounds.Width - 30, 3);
            }
        };
        Controls.Add(tabs);
        tabs.TabPages.Add(MakeChat()); tabs.TabPages.Add(MakeReminders()); tabs.TabPages.Add(MakePremiumEmail()); tabs.TabPages.Add(MakeUsage()); tabs.TabPages.Add(MakeSettings());
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 80, ColumnCount = 2, BackColor = Canvas, Padding = new Padding(22, 12, 22, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "✦  TOKI\n     Your desktop companion", AutoSize = true, Font = new Font("Segoe UI Semibold", 13), ForeColor = Color.White }, 0, 0);
        hotkeyHint.Text = app.Data.OpenHotkey == "Off" ? "Ready when you are" : app.Data.OpenHotkey + "  to open";
        header.Controls.Add(hotkeyHint, 1, 0);
        Controls.Add(header);
        ApplyTheme(this);
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
                case ComboBox box: box.BackColor = InputSurface; box.ForeColor = Color.White; box.FlatStyle = FlatStyle.Flat; break;
                case ListBox box: box.BackColor = InputSurface; box.ForeColor = Color.White; box.BorderStyle = BorderStyle.None; break;
                case NumericUpDown number: number.BackColor = InputSurface; number.ForeColor = Color.White; break;
                case DateTimePicker date: date.CalendarMonthBackground = InputSurface; date.CalendarForeColor = Color.White; break;
                case TabPage page: page.BackColor = Canvas; page.ForeColor = Color.White; break;
            }
            ApplyTheme(control);
        }
    }
    public void SelectTab(int index) => tabs.SelectedIndex = index;
    private TabPage Page(string title, Control content) { var p = new TabPage(title) { BackColor = Canvas, ForeColor = ForeColor, Padding = new Padding(14) }; p.Controls.Add(content); return p; }
    private static Button Button(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(67, 59, 139), ForeColor = Color.White, Padding = new Padding(8, 5, 8, 5), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(91, 79, 190);
        b.Click += click; return b;
    }
    private TabPage MakeChat()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        panel.Controls.Add(history, 0, 0);
        var sendRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        sendRow.ColumnCount = 3;
        sendRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sendRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55)); sendRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        sendRow.Controls.Add(input, 0, 0); sendRow.Controls.Add(Button("🎙", (_, _) => StartVoiceTyping()), 1, 0);
        sendRow.Controls.Add(Button("Send", async (_, _) => await Send()), 2, 0);
        input.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await Send(); } };
        panel.Controls.Add(sendRow, 0, 1);
        var quick = new FlowLayoutPanel { Dock = DockStyle.Fill };
        quick.Controls.Add(Button("Reminder", (_, _) => SelectTab(1))); quick.Controls.Add(Button("Email", (_, _) => SelectTab(2)));
        quick.Controls.Add(Button("Apps", (sender, _) => ShowAppsMenu((Control)sender!)));
        quick.Controls.Add(Button("Usage", (_, _) => SelectTab(3))); quick.Controls.Add(Button("Settings", (_, _) => SelectTab(4)));
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
        history.Clear();
        foreach (var m in app.Data.Messages) history.AppendText($"{(m.Role == "user" ? "You" : "TOKI")}  {m.At:g}\n{m.Content}\n\n");
        history.SelectionStart = history.TextLength; history.ScrollToCaret();
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
                        app.Data.Messages[^1].Content = answer.ToString(); RefreshChat();
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
                try { Process.Start(new ProcessStartInfo(custom.Path) { UseShellExecute = true }); return $"Opened {custom.Name}."; }
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
    private TabPage MakeReminders()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var editor = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        var title = new TextBox { Width = 290, PlaceholderText = "What should I remind you about?" };
        var due = new DateTimePicker { Width = 230, Format = DateTimePickerFormat.Custom, CustomFormat = "ddd, dd MMM yyyy  hh:mm tt", Value = DateTime.Now.AddHours(1) };
        var repeat = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
        repeat.Items.AddRange(new object[] { "None", "Daily", "Weekly" }); repeat.SelectedIndex = 0;
        editor.Controls.Add(title); editor.Controls.Add(due); editor.Controls.Add(repeat);
        editor.Controls.Add(Button("Add reminder", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text) || due.Value <= DateTime.Now) { MessageBox.Show("Enter a description and a future date."); return; }
            app.Data.Reminders.Add(new Reminder { Text = title.Text.Trim(), Due = due.Value, Recurrence = repeat.Text }); app.Save(); title.Clear(); RefreshReminders();
        }));
        root.Controls.Add(editor, 0, 0); root.Controls.Add(reminders, 0, 1);
        return Page("Reminders", root);
    }
    private void RefreshReminders()
    {
        reminders.Controls.Clear();
        foreach (var r in app.Data.Reminders.OrderBy(x => x.Completed).ThenBy(x => x.Due).ToList())
        {
            var row = new FlowLayoutPanel { Width = 410, Height = 70, BackColor = Color.FromArgb(38, 42, 57) };
            row.Controls.Add(new Label { Text = $"{r.Text}\n{r.Due:g} • {r.Recurrence}" + (r.Completed ? " • Done" : ""), Width = 215, Height = 62, ForeColor = Color.White });
            if (!r.Completed)
            {
                row.Controls.Add(Button("Snooze", (_, _) => { r.Due = DateTime.Now.AddMinutes(10); app.Save(); RefreshReminders(); }));
                row.Controls.Add(Button("Done", (_, _) => { r.Completed = true; app.Save(); RefreshReminders(); }));
            }
            row.Controls.Add(Button("×", (_, _) => { app.Data.Reminders.Remove(r); app.Save(); RefreshReminders(); }));
            reminders.Controls.Add(row);
        }
    }
    private TabPage MakeEmail()
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
    private TabPage MakePremiumEmail()
    {
        var gallery = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, BackColor = Canvas };
        var accountCard = new SurfaceCard { Height = 380, Padding = new Padding(20), Margin = new Padding(8) };
        var composeCard = new SurfaceCard { Height = 670, Padding = new Padding(20), Margin = new Padding(8) };
        var accountContent = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Surface };
        accountContent.Controls.Add(new Label { Text = "CONNECTED ACCOUNTS", Height = 28, Width = 440, Font = new Font("Segoe UI Semibold", 10), ForeColor = Color.FromArgb(161, 175, 202) });
        var identity = new FlowLayoutPanel { Height = 56, Width = 440, BackColor = Surface, WrapContents = false };
        identity.Controls.Add(accountAvatar); identity.Controls.Add(accountStatus); accountContent.Controls.Add(identity);
        accountContent.Controls.Add(new Label { Text = "Active sender", Height = 26, Width = 440, ForeColor = Color.FromArgb(161, 175, 202) });
        accountBox = new ComboBox { Width = 440, Height = 38, DropDownStyle = ComboBoxStyle.DropDownList };
        accountBox.SelectedIndexChanged += (_, _) =>
        {
            var selected = SelectedAccount(); app.Data.ActiveEmailAccountId = selected?.Id; app.Save(); UpdateAccountDisplay();
        };
        RefreshAccounts(); accountContent.Controls.Add(accountBox);
        var connectionButtons = new FlowLayoutPanel { Width = 440, Height = 54, BackColor = Surface, WrapContents = false };
        gmailButton = Button("+ Gmail account", async (_, _) => await ConnectEmail("Gmail"));
        outlookButton = Button("+ Outlook account", async (_, _) => await ConnectEmail("Outlook"));
        connectionButtons.Controls.Add(gmailButton); connectionButtons.Controls.Add(outlookButton); accountContent.Controls.Add(connectionButtons);
        connectionStatus.Text = SelectedAccount() == null ? "Choose an account to connect." : "Your account is ready to use.";
        accountContent.Controls.Add(connectionStatus);
        var accountActions = new FlowLayoutPanel { Width = 440, Height = 48, BackColor = Surface, WrapContents = false };
        cancelConnectionButton = Button("Cancel sign-in", (_, _) => connectionCancellation?.Cancel()); cancelConnectionButton.Enabled = false;
        accountActions.Controls.Add(cancelConnectionButton);
        accountActions.Controls.Add(Button("Disconnect", (_, _) =>
        {
            var account = SelectedAccount(); if (account == null) return;
            if (MessageBox.Show(this, $"Remove {account.Address} from TOKI?", "Disconnect", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            app.Data.Accounts.Remove(account); app.Data.ActiveEmailAccountId = null; app.Save(); RefreshAccounts();
            connectionStatus.Text = "Account disconnected.";
        }));
        accountContent.Controls.Add(accountActions); accountCard.Controls.Add(accountContent);

        var compose = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Surface };
        compose.Controls.Add(new Label { Text = "COMPOSE EMAIL", Height = 32, Width = 440, Font = new Font("Segoe UI Semibold", 13), ForeColor = Color.White });
        compose.Controls.Add(new Label { Text = "Write, review, and send from your connected account.", Height = 34, Width = 440, ForeColor = Color.FromArgb(161, 175, 202) });
        var recipient = new TextBox { Width = 440, Height = 36, PlaceholderText = "To · recipient@example.com" };
        var subject = new TextBox { Width = 440, Height = 36, PlaceholderText = "Subject" };
        var body = new TextBox { Width = 440, Height = 270, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Write your message..." };
        compose.Controls.Add(recipient); compose.Controls.Add(subject); compose.Controls.Add(body);
        var readActions = new FlowLayoutPanel { Width = 440, Height = 53, BackColor = Surface, WrapContents = false };
        readActions.Controls.Add(Button("Latest inbox", async (_, _) => await ShowInbox(false)));
        readActions.Controls.Add(Button("Unread inbox", async (_, _) => await ShowInbox(true))); compose.Controls.Add(readActions);
        var sendActions = new FlowLayoutPanel { Width = 440, Height = 53, BackColor = Surface, WrapContents = false };
        sendActions.Controls.Add(Button("Review & send", async (_, _) =>
        {
            var account = SelectedAccount(); if (account == null) { MessageBox.Show(this, "Connect or select an account first.", "TOKI"); return; }
            if (!Regex.IsMatch(recipient.Text.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) { MessageBox.Show(this, "Enter a valid recipient address.", "TOKI"); return; }
            if (MessageBox.Show(this, $"From: {account.Address}\nTo: {recipient.Text}\nSubject: {subject.Text}\n\n{body.Text}", "Review and approve email", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { await email.SendAsync(account, recipient.Text.Trim(), subject.Text, body.Text); app.Notify("TOKI email", "Email sent."); MessageBox.Show(this, "Email sent.", "TOKI"); }
            catch (Exception ex) { app.Notify("TOKI email", "Email sending failed."); MessageBox.Show(this, ex.Message, "TOKI"); }
        }));
        sendActions.Controls.Add(Button("Open in mail app", (_, _) =>
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
            var sideBySide = width >= 960;
            var cardWidth = sideBySide ? Math.Min(520, (width - 56) / 2) : Math.Min(620, width - 36);
            var groupWidth = sideBySide ? 2 * (cardWidth + 16) : cardWidth + 16;
            gallery.Padding = new Padding(Math.Max(8, (width - groupWidth) / 2), 12, 8, 12);
            accountCard.Width = cardWidth; composeCard.Width = cardWidth;
            var innerWidth = cardWidth - 48;
            identity.Width = innerWidth; accountStatus.Width = innerWidth - 52; accountBox.Width = innerWidth;
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
    private TabPage MakeUsage()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false };
        panel.Controls.Add(new Label { Text = "ChatGPT usage • manual estimate", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        panel.Controls.Add(new Label { Text = "Live subscription usage is unavailable. Enter your own plan, limit and used count. API usage is separate from ChatGPT usage.", Width = 390, Height = 75 });
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
        panel.Controls.Add(usageStatus);
        var link = new LinkLabel { Text = "Open ChatGPT settings", AutoSize = true, LinkColor = Color.Cyan };
        link.Click += (_, _) => Process.Start(new ProcessStartInfo("https://chatgpt.com/settings/usage") { UseShellExecute = true });
        panel.Controls.Add(link);
        return Page("Usage", panel);
    }
    private void RefreshUsage()
    {
        var u = app.Data.Usage;
        usageStatus.Text = u.Limit <= 0 ? "No manual usage estimate set." : $"Manual estimate: {u.Used}/{u.Limit} ({100.0 * u.Used / u.Limit:0}%)";
    }
    private TabPage MakeSettings()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false };
        var googleClient = new TextBox { Width = 280, PlaceholderText = "Google OAuth client ID", Text = app.Data.GoogleClientId };
        string? importedGoogleSecret = null;
        string? importedGoogleClientId = null;
        var microsoftClient = new TextBox { Width = 280, PlaceholderText = "Microsoft OAuth client ID", Text = app.Data.MicrosoftClientId };
        panel.Controls.Add(new Label { Text = "AI provider", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        providerBox.Items.AddRange(new object[] { "OpenAI", "OpenAI-compatible" }); providerBox.SelectedItem = app.Data.Provider;
        if (providerBox.SelectedIndex < 0) providerBox.SelectedIndex = 0;
        modelBox.Text = app.Data.Model; urlBox.Text = app.Data.BaseUrl;
        panel.Controls.Add(providerBox);
        panel.Controls.Add(new Label { Text = "Model", AutoSize = true }); panel.Controls.Add(modelBox);
        panel.Controls.Add(new Label { Text = "API base URL (HTTPS)", AutoSize = true }); panel.Controls.Add(urlBox);
        panel.Controls.Add(new Label { Text = "API key (leave blank to keep existing)", AutoSize = true }); panel.Controls.Add(keyBox);
        panel.Controls.Add(new Label { Text = "ChatGPT subscription access does not automatically include API credits. Eligible plans may offer separate API credit benefits; check your API billing.", Width = 390, Height = 70 });
        panel.Controls.Add(new Label { Text = "Email OAuth client IDs", AutoSize = true }); panel.Controls.Add(googleClient); panel.Controls.Add(microsoftClient);
        panel.Controls.Add(Button("Import Google desktop OAuth JSON", (_, _) =>
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
        var size = new NumericUpDown { Minimum = 40, Maximum = 120, Value = Math.Clamp(app.Data.OrbSize, 40, 120), Width = 100 };
        var hotkey = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
        hotkey.Items.AddRange(new object[] { "Ctrl+Alt+T", "Ctrl+Shift+Space", "Off" });
        hotkey.SelectedItem = app.Data.OpenHotkey;
        if (hotkey.SelectedIndex < 0) hotkey.SelectedIndex = 0;
        panel.Controls.Add(startup); panel.Controls.Add(top); panel.Controls.Add(notifications);
        panel.Controls.Add(speak); panel.Controls.Add(new Label { Text = "Speech speed", AutoSize = true }); panel.Controls.Add(speechRate);
        panel.Controls.Add(new Label { Text = "Floating icon size", AutoSize = true }); panel.Controls.Add(size);
        panel.Controls.Add(new Label { Text = "Keyboard shortcut to open TOKI", AutoSize = true }); panel.Controls.Add(hotkey);
        panel.Controls.Add(Button("Save settings", (_, _) =>
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
            app.Save(); MessageBox.Show("Saved. Restart TOKI to apply icon size and always-on-top changes.");
        }));
        panel.Controls.Add(Button("Delete API key", (_, _) => { app.Data.EncryptedKey = ""; app.Save(); MessageBox.Show("API key deleted."); }));
        panel.Controls.Add(Button("Clear chat history", (_, _) => { if (MessageBox.Show("Delete local chat history?", "TOKI", MessageBoxButtons.YesNo) == DialogResult.Yes) { app.Data.Messages.Clear(); app.Save(); RefreshChat(); } }));
        panel.Controls.Add(new Label { Text = "App shortcuts", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        panel.Controls.Add(new Label { Text = "Choose an .exe or Windows .lnk shortcut, give it a name, then say 'open <name>' in Chat.", Width = 390, Height = 48 });
        var shortcuts = new ListBox { Width = 390, Height = 108 };
        void RefreshShortcuts()
        {
            shortcuts.Items.Clear();
            foreach (var item in app.Data.AppShortcuts) shortcuts.Items.Add($"{item.Name}  ·  {Path.GetFileName(item.Path)}");
        }
        RefreshShortcuts(); panel.Controls.Add(shortcuts);
        var shortcutName = new TextBox { Width = 390, PlaceholderText = "Name, e.g. Photoshop" };
        var shortcutPath = new TextBox { Width = 390, PlaceholderText = "Selected application", ReadOnly = true };
        panel.Controls.Add(shortcutName); panel.Controls.Add(shortcutPath);
        panel.Controls.Add(Button("Choose application or shortcut", (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Applications and Windows shortcuts (*.exe;*.lnk)|*.exe;*.lnk", Title = "Choose an application" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            shortcutPath.Text = picker.FileName;
            if (shortcutName.TextLength == 0) shortcutName.Text = Path.GetFileNameWithoutExtension(picker.FileName);
        }));
        panel.Controls.Add(Button("Add app shortcut", (_, _) =>
        {
            var name = shortcutName.Text.Trim();
            if (name.Length == 0 || !File.Exists(shortcutPath.Text)) { MessageBox.Show(this, "Enter a name and choose an existing application or shortcut.", "TOKI"); return; }
            if (app.Data.AppShortcuts.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "That shortcut name already exists.", "TOKI"); return; }
            app.Data.AppShortcuts.Add(new AppShortcut { Name = name, Path = shortcutPath.Text }); app.Save(); RefreshShortcuts(); shortcutName.Clear(); shortcutPath.Clear();
        }));
        panel.Controls.Add(Button("Remove selected shortcut", (_, _) =>
        {
            if (shortcuts.SelectedIndex < 0) return;
            app.Data.AppShortcuts.RemoveAt(shortcuts.SelectedIndex); app.Save(); RefreshShortcuts();
        }));
        panel.Controls.Add(new Label { Text = "Local data: " + Store.DirectoryPath, Width = 390, Height = 45 });
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
