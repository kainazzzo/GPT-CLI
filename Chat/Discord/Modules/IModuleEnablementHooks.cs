using Discord;

namespace GPT.CLI.Chat.Discord.Modules;

// Optional module hook: lets the core "/gptcli modules enable|disable" command trigger module-specific side effects.
public interface IModuleEnablementHooks
{
    Task OnModuleEnabledChangedAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        IMessageChannel channel,
        bool enabled,
        CancellationToken cancellationToken);
}
