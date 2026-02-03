using Discord;

namespace GPT.CLI.Chat.Discord.Modules;

public sealed record SlashCommandContribution(string TargetOption, SlashCommandOptionBuilder Option)
{
    public static SlashCommandContribution TopLevel(SlashCommandOptionBuilder option)
        => new(null, option);

    public static SlashCommandContribution ForOption(string targetOption, SlashCommandOptionBuilder option)
        => new(targetOption, option);
}
