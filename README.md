# TOKI desktop assistant

TOKI is a native Windows 10/11 desktop assistant built with .NET 6 WinForms. It provides a draggable floating icon, tray menu, local chat history, reminders, manual ChatGPT usage estimates, desktop shortcuts, clipboard actions, screenshots, and an optional OpenAI-compatible chat connection.

## Run

Open `dist/Toki.exe` on Windows with the .NET 6 Desktop Runtime installed. Keep `dist/oauth.local.json` beside the executable for the locally configured Google OAuth credentials. Its client secret is protected with Windows DPAPI for this Windows user. To build from source, run:

```powershell
dotnet publish Toki.csproj -c Release --self-contained false -o dist
```

The published folder must be kept together. TOKI stores settings, encrypted API keys, reminders, and conversations in `%LOCALAPPDATA%\TOKI\data.json`. Keys are protected with Windows DPAPI for the current Windows user. TOKI does not make an AI request until a key is configured and a chat message is sent. Depending on your provider and account, such a request may be billed; TOKI itself does not buy credits or start a subscription.

Run `dist/Toki.exe --open` to open the full workspace immediately. Launching without `--open` starts with the floating icon and tray control.

## Use

- Drag the floating `T` to move it. Click to open chat; right-click for its menu.
- Press `Ctrl+Alt+T` to open TOKI from anywhere. Change this to `Ctrl+Shift+Space` or turn it off in Settings. If another app already owns a shortcut, TOKI asks you to choose another.
- Use the tray icon to open TOKI, create a new chat, open reminders, email, usage, or settings, pause notifications, restart, or exit.
- Reminders: enter text, date and time, and optional daily or weekly recurrence. Due reminders display a tray notification with a sound. Snooze, mark done, or delete them from the Reminders page.
- Chat shortcuts: `open calculator`, `open file explorer`, `open task manager`, `open downloads`, `open vs code`, `open chrome`, `take a screenshot`, `read clipboard`, `copy to clipboard <text>`, and `remind me to study at 7 pm`.
- Settings: choose OpenAI or another OpenAI-compatible HTTPS endpoint, model, API key, startup behavior, notifications, and floating icon size. The key is encrypted locally. The model name must be supported by the configured provider. Chat responses stream when the provider supports OpenAI-compatible chat completion streaming.
- App shortcuts: in Settings, choose an `.exe` or `.lnk` file and give it a name. Say `open <name>` in Chat to launch it. TOKI never runs a chat-generated terminal command.
- Email: connect Gmail or Outlook through browser-based OAuth. The Email tab shows the active account icon and address; use **Active sender** to switch between saved accounts. **+ Gmail account** opens Google's account chooser. TOKI shows sign-in progress and lets you cancel while the browser is open. Wait until the browser displays the TOKI confirmation page before returning to the app. You can read the latest ten inbox messages and send an email after reviewing a confirmation dialog, or open a draft in the default mail application. TOKI stores OAuth tokens encrypted for the current Windows user.
- Usage: enter your own plan, limit, used count, reset date, and alert thresholds. The percentage is explicitly a manual estimate.
- Voice: click the microphone button to invoke Windows voice typing in the chat input. Windows manages microphone permission. Spoken responses can be enabled in Settings; speech runs locally through the Windows SAPI voice and its speed can be adjusted.

## Current scope and limits

This build is a working desktop foundation, but it does **not** yet include a built-in speech recognizer or voice selection, native Windows toast notifications, email thread summaries, reply/forward and saved drafts, trusted-recipient auto-send, or a fully styled animation system. Windows voice typing is launched on demand. Chat reminders support a simple time phrase; use the Reminders tab for exact dates and recurrence. The .NET 6 Desktop Runtime is required by the published executable.

## Email OAuth setup

Gmail and Outlook connections require an OAuth application registration owned by the person deploying TOKI. The current local build has been configured from the Google Desktop OAuth JSON downloaded on November 6, 2026. Its client ID and a Windows DPAPI encrypted client secret are copied into `oauth.local.json` and the published folder. The original secret is never printed or copied in plaintext by TOKI setup. To connect an account, launch TOKI, open **Email**, and click **+ Gmail account**. Complete Google's browser sign-in and consent, then wait for the TOKI confirmation page. The app displays the connected address when the token exchange and profile lookup finish.

For a different Google OAuth client, import its complete downloaded Desktop app OAuth JSON in Settings, then save settings. `ConfigureGoogleAuth.ps1` can read the matching JSON from Downloads and encrypt its secret for the current Windows user before rebuilding. The local `oauth.local.json` file is excluded from source control. The encrypted file is bound to this Windows user; import the JSON again on another PC or Windows account.

- For Gmail, create a **Desktop app** OAuth client and enable the Gmail API. Configure the consent screen with the Gmail read and send scopes, download its JSON, then use **Import Google desktop OAuth JSON** in TOKI Settings. Google may require verification before the app can be used publicly. TOKI uses the system browser, PKCE, and a temporary loopback callback. See [Google's desktop OAuth guide](https://developers.google.com/identity/protocols/oauth2/native-app) and [Gmail setup quickstart](https://developers.google.com/workspace/gmail/api/quickstart/nodejs). Do not attach a billing account or enable paid services for this setup.
- For Outlook, register a **Mobile and desktop applications** client in Microsoft Entra. Add `http://localhost/callback/` as a redirect URI and grant `User.Read`, `Mail.Read`, and `Mail.Send` delegated permissions. Enable the public client flow. The temporary local port is ignored in localhost redirect matching. See [Microsoft's redirect URI guidance](https://learn.microsoft.com/entra/identity-platform/reply-url).

No email action is performed by the build process. Actual account connection and sending require the user to sign in and consent in the provider's browser flow.

### Google 403 during sign-in

If Google says **“Access blocked: toki has not completed the Google verification process”** and **“Error 403: access_denied”**, the OAuth app is in Testing and the signing-in account is not on its test user list. In the Google Cloud project that owns TOKI's Desktop OAuth client, open [Google Auth Platform → Audience](https://console.cloud.google.com/auth/audience), choose **Add users** under **Test users**, enter the Google account that will connect to TOKI, and save. Then retry **Connect Gmail** in TOKI. This is the [official Google procedure](https://developers.google.com/workspace/guides/configure-oauth-consent). Testing authorizations may expire after seven days; [Google explains that limit](https://support.google.com/cloud/answer/15549945).

TOKI does not have a supported source for live ChatGPT subscription quota. API usage is a separate measure and is never displayed as ChatGPT subscription usage. OpenAI's [API credits documentation](https://developers.openai.com/api/docs/guides/chatgpt-api-credits) explains the separation. Use [ChatGPT settings → Usage](https://chatgpt.com/settings/usage) to view whatever plan information your account provides.

No email password, browser cookie, ChatGPT session token, or undocumented endpoint is used.
