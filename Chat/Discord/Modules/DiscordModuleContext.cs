using System.Collections.Concurrent;
using Discord.WebSocket;
using GPT.CLI.Embeddings;
using Microsoft.Extensions.Configuration;

namespace GPT.CLI.Chat.Discord.Modules;

public sealed class DiscordModuleContext
{
    public DiscordModuleContext(
        DiscordSocketClient client,
        IConfiguration configuration,
        OpenAILogic openAILogic,
        GptOptions defaultParameters,
        ConcurrentDictionary<ulong, InstructionGPT.ChannelState> channelStates,
        ConcurrentDictionary<ulong, List<Document>> documents,
        ConcurrentDictionary<ulong, List<FactoidEntry>> channelFactoids,
        ConcurrentDictionary<ulong, List<FactoidMatchEntry>> channelFactoidMatches,
        ConcurrentDictionary<ulong, FactoidMatchStats> channelFactoidMatchStats,
        ConcurrentDictionary<ulong, ulong> channelGuildIds,
        IDiscordModuleHost host)
    {
        Client = client;
        Configuration = configuration;
        OpenAILogic = openAILogic;
        DefaultParameters = defaultParameters;
        ChannelStates = channelStates;
        Documents = documents;
        ChannelFactoids = channelFactoids;
        ChannelFactoidMatches = channelFactoidMatches;
        ChannelFactoidMatchStats = channelFactoidMatchStats;
        ChannelGuildIds = channelGuildIds;
        Host = host;
    }

    public DiscordSocketClient Client { get; }
    public IConfiguration Configuration { get; }
    public OpenAILogic OpenAILogic { get; }
    public GptOptions DefaultParameters { get; }
    public ConcurrentDictionary<ulong, InstructionGPT.ChannelState> ChannelStates { get; }
    public ConcurrentDictionary<ulong, List<Document>> Documents { get; }
    public ConcurrentDictionary<ulong, List<FactoidEntry>> ChannelFactoids { get; }
    public ConcurrentDictionary<ulong, List<FactoidMatchEntry>> ChannelFactoidMatches { get; }
    public ConcurrentDictionary<ulong, FactoidMatchStats> ChannelFactoidMatchStats { get; }
    public ConcurrentDictionary<ulong, ulong> ChannelGuildIds { get; }
    public IDiscordModuleHost Host { get; }
}
