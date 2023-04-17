using System.Text;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;

namespace GPT.CLI.Chat.Discord;

public class DiscordBot : IHostedService
{
    public record ChannelState
    {
        private ChatBot.ChatState _state;

        [JsonIgnore]
        public ChatBot ChatBot { get; set; }

        public ChatBot.ChatState State
        {
            get
            {
                if (ChatBot != null)
                {
                    return ChatBot.State;
                }

                return _state;
            }
            set
            {
                if (ChatBot != null)
                {
                    ChatBot.State = value;
                }
                _state = value;
            }
        }

        public ChannelOptions Options { get; set; }
    }

    public record ChannelOptions
    {
        public bool Enabled { get; set; }
        public bool Muted { get; set; }
    }


    private readonly DiscordSocketClient _client;
    private readonly IConfiguration _configuration;
    private readonly OpenAILogic _openAILogic;
    private readonly GPTParameters _defaultParameters;
    private readonly Dictionary<ulong, ChannelState> _channelBots = new();

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
        
        // Load state from discordState.json
        await LoadState();

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();


        _client.Log += LogAsync;

        _client.InteractionCreated += HandleInteractionAsync;

        _client.PresenceUpdated += ((user, presence, updated) => Task.CompletedTask);
        _client.MessageReceived += async message =>
        {
            Task.Run(async () =>
            {
                await MessageReceivedAsync(message);
            }, cancellationToken);
        };

        _client.MessageUpdated += async (oldMessage, newMessage, channel) =>
        {
            if (newMessage.Content != null && oldMessage.Value?.Content != newMessage.Content)
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

    private async Task LoadState()
    {
        if (Directory.Exists("channels"))
        {
            var files = Directory.GetFiles("channels");
            foreach (var file in files)
            {
                ulong channelId = Convert.ToUInt64(Path.GetFileNameWithoutExtension(file));
                await using var stream = File.OpenRead(file);
                await ReadAsync(channelId, stream);
            }
        }
    }

    private async Task SaveState()
    {
        if (!Directory.Exists("channels"))
        {
            Directory.CreateDirectory("channels");
        }

        foreach (var (channelId, channel) in _channelBots)
        {
            await using var stream = File.OpenWrite($"./channels/{channelId}.json");
            WriteAsync(channelId, stream);
        }
    }

    private async Task SaveChannelState(ulong channelId)
    {
        if (!Directory.Exists("channels"))
        {
            Directory.CreateDirectory("channels");
        }
        await using var stream = File.OpenWrite($"./channels/{channelId}.json");
        WriteAsync(channelId, stream);
    }

    // Method to write state to a Stream in JSON format
    private void WriteAsync(ulong channelId, Stream stream)
    {
        if (_channelBots.TryGetValue(channelId, out var channelState))
        {
            // prepare serializer options
            // Serialize channelState to stream using Newtonsoft
            var serializer = new JsonSerializer();
            var str = JsonConvert.SerializeObject(channelState);

            // write string to stream
            using var writer = new StreamWriter(stream);
            writer.Write(str);
        }
    }

    // Method to read state from a Stream in JSON format
    private async Task ReadAsync(ulong channelId, Stream stream)
    {
        try
        {
            // Deserialize channelState from stream using Newtonsoft
            
            var channelState = JsonConvert.DeserializeObject<ChannelState>(await new StreamReader(stream).ReadToEndAsync());

            if (channelState != null)
            {
                channelState.ChatBot = new ChatBot(_openAILogic, channelState.State.Parameters);
                _channelBots[channelId] = channelState;
            }
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync(ex.ToString());
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Save state to discordState.json
        await SaveState();
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
            channel = InitializeChannel(message.Channel.Id);
        }

        if (!channel.Options.Enabled)
            return;

        if (message.Content.StartsWith("!ignore"))
            return;

        // Add this message as a chat log
        await channel.ChatBot.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.User, message.Content));

        if (!channel.Options.Muted)
        {
            // Get the response from the bot
            var responses = channel.ChatBot.GetResponseAsync();
            // Add the response as a chat log


            using var typingState = message.Channel.EnterTypingState();

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
                    await Console.Out.WriteLineAsync(
                        $"Error code {response.Error?.Code}: {response.Error?.Message}");
                }
            }

            int chunkSize = 2000;
            int currentPosition = 0;

            while (currentPosition < sb.Length)
            {
                var size = Math.Min(chunkSize, sb.Length - currentPosition);
                var chunk = sb.ToString(currentPosition, size);
                currentPosition += size;

                var responseMessage = new ChatMessage(StaticValues.ChatMessageRoles.Assistant, chunk);

                await channel.ChatBot.AddMessage(responseMessage);
                await message.Channel.SendMessageAsync(responseMessage.Content);
            }
        
        }

        await SaveChannelState(message.Channel.Id);
    }

    private ChannelState InitializeChannel(ulong channelId)
    {
        ChannelState channel = new()
        {
            ChatBot = new(_openAILogic, Clone(_defaultParameters)),
            Options = new()
        };

        channel.ChatBot.State.PrimeDirective = new(StaticValues.ChatMessageRoles.System,
            "I'm a Discord Chat Bot named GPTInfoBot. Every message to the best of your ability.");
        _channelBots[channelId] = channel;

        return channel;
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
            chatBot = InitializeChannel(channel.Id);
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
                        chatBot.ChatBot.ClearMessages();
                    }
                    else if (clearOptionValue == "instructions")
                    {
                        chatBot.ChatBot.ClearInstructions();
                    }
                    else if (clearOptionValue == "all")
                    {
                        chatBot.ChatBot.ClearMessages();
                        chatBot.ChatBot.ClearInstructions();
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
                        chatBot.ChatBot.AddInstruction(new(StaticValues.ChatMessageRoles.System, instruction));

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
                    try
                    {
                        await command.RespondAsync($"InstructionStr: {chatBot.ChatBot.InstructionStr}");
                    }
                    catch (Exception ex)
                    {
                        await Console.Out.WriteLineAsync(ex.Message);
                    }
                    break;
                case "enabled":
                    if (option.Value is bool enabled)
                    {
                        chatBot.Options.Enabled = enabled;
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
                        chatBot.Options.Muted = muted;

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

        await SaveChannelState(channel.Id);
    }
}