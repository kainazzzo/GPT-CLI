using System.Text;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;

namespace GPT.CLI.Chat.Discord;

public class DiscordBot : IHostedService
{
    public record ChannelOptions
    {
        public bool Enabled { get; set; }
        public bool Muted { get; set; }
    }


    private readonly DiscordSocketClient _client;
    private readonly IConfiguration _configuration;
    private readonly OpenAILogic _openAILogic;
    private readonly GPTParameters _defaultParameters;
    private readonly Dictionary<ulong, (ChatBot chatBot, ChannelOptions options)> _channelBots = new();

    public DiscordBot(DiscordSocketClient client, IConfiguration configuration, OpenAILogic openAILogic, GPTParameters defaultParameters)
    {
        _client = client;
        _configuration = configuration;
        _openAILogic = openAILogic;
        _defaultParameters = defaultParameters;
    }

    // A method that clones GPTParameters
    private GPTParameters Clone(GPTParameters gptParameters)
    {
        // Clone gptParameters to a new instance and copy all properties one by one
        GPTParameters parameters = new()
        {
            ApiKey = gptParameters.ApiKey,
            BaseDomain = gptParameters.BaseDomain,
            Config = gptParameters.Config,
            Model = gptParameters.Model,
            MaxTokens = gptParameters.MaxTokens,
            Temperature = gptParameters.Temperature,
            TopP = gptParameters.TopP,
            N = gptParameters.N,
            Stream = gptParameters.Stream,
            Stop = gptParameters.Stop,
            PresencePenalty = gptParameters.PresencePenalty,
            FrequencyPenalty = gptParameters.FrequencyPenalty,
            LogitBias = gptParameters.LogitBias,
            User = gptParameters.User,
            Input = gptParameters.Input,
            EmbedFilenames = gptParameters.EmbedFilenames,
            ChunkSize = gptParameters.ChunkSize,
            ClosestMatchLimit = gptParameters.ClosestMatchLimit,
            EmbedDirectoryNames = gptParameters.EmbedDirectoryNames,
            Prompt = gptParameters.Prompt
        };

        return parameters;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Use _configuration to access your configuration settings
        string token = _configuration["Discord:BotToken"];

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();


        _client.Log += LogAsync;

        _client.InteractionCreated += HandleInteractionAsync;

        _client.PresenceUpdated += ((user, presence, updated) => Task.CompletedTask);
        _client.MessageReceived += MessageReceivedAsync;
        _client.MessageUpdated += async (oldMessage, newMessage, channel) =>
        {
            if (oldMessage.Value.Content != newMessage.Content)
            {
                await MessageReceivedAsync(newMessage);
            }
        };
        _client.Ready += () =>
        {
            Console.WriteLine("Client is ready!");
            return Task.CompletedTask;
        };

        _client.MessageCommandExecuted += (command) =>
        {
            Console.WriteLine($"Command {command.CommandName} executed with result {command.Data.Message.Content}");
            return Task.CompletedTask;
        };
        // Register the interaction handler
        //_client.InteractionCreated += HandleInteractionAsync;
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
        if (!_channelBots.TryGetValue(message.Channel.Id, out var channel))
        {
            channel = (new (_openAILogic, Clone(_defaultParameters)), new ());
            channel.chatBot.AddInstruction(new (StaticValues.ChatMessageRoles.System, "You're a Discord Chat Bot named GPTInfoBot. Every message to the best of your ability."));
            _channelBots.Add(message.Channel.Id, channel);
        }

        if (!channel.options.Enabled)
            return;

        // Add this message as a chat log
        channel.chatBot.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.User, message.Content));

        if (!channel.options.Muted)
        {
            // Get the response from the bot
            var responses = channel.chatBot.GetResponseAsync();
            // Add the response as a chat log
            
            var sb = new StringBuilder();
            // Send the response to the channel
            await foreach (var response in responses)
            {
                sb.Append(response.Choices[0].Message.Content);
            }

            var responseMessage = new ChatMessage(StaticValues.ChatMessageRoles.Assistant, sb.ToString());

            channel.chatBot.AddMessage(responseMessage);
            await message.Channel.SendMessageAsync(responseMessage.Content);
        }

        await Task.CompletedTask;
    }

    private Task HandleInteractionAsync(SocketInteraction arg)
    {
        switch (arg.Type)
        {
            case InteractionType.ApplicationCommand:
                var command = (SocketSlashCommand)arg;
                switch (command.Data.Name)
                {
                    case "gptcli":
                        HandleGptCliCommand(command);
                        break;
                }
                break;
        }

        return Task.CompletedTask;
    }

    private async void HandleGptCliCommand(SocketSlashCommand command)
    {
        var channel = command.Channel;
        if (!_channelBots.TryGetValue(channel.Id, out var chatBot))
        {
            chatBot = new(new(_openAILogic, Clone(_defaultParameters)), new ChannelOptions());
            _channelBots.Add(channel.Id, chatBot);
        }


        var options = command.Data.Options;
        foreach (var option in options)
        {
            switch (option.Name)
            {
                case "clear":
                    var clearOptionValue = option.Value.ToString();
                    if (clearOptionValue == "messages")
                    {
                        chatBot.chatBot.ClearMessages();
                    }
                    else if (clearOptionValue == "instructions")
                    {
                        chatBot.chatBot.ClearInstructions();
                    }
                    else if (clearOptionValue == "all")
                    {
                        chatBot.chatBot.ClearMessages();
                        chatBot.chatBot.ClearInstructions();
                    }

                    if (clearOptionValue != null)
                        await command.RespondAsync($"{char.ToUpper(clearOptionValue[0])}{clearOptionValue.Substring(1)} cleared.");

                    break;
                case "instruction":
                    var instruction = option.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(instruction))
                    {
                        chatBot.chatBot.AddInstruction(new(StaticValues.ChatMessageRoles.System, instruction));
                        await command.RespondAsync($"Instruction added: {instruction}");
                    }
                    break;
                case "enabled":
                    if (option.Value is bool enabled)
                    {
                        chatBot.options.Enabled = enabled;
                        await command.RespondAsync($"Chat bot {(enabled ? "enabled" : "disabled")}.");
                    }
                    break;
                case "mute":
                    if (option.Value is bool muted)
                    {
                        chatBot.options.Muted = muted;
                        await command.RespondAsync($"Chat bot {(muted ? "muted" : "unmuted")}.");
                    }
                    break;
                case "embed":
                    break;
            }
        }
    }
}