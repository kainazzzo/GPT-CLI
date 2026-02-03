using Discord;

namespace GPT.CLI.Chat.Discord.Modules;

public interface IDiscordModuleHost
{
    InstructionGPT.ChannelState GetOrCreateChannelState(IChannel channel);
    InstructionGPT.ChannelState GetOrCreateChannelState(ulong channelId);
    void EnsureChannelStateMetadata(InstructionGPT.ChannelState state, IGuildChannel guildChannel);
    ulong GetGuildId(IMessageChannel channel);
    bool IsChannelGuildMatch(InstructionGPT.ChannelState state, IMessageChannel channel, string context);
    Task SaveCachedChannelStateAsync(ulong channelId);
}
