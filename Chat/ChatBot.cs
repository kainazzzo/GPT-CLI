using System.Text.Json.Serialization;
using Discord;
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

    [JsonIgnore]
    public string InstructionStr => State.Instructions.Count > 0 ? string.Join("\n", State.Instructions.Select(x => x.Content)) : string.Empty;


    public void AddMessage(ChatMessage message)
    {
        while (State.Messages.Count > 0 && State.MessageLength > State.Parameters.MaxChatHistoryLength)
        {
            
            var removed = State.Messages.First();
            var removedLength = removed.Content.Length;
            State.MessageLength -= (uint)removedLength;
            State.Messages.RemoveFirst();
        }

        if (State.Messages.Count == 0)
        {
            State.MessageLength = 0u;
        }

        State.Messages.AddLast(message);
        State.MessageLength += (uint)message.Content.Length;
    }

    public void AddInstruction(ChatMessage message)
    {
        State.Instructions.Add(message);
    }


    public async IAsyncEnumerable<ChatCompletionCreateResponse> GetResponseAsync()
    {
        //await Console.Out.WriteLineAsync(string.Join("\r\n\r\n",
        //    State.PrimeDirectives.Concat(State.Instructions).Concat(State.Messages).Select(s => s.Content)));

        await foreach (var response in OpenAILogic.CreateChatCompletionAsyncEnumerable(
                           await ParameterMapping.MapCommon(
                               State.Parameters,
                               OpenAILogic, new ChatCompletionCreateRequest()
                               {
                                   Messages = State.PrimeDirectives.Concat(State.Instructions).Concat(State.Messages).ToList()
                               }, ParameterMapping.Mode.Discord)))
        {
            yield return response;
        }
    }

    public void ClearInstructions()
    {
        State.Instructions.Clear();
    }

    public void ClearMessages()
    {
        State.Messages.Clear();
    }
}