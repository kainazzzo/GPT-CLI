using Discord;
using System;

namespace GPT.CLI.Chat.Discord;

public static class DiscordTyping
{
    public static IDisposable Begin(IMessageChannel channel)
    {
        // Discord.Net will keep the typing indicator alive until disposed.
        // This is best-effort; if Discord rejects typing for a channel, swallow.
        if (channel == null)
        {
            return NoopDisposable.Instance;
        }

        try
        {
            return channel.EnterTypingState();
        }
        catch
        {
            return NoopDisposable.Instance;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

