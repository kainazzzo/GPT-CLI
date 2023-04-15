using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GPT.CLI.Discord;

public class DiscordBot : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly IConfiguration _configuration;

    public DiscordBot(DiscordSocketClient client, IConfiguration configuration)
    {
        _client = client;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Use _configuration to access your configuration settings
        string token = _configuration["Discord:BotToken"];
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        _client.Log += LogAsync;
        _client.MessageReceived += MessageReceivedAsync;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
    }

    private Task LogAsync(LogMessage log)
    {
        Console.WriteLine(log.ToString());
        return Task.CompletedTask;
    }

    private async Task MessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.Id == _client.CurrentUser.Id)
            return;

        // Handle the received message here
        // ...

        await Task.CompletedTask;
    }
}