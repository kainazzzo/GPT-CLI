using Discord.WebSocket;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;

namespace GPT.CLI.Chat;

public class ChatBot
{
    private readonly OpenAILogic _openAILogic;
    private readonly GPTParameters _gptParameters;
    private readonly LinkedList<ChatMessage> _messages = new();
    private readonly List<ChatMessage> _instructions = new();


    public ChatBot(OpenAILogic openAILogic, GPTParameters gptParameters)
    {
        _openAILogic = openAILogic;
        _gptParameters = gptParameters;
    }

    public string Instructions => _instructions.Count > 0 ? string.Join("\n", _instructions.Select(x => x.Content)) : string.Empty;


    public void AddMessage(ChatMessage message)
    {
        _messages.AddLast(message);
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