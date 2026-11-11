using System.Text.RegularExpressions;

namespace Toki;

/// <summary>Deterministic chatbot backed by a preloaded answer and keyword library.</summary>
internal static class OfflineChatService
{
    private sealed record Intent(string[] Phrases, string[] Keywords, string[] Answers);

    private static readonly Intent[] Intents =
    {
        new(new[] { "hello", "hi", "hey", "good morning", "good afternoon", "good evening", "how are you" }, new[] { "hello", "hi", "hey", "morning", "afternoon", "evening", "greetings" }, new[] { "Hello! I’m TOKI. What would you like to do?", "Hi! I’m ready to help with reminders, apps, email, and everyday questions.", "Hey there — how can I help?" }),
        new(new[] { "who are you", "what are you", "tell me about yourself", "your name" }, new[] { "toki", "assistant", "name", "robot" }, new[] { "I’m TOKI, your desktop assistant. This chat runs locally from preloaded answers, so it works without an AI account or internet connection.", "I’m TOKI — a private offline desktop helper for quick answers and actions." }),
        new(new[] { "what can you do", "help me", "show help", "how do i use you", "capabilities" }, new[] { "help", "capabilities", "features", "use" }, new[] { "I can answer common questions offline, set reminders, open Calculator, File Explorer, Task Manager, Downloads, VS Code, or Chrome, take screenshots, read or copy clipboard text, and open the email composer.", "Try: ‘remind me to call Sam at 6 PM’, ‘open calculator’, ‘take a screenshot’, ‘read clipboard’, or ask me a general question." }),
        new(new[] { "thank you", "thanks", "thx", "that helped", "perfect" }, new[] { "thanks", "thank", "grateful", "appreciate" }, new[] { "You’re welcome!", "Glad I could help.", "Anytime." }),
        new(new[] { "what time is it", "current time", "tell me the time", "date today", "what is today's date" }, new[] { "time", "date", "today", "clock" }, new[] { "It’s {TIME} on this computer." }),
        new(new[] { "are you offline", "do you use the internet", "do i need an api", "api key", "without internet", "private chat" }, new[] { "offline", "internet", "network", "api", "key", "private", "local" }, new[] { "Yes. TOKI’s chatbot is local and works without an API key or internet connection. Your chat stays in TOKI’s local data file.", "No API is required for this chat. I match your words against the answers bundled in the application." }),
        new(new[] { "how do reminders work", "reminder help", "can you remind me", "show my reminders" }, new[] { "reminder", "remind", "schedule", "alarm" }, new[] { "Say ‘remind me to [task] at [time]’, for example ‘remind me to stretch at 7 PM’. You can also use the Reminders page for an exact date and repeat schedule.", "I can create reminders from a sentence. Include a time such as 8:30 AM or 6 PM." }),
        new(new[] { "how do i open an app", "launch an app", "open a program", "start calculator", "open file explorer" }, new[] { "open", "launch", "start", "run", "calculator", "explorer", "chrome" }, new[] { "Say ‘open Calculator’, ‘open File Explorer’, ‘open Task Manager’, ‘open Downloads’, ‘open VS Code’, or ‘open Chrome’. Custom apps can be added in Settings." }),
        new(new[] { "screenshot help", "how to take a screenshot", "capture my screen" }, new[] { "screenshot", "screen", "capture" }, new[] { "Say ‘take a screenshot’ and TOKI will save a PNG in your Pictures\\TOKI folder." }),
        new(new[] { "clipboard help", "what is on my clipboard", "copy something" }, new[] { "clipboard", "paste", "copy" }, new[] { "Say ‘read clipboard’ to show text currently copied, or ‘copy to clipboard [text]’ to place text there." }),
        new(new[] { "email help", "send an email", "compose an email", "mail help" }, new[] { "email", "mail", "gmail", "outlook", "compose", "inbox" }, new[] { "Use the Email page to connect Gmail or Outlook, view messages, and compose mail. I’ll never send a message without your review." }),
        new(new[] { "why are you not answering", "chat is broken", "ai is not working", "not working", "no response", "it failed" }, new[] { "broken", "failed", "error", "working", "answer", "response" }, new[] { "The chatbot is now running in offline mode. Try a shorter request such as ‘set a reminder’, ‘open calculator’, or ‘what can you do’.", "I don’t need an API to answer. Rephrase your request with a few key words and I’ll match it to my built-in answer library." }),
        new(new[] { "windows shortcut", "keyboard shortcut", "hotkey", "open toki" }, new[] { "shortcut", "hotkey", "keyboard", "ctrl", "windows" }, new[] { "You can configure TOKI’s keyboard shortcut in Settings. The default is Ctrl+Alt+T." }),
        new(new[] { "privacy", "where is my data", "delete chat", "clear history" }, new[] { "privacy", "data", "delete", "history", "stored" }, new[] { "Chat history is stored locally on this Windows account. Open Settings → Privacy & data to clear chat history or delete the old API key." }),
        new(new[] { "tell me a joke", "make me laugh", "joke" }, new[] { "joke", "funny", "laugh" }, new[] { "Why did the computer get cold? It left its Windows open.", "I would tell you a UDP joke, but you might not get it." }),
        new(new[] { "goodbye", "bye", "see you later" }, new[] { "bye", "goodbye", "later" }, new[] { "See you later!", "Bye — TOKI will be here when you need me." }),
    };

    public static string GetResponse(string prompt)
    {
        var normalized = Normalize(prompt);
        if (normalized.Length == 0) return "Ask me something. I can answer offline and help with reminders, apps, screenshots, clipboard, and email.";
        var best = Intents.Select(intent => (intent, score: Score(normalized, intent))).OrderByDescending(x => x.score).First();
        if (best.score >= 2)
        {
            var answer = best.intent.Answers[Math.Abs(prompt.GetHashCode()) % best.intent.Answers.Length];
            return answer.Replace("{TIME}", $"{DateTime.Now:dddd, MMMM d, yyyy} at {DateTime.Now:h:mm tt}");
        }
        var topic = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(8);
        return $"I’m running offline, so I answer from my built-in knowledge. I didn’t find a close match for ‘{string.Join(' ', topic)}’. Try asking what I can do, or mention a reminder, app, screenshot, clipboard, email, privacy, or offline chat.";
    }

    private static int Score(string prompt, Intent intent)
    {
        var score = intent.Phrases.Any(phrase => prompt.Contains(phrase, StringComparison.Ordinal)) ? 5 : 0;
        foreach (var keyword in intent.Keywords)
            if (Regex.IsMatch(prompt, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.CultureInvariant)) score++;
        return score;
    }

    private static string Normalize(string value) => Regex.Replace(Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}\s']", " "), @"\s+", " ").Trim();
}
