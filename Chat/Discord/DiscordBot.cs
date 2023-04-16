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
            Prompt = gptParameters.Prompt,
            BotToken = gptParameters.BotToken,
            MaxChatHistoryLength = gptParameters.MaxChatHistoryLength
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

    private async Task LogAsync(LogMessage log)
    {
        await Console.Out.WriteLineAsync(log.ToString());
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

        if (message.Content.StartsWith("!ignore"))
            return;

        // Add this message as a chat log
        await channel.chatBot.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.User, message.Content));

        if (!channel.options.Muted)
        {
            // Get the response from the bot
            var responses = channel.chatBot.GetResponseAsync();
            // Add the response as a chat log
            
            var sb = new StringBuilder();
            // Send the response to the channel
            await foreach (var response in responses)
            {
                if (response.Successful)
                {
                    var content = response?.Choices?.FirstOrDefault()?.Message.Content;
                    if (content is not null)
                    {
                        sb.Append(content);
                    }
                }
                else
                {
                    await Console.Out.WriteLineAsync($"Error code {response.Error?.Code}: {response.Error?.Message}");
                }

            }

            if (sb.Length > 0)
            {
                var responseMessage = new ChatMessage(StaticValues.ChatMessageRoles.Assistant, sb.ToString());

                await channel.chatBot.AddMessage(responseMessage);
                await message.Channel.SendMessageAsync(responseMessage.Content);
            }
        }
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
                    {
                        try
                        {
                            await command.RespondAsync(
                                $"{char.ToUpper(clearOptionValue[0])}{clearOptionValue.Substring(1)} cleared.");
                        }
                        catch (Exception ex)
                        {
                            await Console.Out.WriteLineAsync(ex.Message);
                        }
                    }

                    break;
                case "instruction":
                    var instruction = option.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(instruction))
                    {
                        chatBot.chatBot.AddInstruction(new(StaticValues.ChatMessageRoles.System, instruction));

                        try
                        {
                            await command.RespondAsync($"Instruction added: {instruction}");
                        }
                        catch (Exception ex)
                        {
                            await Console.Out.WriteLineAsync(ex.Message);
                        }
                    }
                    break;
                case "instructions":
                    
                    await command.RespondAsync($"Instructions: {chatBot.chatBot.Instructions}");
                    break;
                case "enabled":
                    if (option.Value is bool enabled)
                    {
                        chatBot.options.Enabled = enabled;
                        try
                        {
                            await command.RespondAsync($"Chat bot {(enabled ? "enabled" : "disabled")}.");
                        }
                        catch (Exception ex)
                        {
                            await Console.Out.WriteLineAsync(ex.Message);
                        }
                    }
                    break;
                case "mute":
                    if (option.Value is bool muted)
                    {
                        chatBot.options.Muted = muted;

                        try
                        {
                            await command.RespondAsync($"Chat bot {(muted ? "muted" : "un-muted")}.");
                        }
                        catch (Exception ex)
                        {
                            await Console.Out.WriteLineAsync(ex.Message);
                        }
                    }
                    break;
                case "embed":
                    if (option.Value is string embed)
                    {
                        try
                        {
                            await command.RespondAsync($"Embed set to {embed}.");
                        }
                        catch (Exception ex)
                        {
                            await Console.Out.WriteLineAsync(ex.Message);
                        }
                    }
                    break;
            }
        }
    }
}