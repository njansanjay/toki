using System.Text.Json;
using System.Runtime.InteropServices;
using Toki;

internal static class Program
{
    private sealed class RobotCanvas : Form
    {
        protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x80000; return value; } }
    }
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    private static int checks;
    private static void Check(bool result, string name)
    {
        if (!result) throw new Exception("FAILED: " + name);
        checks++; Console.WriteLine("PASS: " + name);
    }
    [STAThread]
    private static void Main()
    {
        // No account access, app launches, network calls or writes to the user's TOKI data.
        var oldSettings = JsonSerializer.Deserialize<AppData>("{}");
        Check(oldSettings?.PrimaryView == "Shortcuts", "Existing installations default to the shortcut launcher");
        foreach (var name in PrimaryViews.Names)
        {
            var data = new AppData { PrimaryView = name };
            Check(JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(data))?.PrimaryView == name, "Saved primary view: " + name);
        }
        Check(PrimaryViews.PageIndex("Chat") == 0 && PrimaryViews.PageIndex("Settings") == 4 && PrimaryViews.PageIndex("Shortcuts") == 5, "Primary views map to their actual pages");
        Check(PrimaryViews.PageIndex("obsolete") == 5, "Unknown preference falls back to Shortcuts");
        foreach (var area in new[] { new Rectangle(0, 0, 1366, 728), new Rectangle(-1920, -200, 1920, 1080) })
        {
            foreach (var anchor in new[] { new Rectangle(area.Left, area.Top, 64, 64), new Rectangle(area.Right - 64, area.Bottom - 64, 64, 64) })
            {
                var size = new Size(350, 480);
                Check(area.Contains(new Rectangle(PrimaryViews.PopupLocation(anchor, size, area), size)), "Popup remains within work area at " + anchor.Location);
            }
        }
        using (var robot = new Bitmap(BrandAssets.Robot))
        {
            Check(robot.GetPixel(0, 0).A == 0 && robot.GetPixel(robot.Width - 1, robot.Height - 1).A == 0, "Robot PNG has transparent corners");
            Check(robot.GetPixel(robot.Width / 2, robot.Height / 2).B > 150, "Robot PNG contains the blue logo");
        }
        using (var icon = BrandAssets.CreateIcon()) Check(icon.Width > 0, "Embedded Windows icon loads");
        Check(Math.Abs(LayeredRobot.FloatOffset(Math.PI / 2) - 3) < 0.001, "Floating motion stays within three pixels");
        Check(LayeredRobot.FloatOffset(0.1) > 0 && LayeredRobot.FloatOffset(0.1) < 1, "Floating motion retains fractional-pixel positions");
        using (var menuButton = new LauncherActionButton(true))
        using (var closeButton = new LauncherActionButton(false))
            Check(menuButton.Size == closeButton.Size, "Menu and close controls have identical hit areas");
        using (var canvas = new RobotCanvas { AutoScaleMode = AutoScaleMode.None, FormBorderStyle = FormBorderStyle.None, MinimumSize = new Size(64, 64), MaximumSize = new Size(64, 64), Size = new Size(64, 64) })
        {
            _ = canvas.Handle;
            Check(canvas.Width == canvas.Height, "Native floating canvas remains square");
            var location = canvas.Location;
            var resourcesBefore = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
            using (var renderer = new LayeredRobot())
            {
                for (var i = -24; i <= 24; i++) renderer.Render(canvas, i / 8f);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 600; i++) renderer.Render(canvas, LayeredRobot.FloatOffset(i * 0.03));
                Console.WriteLine($"Cached animation: {clock.Elapsed.TotalMilliseconds / 600:0.000} ms/frame (600 frames)");
                Check(canvas.Location == location, "Animation does not reposition the native window");
            }
            Check(GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0) <= resourcesBefore + 2, "Animation releases its cached GDI frames");
        }
        foreach (var canvas in new[] { new Size(112, 40), new Size(40, 112), new Size(64, 64) })
        {
            var fit = BrandAssets.FitRobot(canvas);
            Check(Math.Abs((double)fit.Width / fit.Height - (double)BrandAssets.Robot.Width / BrandAssets.Robot.Height) < 0.02,
                "Robot keeps its proportions on canvas " + canvas);
            Check(new Rectangle(Point.Empty, canvas).Contains(fit), "Robot fits inside canvas " + canvas);
        }
        var testDirectory = Path.Combine(Path.GetTempPath(), "TokiLauncherChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            var exe = Path.Combine(testDirectory, "example.exe"); File.WriteAllText(exe, "validation only; never executed");
            Check(ShortcutService.ValidatePath(exe) == exe, "Existing executable path accepted");
            var script = Path.Combine(testDirectory, "script.cmd"); File.WriteAllText(script, "never executed");
            try { ShortcutService.ValidatePath(script); throw new Exception("Script accepted"); } catch (ArgumentException) { checks++; }
            try { ShortcutService.ValidatePath(Path.Combine(testDirectory, "missing.exe")); throw new Exception("Missing file accepted"); } catch (FileNotFoundException) { checks++; }
            using var applicationIcon = ShortcutService.LoadIconAsync(Environment.ProcessPath!).GetAwaiter().GetResult();
            Check(applicationIcon.Width > 0, "Windows executable icon resolves asynchronously");
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(Path.Combine(testDirectory, "Toki.lnk"));
            try
            {
                link.TargetPath = Environment.ProcessPath!; link.Save();
                var linkPath = ShortcutService.ValidatePath(Path.Combine(testDirectory, "Toki.lnk"));
                using var linkIcon = ShortcutService.LoadIconAsync(linkPath).GetAwaiter().GetResult();
                Check(linkIcon.Width > 0, "Windows .lnk icon resolves without launching its target");
            }
            finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
            using var fallback = ShortcutService.LoadIconAsync(Path.Combine(testDirectory, "missing.exe")).GetAwaiter().GetResult();
            Check(fallback.Width > 0, "Moved app has a usable fallback icon");
        }
        finally
        {
            // Delete only files created in this uniquely named test directory.
            foreach (var file in Directory.EnumerateFiles(testDirectory)) File.Delete(file);
            Directory.Delete(testDirectory);
        }
        Console.WriteLine($"{checks} launcher checks passed.");
    }
}
