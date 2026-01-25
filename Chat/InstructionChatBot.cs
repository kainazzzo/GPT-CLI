using System.Text.Json.Serialization;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.ResponseModels;

namespace GPT.CLI.Chat;

public class InstructionChatBot
{
    public record InstructionChatBotState
    {
        [JsonPropertyName("parameters")]
        public GptOptions Parameters { get; set; }

        [JsonPropertyName("messages")]
        public LinkedList<ChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("instructions")] 
        public List<ChatMessage> Instructions { get; set; } = new();

        [JsonPropertyName("message-length")]
        public uint MessageLength { get; set; }

        [JsonPropertyName("prime-directives")]
        public List<ChatMessage> PrimeDirectives { get; set; } = new() {new(StaticValues.ChatMessageRoles.System,
            "Your Prime Directive: This is a chat bot running in [GPT-CLI](https://github.com/kainazzzo/GPT-CLI). Analyze and understand these instructions and apply them strictly to the response message:")};

        [JsonPropertyName("response-mode")]
        public ResponseMode ResponseMode { get; set; }

        [JsonPropertyName("embed-mode")]
        public EmbedMode EmbedMode { get; set; }
    }

    public enum ResponseMode
    {
        All,
        Matches
    }

    public enum EmbedMode
    {
        Explicit,
        All
    }

    [JsonIgnore] internal OpenAILogic OpenAILogic;

    [JsonPropertyName("state")]
    public InstructionChatBotState ChatBotState { get; set; } = new();
    

    public InstructionChatBot(OpenAILogic openAILogic, GptOptions gptParameters)
    {
        OpenAILogic = openAILogic;
        ChatBotState.Parameters = gptParameters;
    }

    public InstructionChatBot()
    {

    }

    // Event that fires when a message is added to the chat
    public event EventHandler<ChatMessage> MessageAdded;
    public event EventHandler<ChatMessage> MessageRemoved;
    public event EventHandler<ChatMessage> InstructionAdded;
    public event EventHandler<ChatMessage> InstructionRemoved;
    public event EventHandler InstructionsCleared;
    public event EventHandler MessagesCleared;



    [JsonIgnore]
    public string InstructionStr => ChatBotState.Instructions.Count > 0 ? string.Join("\n", ChatBotState.Instructions.Select(x => x.Content)) : string.Empty;

    [JsonIgnore]
    public string PrimeDirectiveStr => ChatBotState.PrimeDirectives.Count > 0 ? string.Join("\n", ChatBotState.PrimeDirectives.Select(x => x.Content)) : string.Empty;


    public void AddMessage(ChatMessage message)
    {
        while (ChatBotState.Messages.Count > 0 && ChatBotState.MessageLength > ChatBotState.Parameters.MaxChatHistoryLength)
        {
            var removed = ChatBotState.Messages.First();
            var removedLength = removed.Content.Length;
            ChatBotState.MessageLength -= (uint)removedLength;
            ChatBotState.Messages.RemoveFirst();
            MessageRemoved?.Invoke(this, removed);
        }

        if (ChatBotState.Messages.Count == 0)
        {
            ChatBotState.MessageLength = 0u;
        }

        ChatBotState.Messages.AddLast(message);
        ChatBotState.MessageLength += (uint)message.Content.Length;
        MessageAdded?.Invoke(this, message);
    }

    public void AddInstruction(ChatMessage message)
    {
        InstructionAdded?.Invoke(this, message);
        ChatBotState.Instructions.Add(message);
    }


    public async IAsyncEnumerable<ChatCompletionCreateResponse> GetResponseAsync()
    {
        //await Console.Out.WriteLineAsync(string.Join("\r\n\r\n",
        //    ChatBotState.PrimeDirectives.Concat(ChatBotState.Instructions).Concat(ChatBotState.Messages).Select(s => s.Content)));

        // Consolidate prime directives into a single ChatMessage
        var primeDirectiveMessage = new ChatMessage(StaticValues.ChatMessageRoles.System, $"Prime Directive: {PrimeDirectiveStr}");

        // Consolidate ChatBotState.Instructions into a single ChatMessage
        var instructionMessage = new ChatMessage(StaticValues.ChatMessageRoles.System, $"Instructions: {InstructionStr}");

        var messages = new List<ChatMessage> {primeDirectiveMessage, instructionMessage}.Concat(ChatBotState.Messages).ToList();

        await foreach (var response in OpenAILogic.CreateChatCompletionAsyncEnumerable(
                           await ParameterMapping.MapCommon(
                               ChatBotState.Parameters,
                               OpenAILogic, new ChatCompletionCreateRequest()
                               {
                                   Messages = messages
                               }, ParameterMapping.Mode.Discord)))
        {
            yield return response;
        }
    }

    public void RemoveInstruction(int index)
    {
        InstructionRemoved?.Invoke(this, ChatBotState.Instructions[index]);
        ChatBotState.Instructions.RemoveAt(index);
    }

    public void ClearInstructions()
    {
        InstructionsCleared?.Invoke(this, EventArgs.Empty);
        ChatBotState.Instructions.Clear();
    }

    public void ClearMessages()
    {
        MessagesCleared?.Invoke(this, EventArgs.Empty);
        ChatBotState.Messages.Clear();
    }
}