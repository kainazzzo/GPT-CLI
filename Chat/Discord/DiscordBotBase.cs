using System.Collections.Concurrent;
using Discord.WebSocket;
using GPT.CLI.Embeddings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GPT.CLI.Chat.Discord;

public abstract class DiscordBotBase : IHostedService
{
    protected DiscordBotBase(DiscordSocketClient client, IConfiguration configuration, OpenAILogic openAILogic, GptOptions defaultParameters)
    {
        Client = client;
        Configuration = configuration;
        OpenAILogic = openAILogic;
        DefaultParameters = defaultParameters;
    }

    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    protected readonly DiscordSocketClient Client;
    protected readonly IConfiguration Configuration;
    protected readonly OpenAILogic OpenAILogic;
    protected readonly GptOptions DefaultParameters;
    protected readonly ConcurrentDictionary<ulong, InstructionGPT.ChannelState> ChannelBots = new();
    protected readonly ConcurrentDictionary<ulong, List<Document>> Documents = new();
    protected readonly ConcurrentDictionary<ulong, List<FactoidEntry>> ChannelFactoids = new();
    protected readonly ConcurrentDictionary<ulong, List<FactoidMatchEntry>> ChannelFactoidMatches = new();
    protected readonly ConcurrentDictionary<ulong, FactoidMatchStats> ChannelFactoidMatchStats = new();


}
