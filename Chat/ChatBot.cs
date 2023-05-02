using System.Linq;
using System.Text.Json.Serialization;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;

namespace GPT.CLI.Chat;

public class ChatBot
{
    public record ChatState
    {
        [JsonPropertyName("parameters")]
        public GPTParameters Parameters { get; set; }

        [JsonPropertyName("messages")]
        public LinkedList<ChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("instructions")] 
        public List<ChatMessage> Instructions { get; set; } = new();

        [JsonPropertyName("message-length")]
        public uint MessageLength { get; set; }

        [JsonPropertyName("prime-directives")]
        public List<ChatMessage> PrimeDirectives { get; set; } = new() {new(StaticValues.ChatMessageRoles.System,
            "Your Prime Directive: This is a chat bot running in [GPT-CLI](https://github.com/kainazzzo/GPT-CLI). Provide the best answer possible.")};
    }

    [JsonIgnore] internal OpenAILogic OpenAILogic;

    [JsonPropertyName("state")]
    public ChatState State { get; set; } = new();
    

    public ChatBot(OpenAILogic openAILogic, GPTParameters gptParameters)
    {
        OpenAILogic = openAILogic;
        State.Parameters = gptParameters;
    }

    public ChatBot()
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
    public string InstructionStr => State.Instructions.Count > 0 ? string.Join("\n", State.Instructions.Select(x => x.Content)) : string.Empty;

    [JsonIgnore]
    public string PrimeDirectiveStr => State.PrimeDirectives.Count > 0 ? string.Join("\n", State.PrimeDirectives.Select(x => x.Content)) : string.Empty;


    public void AddMessage(ChatMessage message)
    {
        while (State.Messages.Count > 0 && State.MessageLength > State.Parameters.MaxChatHistoryLength)
        {
            var removed = State.Messages.First();
            var removedLength = removed.Content.Length;
            State.MessageLength -= (uint)removedLength;
            State.Messages.RemoveFirst();
            MessageRemoved?.Invoke(this, removed);
        }

        if (State.Messages.Count == 0)
        {
            State.MessageLength = 0u;
        }

        State.Messages.AddLast(message);
        State.MessageLength += (uint)message.Content.Length;
        MessageAdded?.Invoke(this, message);
    }

    public void AddInstruction(ChatMessage message)
    {
        InstructionAdded?.Invoke(this, message);
        State.Instructions.Add(message);
    }


    public async IAsyncEnumerable<ChatCompletionCreateResponse> GetResponseAsync()
    {
        //await Console.Out.WriteLineAsync(string.Join("\r\n\r\n",
        //    State.PrimeDirectives.Concat(State.Instructions).Concat(State.Messages).Select(s => s.Content)));

        // Consolidate prime directives into a single ChatMessage
        var primeDirectiveMessage = new ChatMessage(StaticValues.ChatMessageRoles.System, $"Prime Directive: {PrimeDirectiveStr}");

        // Consolidate State.Instructions into a single ChatMessage
        var instructionMessage = new ChatMessage(StaticValues.ChatMessageRoles.System, $"Instructions: {InstructionStr}");

        var messages = new List<ChatMessage> {primeDirectiveMessage, instructionMessage}.Concat(State.Messages).ToList();

        await foreach (var response in OpenAILogic.CreateChatCompletionAsyncEnumerable(
                           await ParameterMapping.MapCommon(
                               State.Parameters,
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
        InstructionRemoved?.Invoke(this, State.Instructions[index]);
        State.Instructions.RemoveAt(index);
    }

    public void ClearInstructions()
    {
        InstructionsCleared?.Invoke(this, EventArgs.Empty);
        State.Instructions.Clear();
    }

    public void ClearMessages()
    {
        MessagesCleared?.Invoke(this, EventArgs.Empty);
        State.Messages.Clear();
    }
}