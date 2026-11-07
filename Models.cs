using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Toki;

public sealed class AppData
{
    public int OrbX { get; set; } = 50;
    public int OrbY { get; set; } = 100;
    public int OrbSize { get; set; } = 64;
    public bool TopMost { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool Notifications { get; set; } = true;
    public bool SpeakResponses { get; set; }
    public int SpeechRate { get; set; }
    public string Provider { get; set; } = "OpenAI";
    public string Model { get; set; } = "gpt-4.1-mini";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string EncryptedKey { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    public List<Reminder> Reminders { get; set; } = new();
    public UsageData Usage { get; set; } = new();
    public string GoogleClientId { get; set; } = "";
    public string EncryptedGoogleClientSecret { get; set; } = "";
    public string MicrosoftClientId { get; set; } = "";
    public List<EmailAccount> Accounts { get; set; } = new();
    public Guid? ActiveEmailAccountId { get; set; }
    public string OpenHotkey { get; set; } = "Ctrl+Alt+T";
    public List<AppShortcut> AppShortcuts { get; set; } = new();
}

public sealed class AppShortcut
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class EmailAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "";
    public string Address { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
}

public sealed class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public DateTime Due { get; set; }
    public string Recurrence { get; set; } = "None";
    public bool Completed { get; set; }
}

public sealed class UsageData
{
    public string Plan { get; set; } = "";
    public int Limit { get; set; }
    public int Used { get; set; }
    public DateTime Reset { get; set; } = DateTime.Today.AddMonths(1);
    public int Warning { get; set; } = 80;
    public int Critical { get; set; } = 95;
    public bool WarningSent { get; set; }
    public bool CriticalSent { get; set; }
}

public static class Store
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TOKI");
    private static string FilePath => Path.Combine(DirectoryPath, "data.json");
    public static AppData Load()
    {
        try { return JsonSerializer.Deserialize<AppData>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }
    public static void Save(AppData data)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
    public static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
