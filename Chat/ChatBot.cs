using System.Text.Json.Serialization;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;

namespace GPT.CLI.Chat;

public class ChatBot
{
    [JsonIgnore]
    private readonly OpenAILogic _openAILogic;

    [JsonPropertyName("parameters")]
    private readonly GPTParameters _gptParameters;

    [JsonPropertyName("messages")]
    private readonly LinkedList<ChatMessage> _messages = new();

    [JsonPropertyName("instructions")]
    private readonly List<ChatMessage> _instructions = new();

    [JsonPropertyName("messageLength")]
    private uint _messageLength;


    public ChatBot(OpenAILogic openAILogic, GPTParameters gptParameters)
    {
        _openAILogic = openAILogic;
        _gptParameters = gptParameters;
    }

    [JsonIgnore]
    public string Instructions => _instructions.Count > 0 ? string.Join("\n", _instructions.Select(x => x.Content)) : string.Empty;


    public async Task AddMessage(ChatMessage message)
    {
        await Console.Out.WriteAsync("Message Added. ");
        while (_messages.Count > 0 && _messageLength > _gptParameters.MaxChatHistoryLength)
        {
            await Console.Out.WriteAsync($"Removing message. Length: {_messageLength} > Max: {_gptParameters.MaxChatHistoryLength}. ");
            var removed = _messages.First();
            var removedLength = removed.Content.Length;
            _messageLength -= (uint)removedLength;
            _messages.RemoveFirst();
        }

        if (_messages.Count == 0)
        {
            _messageLength = 0u;
        }

        _messages.AddLast(message);
        _messageLength += (uint)message.Content.Length;
        await Console.Out.WriteLineAsync($"Message Length: {_messageLength}");
    }

    public void AddInstruction(ChatMessage message)
    {
        _instructions.Add(message);
    }

    public async IAsyncEnumerable<ChatCompletionCreateResponse> GetResponseAsync()
    {
        await foreach (var response in _openAILogic.CreateChatCompletionAsyncEnumerable(
                           await ParameterMapping.MapCommon(
                               _gptParameters,
                               _openAILogic, new ChatCompletionCreateRequest()
                               {
                                   Messages = _instructions.Concat(_messages).ToList()
                               }, ParameterMapping.Mode.Discord)))
        {
            yield return response;
        }
    }

    public void ClearInstructions()
    {
        _instructions.Clear();
    }

    public void ClearMessages()
    {
        _messages.Clear();
    }
}