using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
    }
}
