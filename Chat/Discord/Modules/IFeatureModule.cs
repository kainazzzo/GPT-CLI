using Discord;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord.Commands;
using OpenAI.ObjectModels.RequestModels;

namespace GPT.CLI.Chat.Discord.Modules;

public interface IFeatureModule
{
    string Id { get; }
    string Name { get; }
    IReadOnlyCollection<string> DependsOn { get; }

    Task InitializeAsync(DiscordModuleContext context, CancellationToken cancellationToken);
    Task OnReadyAsync(DiscordModuleContext context, CancellationToken cancellationToken);
    Task OnMessageReceivedAsync(DiscordModuleContext context, SocketMessage message, CancellationToken cancellationToken);
    Task OnMessageUpdatedAsync(DiscordModuleContext context, Cacheable<IMessage, ulong> oldMessage, SocketMessage newMessage, ISocketMessageChannel channel, CancellationToken cancellationToken);
    Task OnReactionAddedAsync(DiscordModuleContext context, Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction, CancellationToken cancellationToken);
    Task<bool> OnInteractionAsync(DiscordModuleContext context, SocketInteraction interaction, CancellationToken cancellationToken);
    Task OnMessageCommandExecutedAsync(DiscordModuleContext context, SocketMessageCommand command, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatMessage>> GetAdditionalMessageContextAsync(DiscordModuleContext context, SocketMessage message, InstructionGPT.ChannelState channel, CancellationToken cancellationToken);
    IReadOnlyList<SlashCommandContribution> GetSlashCommandContributions(DiscordModuleContext context);

    // Unified command definitions used for both slash building and natural-language (tool) invocation.
    IReadOnlyList<GptCliFunction> GetGptCliFunctions(DiscordModuleContext context);
}
