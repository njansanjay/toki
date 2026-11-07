using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Toki;

internal sealed class EmailService
{
    private readonly TokiApp app;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(40) };
    public EmailService(TokiApp app) => this.app = app;
    private static string Enc(string text) => Uri.EscapeDataString(text);
    private static string Url64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<EmailAccount> ConnectAsync(string provider, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var google = provider == "Gmail";
        var clientId = google ? app.Data.GoogleClientId.Trim() : app.Data.MicrosoftClientId.Trim();
        if (clientId.Length == 0) throw new InvalidOperationException(google
            ? "Import a Google Desktop app OAuth JSON file in Settings, save settings, then connect Gmail."
            : "Set the Outlook OAuth client ID in Settings first.");
        var clientSecret = google ? Store.Unprotect(app.Data.EncryptedGoogleClientSecret) : "";
        if (google && clientSecret.Length == 0)
            throw new InvalidOperationException("Import the complete Google Desktop OAuth JSON file in Settings, then save settings. This Google client requires its client secret for sign-in.");
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var redirect = google ? $"http://127.0.0.1:{port}/callback/" : $"http://localhost:{port}/callback/";
        var verifier = Url64(RandomNumberGenerator.GetBytes(32));
        var challenge = Url64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Url64(RandomNumberGenerator.GetBytes(18));
        var authBase = google ? "https://accounts.google.com/o/oauth2/v2/auth" : "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
        var scopes = google ? "openid email https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.send" : "openid email offline_access User.Read Mail.Read Mail.Send";
        var url = $"{authBase}?client_id={Enc(clientId)}&redirect_uri={Enc(redirect)}&response_type=code&scope={Enc(scopes)}&state={Enc(state)}&code_challenge={Enc(challenge)}&code_challenge_method=S256" + (google ? "&access_type=offline&prompt=select_account%20consent" : "&prompt=select_account");
        using var listener = new HttpListener(); listener.Prefixes.Add(redirect); listener.Start();
        progress?.Report($"Browser opened. Finish {provider} sign-in and wait for the TOKI confirmation page.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        var query = context.Request.QueryString;
        var callbackError = query["error"];
        var html = string.IsNullOrEmpty(callbackError)
            ? "<html><body style='font-family:sans-serif'>TOKI received your sign-in. You can return to the app.</body></html>"
            : "<html><body style='font-family:sans-serif'>TOKI sign-in was not completed. Return to the app for details.</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html"; context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
        if (query["state"] != state) throw new InvalidOperationException("OAuth sign-in state did not match. Please try again.");
        if (!string.IsNullOrEmpty(callbackError)) throw new InvalidOperationException($"{provider} sign-in returned: {callbackError}");
        if (string.IsNullOrEmpty(query["code"])) throw new InvalidOperationException($"{provider} did not return a sign-in code.");
        progress?.Report("Sign-in received. Finishing account connection...");
        var endpoint = google ? "https://oauth2.googleapis.com/token" : "https://login.microsoftonline.com/common/oauth2/v2.0/token";
        var tokenFields = new Dictionary<string, string> { ["client_id"] = clientId, ["code"] = query["code"]!, ["code_verifier"] = verifier, ["grant_type"] = "authorization_code", ["redirect_uri"] = redirect };
        if (google) tokenFields["client_secret"] = clientSecret;
        using var tokenResponse = await http.PostAsync(endpoint, new FormUrlEncodedContent(tokenFields), cancellationToken);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
        if (!tokenResponse.IsSuccessStatusCode) throw new InvalidOperationException("OAuth token exchange failed: " + tokenBody[..Math.Min(200, tokenBody.Length)]);
        using var doc = JsonDocument.Parse(tokenBody);
        var root = doc.RootElement;
        var account = new EmailAccount { Provider = provider, AccessToken = Store.Protect(root.GetProperty("access_token").GetString()!), RefreshToken = root.TryGetProperty("refresh_token", out var refresh) ? Store.Protect(refresh.GetString()!) : "", ExpiresAt = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60) };
        progress?.Report("Reading the connected account address...");
        var profileUrl = google ? "https://openidconnect.googleapis.com/v1/userinfo" : "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName";
        using var profile = await GetAsync(profileUrl, account, cancellationToken);
        account.Address = google ? profile.RootElement.GetProperty("email").GetString()! : (profile.RootElement.GetProperty("mail").GetString() ?? profile.RootElement.GetProperty("userPrincipalName").GetString()!);
        if (string.IsNullOrWhiteSpace(account.Address)) throw new InvalidOperationException("The provider did not return an account address.");
        var previous = app.Data.Accounts.FirstOrDefault(a => a.Provider == provider && a.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase));
        if (previous != null)
        {
            if (account.RefreshToken.Length == 0) account.RefreshToken = previous.RefreshToken;
            app.Data.Accounts.Remove(previous);
        }
        app.Data.Accounts.Add(account); app.Data.ActiveEmailAccountId = account.Id; app.Save();
        return account;
    }

    private async Task<string> TokenAsync(EmailAccount account)
    {
        if (account.ExpiresAt > DateTime.UtcNow.AddMinutes(1)) return Store.Unprotect(account.AccessToken);
        var clientId = account.Provider == "Gmail" ? app.Data.GoogleClientId : app.Data.MicrosoftClientId;
        var endpoint = account.Provider == "Gmail" ? "https://oauth2.googleapis.com/token" : "https://login.microsoftonline.com/common/oauth2/v2.0/token";
        var refresh = Store.Unprotect(account.RefreshToken);
        if (refresh.Length == 0) throw new InvalidOperationException("Please reconnect this email account.");
        var refreshFields = new Dictionary<string, string> { ["client_id"] = clientId, ["refresh_token"] = refresh, ["grant_type"] = "refresh_token" };
        if (account.Provider == "Gmail")
        {
            var clientSecret = Store.Unprotect(app.Data.EncryptedGoogleClientSecret);
            if (clientSecret.Length == 0) throw new InvalidOperationException("Import the complete Google Desktop OAuth JSON file in Settings, then save settings.");
            refreshFields["client_secret"] = clientSecret;
        }
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(refreshFields));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Email session expired. Reconnect this account.");
        using var doc = JsonDocument.Parse(body);
        account.AccessToken = Store.Protect(doc.RootElement.GetProperty("access_token").GetString()!);
        if (doc.RootElement.TryGetProperty("refresh_token", out var newRefresh)) account.RefreshToken = Store.Protect(newRefresh.GetString()!);
        account.ExpiresAt = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        app.Save(); return Store.Unprotect(account.AccessToken);
    }

    private async Task<JsonDocument> GetAsync(string url, EmailAccount account, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(account));
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Email service returned {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        return JsonDocument.Parse(body);
    }

    public async Task<List<string>> InboxAsync(EmailAccount account, bool unreadOnly)
    {
        var list = new List<string>();
        if (account.Provider == "Gmail")
        {
            var url = "https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults=10" + (unreadOnly ? "&q=is%3Aunread" : "");
            using var doc = await GetAsync(url, account);
            if (!doc.RootElement.TryGetProperty("messages", out var messages)) return list;
            foreach (var item in messages.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                using var detail = await GetAsync($"https://gmail.googleapis.com/gmail/v1/users/me/messages/{id}?format=metadata&metadataHeaders=From&metadataHeaders=Subject", account);
                var headers = detail.RootElement.GetProperty("payload").GetProperty("headers").EnumerateArray();
                string from = "", subject = "";
                foreach (var h in headers) { var name = h.GetProperty("name").GetString(); if (name == "From") from = h.GetProperty("value").GetString() ?? ""; if (name == "Subject") subject = h.GetProperty("value").GetString() ?? ""; }
                list.Add($"From: {from}\nSubject: {subject}\n{detail.RootElement.GetProperty("snippet").GetString()}");
            }
        }
        else
        {
            var filter = unreadOnly ? "&$filter=isRead%20eq%20false" : "";
            using var doc = await GetAsync("https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages?$top=10&$select=subject,from,bodyPreview,receivedDateTime" + filter, account);
            foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var from = item.GetProperty("from").GetProperty("emailAddress").GetProperty("address").GetString();
                list.Add($"From: {from}\nSubject: {item.GetProperty("subject").GetString()}\n{item.GetProperty("bodyPreview").GetString()}");
            }
        }
        return list;
    }

    public async Task SendAsync(EmailAccount account, string to, string subject, string body)
    {
        var url = account.Provider == "Gmail" ? "https://gmail.googleapis.com/gmail/v1/users/me/messages/send" : "https://graph.microsoft.com/v1.0/me/sendMail";
        string payload;
        if (account.Provider == "Gmail")
        {
            if (to.Contains('\r') || to.Contains('\n') || subject.Contains('\r') || subject.Contains('\n')) throw new InvalidOperationException("Invalid email header.");
            var mime = $"To: {to}\r\nSubject: {subject}\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(body))}";
            payload = JsonSerializer.Serialize(new { raw = Url64(Encoding.UTF8.GetBytes(mime)) });
        }
        else payload = JsonSerializer.Serialize(new { message = new { subject, body = new { contentType = "Text", content = body }, toRecipients = new[] { new { emailAddress = new { address = to } } } }, saveToSentItems = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(account));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Send failed ({(int)response.StatusCode}): {error[..Math.Min(200, error.Length)]}");
        }
    }
}
