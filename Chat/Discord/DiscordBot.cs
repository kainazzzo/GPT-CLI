using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
        var newGptParameters = new GPTParameters
        {
            MaxTokens = gptParameters.MaxTokens,
            Temperature = gptParameters.Temperature,
            TopP = gptParameters.TopP,
            PresencePenalty = gptParameters.PresencePenalty,
            FrequencyPenalty = gptParameters.FrequencyPenalty,
            N = gptParameters.N,
            Stop = gptParameters.Stop,
            LogitBias = gptParameters.LogitBias,
            User = gptParameters.User,
            Input = gptParameters.Input,
            EmbedFilenames = gptParameters.EmbedFilenames,
            ChunkSize = gptParameters.ChunkSize,
            ClosestMatchLimit = gptParameters.ClosestMatchLimit,
            EmbedDirectoryNames = gptParameters.EmbedDirectoryNames,
            BotToken = gptParameters.BotToken,
            MaxChatHistoryLength = gptParameters.MaxChatHistoryLength,
            ApiKey = gptParameters.ApiKey,
            BaseDomain = gptParameters.BaseDomain,
            Prompt = gptParameters.Prompt,
            Config = gptParameters.Config,
            Model = gptParameters.Model
        };


        return newGptParameters;
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

        // This is required for slash commands to work
        _client.InteractionCreated += HandleInteractionAsync;
        

        // Message receiver is going to run in parallel
        _client.MessageReceived += message =>
        {
#pragma warning disable CS4014
            return Task.Run(async () =>
#pragma warning restore CS4014
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

        // Handle emoji reactions.
        _client.ReactionAdded += HandleReactionAsync;


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
    }

    private async Task HandleReactionAsync(Cacheable<IUserMessage, ulong> userMessage, Cacheable<IMessageChannel, ulong> messageChannel, SocketReaction reaction)
    {
        if (reaction.Emote.Name == "⬆️")
        {
            var message = await userMessage.GetOrDownloadAsync();
            var channel = await messageChannel.GetOrDownloadAsync();


            if (message.Author.Id == _client.CurrentUser.Id)
            {
                var channelState = _channelBots[channel.Id];
                if (channelState.Options.Enabled)
                {
                    var chatBot = channelState.ChatBot;
                    chatBot.AddInstruction(new ChatMessage(StaticValues.ChatMessageRoles.User, message.Content));

                    using var typingState = channel.EnterTypingState();
                    await message.RemoveReactionAsync(reaction.Emote, reaction.UserId);
                    await channel.SendMessageAsync($"Instruction added: {message.Content}");

                    Console.WriteLine(
                        $"{reaction.User.Value.Username} reacted with an arrow up. Message promoted to instruction: {message.Content}");
                }
            }
        }
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
            await using var stream = File.Create($"./channels/{channelId}.json");
            await WriteAsync(channelId, stream);
        }
    }

    private async Task SaveChannelState(ulong channelId)
    {
        if (!Directory.Exists("channels"))
        {
            Directory.CreateDirectory("channels");
        }
        await using var stream = File.Create($"./channels/{channelId}.json");
        await WriteAsync(channelId, stream);
    }

    // Method to write state to a Stream in JSON format
    private async Task WriteAsync(ulong channelId, Stream stream)
    {
        if (_channelBots.TryGetValue(channelId, out var channelState))
        {
            // prepare serializer options
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            // Serialize channelState to stream
            await JsonSerializer.SerializeAsync(stream, channelState, options);
        }
    }

    // Method to read state from a Stream in JSON format
    private async Task ReadAsync(ulong channelId, Stream stream)
    {
        try
        {
            // Deserialize channelState from stream
            var channelState = await JsonSerializer.DeserializeAsync<ChannelState>(stream);

            if (channelState != null)
            {
                channelState.ChatBot = new ChatBot(_openAILogic, channelState.State.Parameters)
                {
                    State = channelState.State
                };
                channelState.State = channelState.State;
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

    private async Task MessageReceivedAsync(IMessage message)
    {
        if (message.Author.Id == _client.CurrentUser.Id)
            return;

        // Handle the received message here
        // ...
        if (!_channelBots.TryGetValue(message.Channel.Id, out var channel))
        {
            channel = InitializeChannel(message.Channel.Id);
        }
        else if (channel.State.PrimeDirectives.Count != _defaultPrimeDirective.Count || channel.State.PrimeDirectives[0].Content != _defaultPrimeDirective[0].Content)
        {
            channel.State.PrimeDirectives = PrimeDirective.ToList();
        }

        if (!channel.Options.Enabled)
            return;

        if (message.Content.StartsWith("!ignore"))
            return;

        // Add this message as a chat log
        await channel.ChatBot.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.User, $"<{message.Author.Username}> {message.Content}"));

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

    private readonly List<ChatMessage> _defaultPrimeDirective = new(1)
    {
        new
        (StaticValues.ChatMessageRoles.System,
            "This is the Prime Directive: This is a chat bot running in [GPT-CLI](https://github.com/kainazzzo/GPT-CLI). Answer questions and" +
            " provide responses in Discord message formatting. Encourage users to add instructions with /gptcli or by using the :up_arrow:" +
            " emoji reaction on any message. Instructions are like 'sticky' chat messages that provide upfront context to the bot.")
    };

    

    public IEnumerable<ChatMessage> PrimeDirective => _defaultPrimeDirective;

    private ChannelState InitializeChannel(ulong channelId)
    {
        ChannelState channel = new()
        {
            ChatBot = new(_openAILogic, Clone(_defaultParameters))
            {
                State = new()
                    { PrimeDirectives = PrimeDirective.ToList() }
            },
            Options = new(),
        };
        channel.State = channel.ChatBot.State;
        channel.State.Parameters = Clone(_defaultParameters);

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