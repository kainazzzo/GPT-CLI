using Discord;
using Discord.WebSocket;
using GPT.CLI;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Commands;
using GPT.CLI.Chat.Discord.Modules;

namespace GptCli.Tests.TestDoubles;

internal sealed class FakeFeatureModule : IFeatureModule
{
    public string Id { get; init; }
    public string Name { get; init; }
    public IReadOnlyCollection<string> DependsOn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<GptCliFunction> Functions { get; init; } = Array.Empty<GptCliFunction>();
    public IReadOnlyList<SlashCommandContribution> Slash { get; init; } = Array.Empty<SlashCommandContribution>();
    public IReadOnlyList<ChatMessage> ExtraContext { get; init; } = Array.Empty<ChatMessage>();
    public Exception ThrowOnSlash { get; init; }
    public Exception ThrowOnFunctions { get; init; }
    public Exception ThrowOnContext { get; init; }
    public Exception ThrowOnReady { get; init; }

    public Task InitializeAsync(DiscordModuleContext context, CancellationToken cancellationToken)
        => ThrowOnReady == null ? Task.CompletedTask : throw ThrowOnReady;

    public Task OnReadyAsync(DiscordModuleContext context, CancellationToken cancellationToken)
        => ThrowOnReady == null ? Task.CompletedTask : throw ThrowOnReady;

    public Task OnMessageReceivedAsync(DiscordModuleContext context, SocketMessage message, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task OnMessageUpdatedAsync(DiscordModuleContext context, Cacheable<IMessage, ulong> oldMessage, SocketMessage newMessage, ISocketMessageChannel channel, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task OnReactionAddedAsync(DiscordModuleContext context, Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<bool> OnInteractionAsync(DiscordModuleContext context, SocketInteraction interaction, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task OnMessageCommandExecutedAsync(DiscordModuleContext context, SocketMessageCommand command, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ChatMessage>> GetAdditionalMessageContextAsync(
        DiscordModuleContext context,
        SocketMessage message,
        InstructionGPT.ChannelState channel,
        CancellationToken cancellationToken)
    {
        if (ThrowOnContext != null)
        {
            throw ThrowOnContext;
        }

        return Task.FromResult(ExtraContext);
    }

    public IReadOnlyList<SlashCommandContribution> GetSlashCommandContributions(DiscordModuleContext context)
    {
        if (ThrowOnSlash != null)
        {
            throw ThrowOnSlash;
        }

        return Slash;
    }

    public IReadOnlyList<GptCliFunction> GetGptCliFunctions(DiscordModuleContext context)
    {
        if (ThrowOnFunctions != null)
        {
            throw ThrowOnFunctions;
        }

        return Functions;
    }
}
