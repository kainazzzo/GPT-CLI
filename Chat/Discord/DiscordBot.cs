﻿using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Discord;
using Discord.WebSocket;
using Mapster;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;

namespace GPT.CLI.Chat.Discord;

public class DiscordBot : IHostedService
{
    public record ChannelState
    {
        [JsonPropertyName("chat-state")]
        public ChatBot Chat { get; set; }

        [JsonPropertyName("options")]
        public ChannelOptions Options { get; set; }
    }

    public record ChannelOptions
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("muted")]
        public bool Muted { get; set; }
    }


    
    private readonly DiscordSocketClient _client;
    private readonly IConfiguration _configuration;
    private readonly OpenAILogic _openAILogic;
    private readonly GPTParameters _defaultParameters;
    private readonly ConcurrentDictionary<ulong, ChannelState> _channelBots = new();


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
        // Clone gptParameters to a new instance and copy all properties
        return gptParameters.Adapt<GPTParameters>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Use _configuration to access your configuration settings
        string token = _configuration["Discord:BotToken"];
        
        // Load state from discordState.json
        await LoadState();

        // Login and start
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        _client.Log += LogAsync;

        // This is required for slash commands to work
        _client.InteractionCreated += HandleInteractionAsync;
        

        // Message receiver is going to run in parallel
        _client.MessageReceived += HandleMessageReceivedAsync;

        _client.MessageUpdated += async (oldMessage, newMessage, channel) =>
        {
            if (newMessage.Content != null && oldMessage.Value?.Content != newMessage.Content)
            {
                await HandleMessageReceivedAsync(newMessage);
            }
        };

        // Handle emoji reactions.
        _client.ReactionAdded += HandleReactionAsync;



        _client.Ready += async () =>
        {
            await Console.Out.WriteLineAsync("Client is ready!");
        };

        _client.MessageCommandExecuted += async (command) =>
        {
            await Console.Out.WriteLineAsync($"Command {command.CommandName} executed with result {command.Data.Message.Content}");
        };
    }

    private async Task HandleReactionAsync(Cacheable<IUserMessage, ulong> userMessage, Cacheable<IMessageChannel, ulong> messageChannel, SocketReaction reaction)
    {
        if (reaction.UserId == _client.CurrentUser.Id)
        {
            return;
        }

        var message = await userMessage.GetOrDownloadAsync();
        var channel = await messageChannel.GetOrDownloadAsync();


        switch (reaction.Emote.Name)
        {
            case "📌":
            {
                var channelState = _channelBots[channel.Id];
                if (channelState.Options.Enabled)
                {
                    var chatBot = channelState.Chat;
                    chatBot.AddInstruction(new ChatMessage(StaticValues.ChatMessageRoles.User, message.Content));


                    using var typingState = channel.EnterTypingState();
                    await message.RemoveReactionAsync(reaction.Emote, reaction.UserId);
                    await message.ReplyAsync("Instruction added.");
                }

                break;
            }
            case "🔄":
            {
                // If the message is from the bot, ignore it. These aren't prompts.
                if (message.Author.Id == _client.CurrentUser.Id)
                {
                    return;
                }

                // remove the emoji
                await message.RemoveReactionAsync(reaction.Emote, reaction.User.Value);

                // Replay the message as a new message
                await HandleMessageReceivedAsync(message as SocketMessage);
                break;
            }

        }
    }

    private async Task LoadState()
    {
        await Console.Out.WriteLineAsync("Loading state...");
        Directory.CreateDirectory("channels");
        var files = Directory.GetFiles("channels", "*.state.json", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var tokens = file.Split("\\./".ToCharArray());
            if (tokens.Length == 5 && tokens[3] == "state")
            {
                ulong channelId = Convert.ToUInt64(tokens[1]);
                await Console.Out.WriteLineAsync($"Loading state for channel {channelId}");
                await using var stream = File.OpenRead(file);
                var channelState = await ReadAsync(channelId, stream);

                channelState.Chat ??= new(_openAILogic, _defaultParameters);
                channelState.Chat.State ??= new() { PrimeDirectives = PrimeDirective.ToList() };
                channelState.Chat.OpenAILogic = _openAILogic;
            }
        }
    }

    private async Task SaveState()
    {
        Directory.CreateDirectory("channels");
        foreach (var channelId in _channelBots.Keys)
        {
            await SaveCachedChannelState(channelId);
        }
    }

    private async Task SaveCachedChannelState(ulong channelId)
    {
        Directory.CreateDirectory($"channels/{channelId}");

        await using var stream = File.Create($"channels/{channelId}/{channelId}.state.json");
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

            _channelBots[channelId] = channelState;
        }
    }

    // Method to read state from a Stream in JSON format
    private async Task<ChannelState> ReadAsync(ulong channelId, Stream stream)
    {
        try
        {
            var str = await new StreamReader(stream).ReadToEndAsync();
            // Deserialize channelState from stream
            var channelState = JsonSerializer.Deserialize<ChannelState>(str);

            if (channelState != null)
            {
                _channelBots[channelId] = channelState;
            }

            return channelState;
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync(ex.ToString());
            return null;
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

    private async Task HandleMessageReceivedAsync(SocketMessage message)
    {
        if (message == null || message.Author.Id == _client.CurrentUser.Id)
            return;

        // Handle the received message here
        // ...
        var channel = _channelBots.GetOrAdd(message.Channel.Id, InitializeChannel);
       
        if (channel.Chat.State.PrimeDirectives.Count != _defaultPrimeDirective.Count || channel.Chat.State.PrimeDirectives[0].Content != _defaultPrimeDirective[0].Content)
        {
            channel.Chat.State.PrimeDirectives = PrimeDirective.ToList();
        }

        if (!channel.Options.Enabled)
            return;

        if (message.Content.StartsWith("!ignore"))
            return;

        // Add this message as a chat log
        channel.Chat.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.User, $"<{message.Author.Username}> {message.Content}"));

        if (!channel.Options.Muted)
        {
            using var typingState = message.Channel.EnterTypingState();
            // Get the response from the bot
            var responses = channel.Chat.GetResponseAsync();
            // Add the response as a chat 
            
            var sb = new StringBuilder();
            // Send the response to the channel
            await foreach (var response in responses)
            {
                if (response.Successful)
                {
                    var content = response.Choices.FirstOrDefault()?.Message.Content;
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

                channel.Chat.AddMessage(responseMessage);
                // Convert message to SocketMessage

                if (message is IUserMessage userMessage)
                {
                    _ = await userMessage.ReplyAsync(responseMessage.Content, components: BuildStandardComponents());
                }
                else
                {
                    _ = await message.Channel.SendMessageAsync(responseMessage.Content, components: BuildStandardComponents());
                }
            }
        }

        await SaveCachedChannelState(message.Channel.Id);
    }

    private MessageComponent BuildStandardComponents()
    {
        // pushpin unicode escape: \U0001F4CC
        var builder = new ComponentBuilder()
            .WithButton("📌Instruct", "instruction");
        return builder.Build();
    }

    private readonly List<ChatMessage> _defaultPrimeDirective = new(1)
    {
        new
        (StaticValues.ChatMessageRoles.System,
            "This is the Prime Directive: This is a chat bot running in [GPT-CLI](https://github.com/kainazzzo/GPT-CLI). Answer questions and" +
            " provide responses in Discord message formatting. Encourage users to add instructions with /gptcli or by using the :up_arrow:" +
            " emoji reaction on any message. Instructions are like 'sticky' chat messages that provide upfront context to the bot. The the 📌 emoji reaction is for pinning a message to instructions. The 🔄 emoji reaction is for replaying a message as a new prompt."),
        new ChatMessage(StaticValues.ChatMessageRoles.Assistant,
            "Got it. My name is GPT-CLI. I'm a chat bot running on discord that uses [GPT-CLI](https://github.com/kainazzzo/GPT-CLI). I'm still learning, so please be patient with me. I'm also still in development, so please report any bugs you find. You can find the source code on github here: https://github.com/kainazzzo/GPT-CLI")
    };

    private IEnumerable<ChatMessage> PrimeDirective => _defaultPrimeDirective;

    private ChannelState InitializeChannel(ulong channelId)
    {
        ChannelState channel = new()
        {
            Chat = new(_openAILogic, Clone(_defaultParameters))
            {
                State = new()
                {
                    PrimeDirectives = PrimeDirective.ToList(),
                    Parameters = Clone(_defaultParameters)
                }
            },
            Options = new(),
        };
        
        _channelBots[channelId] = channel;

        return channel;
    }

    private async Task HandleInteractionAsync(SocketInteraction arg)
    {
        if (arg is SocketSlashCommand { CommandName: "gptcli" } socketSlashCommand)
        {
            await HandleGptCliCommand(socketSlashCommand);
        }
        else if (arg is SocketMessageComponent command)
        {
            switch (command.Data.CustomId)
            {
                case "instruction":
                    await HandleInstructionCommand(command);
                    break;
            }

            await Console.Out.WriteLineAsync(
                $"Interaction: {arg.Type} {arg.Id} {arg} {arg.Channel.Id} {arg.Channel.Name} {arg.User.Id} {arg.User.Username}");
        }
    }

    private async Task HandleInstructionCommand(SocketMessageComponent command)
    {
        var channelState = _channelBots.GetOrAdd(command.Channel.Id, InitializeChannel);
        if (channelState.Options.Enabled)
        {
            var chatBot = channelState.Chat;
            chatBot.AddInstruction(new ChatMessage(StaticValues.ChatMessageRoles.User, command.Message.Content));


            using var typingState = command.Channel.EnterTypingState();
            await command.RespondAsync("Instruction added");
        }
        
    }

    private async Task HandleGptCliCommand(SocketSlashCommand command)
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
                        chatBot.Chat.ClearMessages();
                    }
                    else if (clearOptionValue == "instructions")
                    {
                        chatBot.Chat.ClearInstructions();
                    }
                    else if (clearOptionValue == "all")
                    {
                        chatBot.Chat.ClearMessages();
                        chatBot.Chat.ClearInstructions();
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
                        chatBot.Chat.AddInstruction(new(StaticValues.ChatMessageRoles.System, instruction));

                        try
                        {
                            await command.RespondAsync("Instruction received!");
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
                        await command.RespondAsync($"Instructions: {chatBot.Chat.InstructionStr}");
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

        await SaveCachedChannelState(channel.Id);
    }
}