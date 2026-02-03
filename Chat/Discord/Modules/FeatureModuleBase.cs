using Discord;
using Discord.WebSocket;
using OpenAI.ObjectModels.RequestModels;

namespace GPT.CLI.Chat.Discord.Modules;

public abstract class FeatureModuleBase : IFeatureModule
{
    public abstract string Id { get; }
    public virtual string Name => Id;
    public virtual IReadOnlyCollection<string> DependsOn => Array.Empty<string>();

    public virtual Task InitializeAsync(DiscordModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnReadyAsync(DiscordModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnMessageReceivedAsync(DiscordModuleContext context, SocketMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnMessageUpdatedAsync(DiscordModuleContext context, Cacheable<IMessage, ulong> oldMessage, SocketMessage newMessage, ISocketMessageChannel channel, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnReactionAddedAsync(DiscordModuleContext context, Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<bool> OnInteractionAsync(DiscordModuleContext context, SocketInteraction interaction, CancellationToken cancellationToken)
        => Task.FromResult(false);
    public virtual Task OnMessageCommandExecutedAsync(DiscordModuleContext context, SocketMessageCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<IReadOnlyList<ChatMessage>> GetAdditionalMessageContextAsync(DiscordModuleContext context, SocketMessage message, InstructionGPT.ChannelState channel, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
}
