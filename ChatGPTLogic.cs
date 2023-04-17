using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;

namespace GPT.CLI
{
    public class ChatGPTLogic
    {
        private readonly OpenAILogic _openAILogic;
        private readonly ChatCompletionCreateRequest _request;

        public ChatGPTLogic(OpenAILogic openAILogic, ChatCompletionCreateRequest request)
        {
            _openAILogic = openAILogic;
            _request = request;
        }

        public void AppendMessage(ChatMessage chatMessage)
        {
            _request.Messages.Add(chatMessage);
        }

        public IAsyncEnumerable<ChatCompletionCreateResponse> SendMessages()
        {
            return _openAILogic.CreateChatCompletionAsyncEnumerable(_request);
        }

        public void ClearMessages()
        {
            var message = _request.Messages.FirstOrDefault();
            if (message != null)
            {
                _request.Messages.Clear();
                _request.Messages.Add(message);
            }
        }
    }
}
