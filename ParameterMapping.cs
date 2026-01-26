using System.Text.Json;
using GPT.CLI.Embeddings;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;

namespace GPT.CLI;

public static class ParameterMapping
{
    public static async Task<ChatCompletionCreateRequest> MapCommon(GptOptions parameters, OpenAILogic openAILogic, ChatCompletionCreateRequest request, Mode mode)
    {
        // It only makes sense to look for embeddings when in completion mode and when a prompt is provided
        if (mode == Mode.Completion && parameters.Prompt != null)
        {
            // Read the embeddings from the files and directories (empty list is returned if none are provided)
            var documents = await Program.ReadEmbedFilesAsync(parameters);
            documents.AddRange(await Program.ReadEmbedDirectoriesAsync(parameters));

            // If there were embeddings supplied either as files or directories:
            if (documents.Count > 0)
            {
                // Search for the closest few documents and add those if they aren't used yet
                var closestDocuments =
                    Document.FindMostSimilarDocuments(documents,
                        await openAILogic.GetEmbeddingForPrompt(parameters.Prompt), parameters.ClosestMatchLimit);

                // Add any closest documents to the request
                if (closestDocuments != null)
                {
                    foreach (var closestDocument in closestDocuments)
                    {
                        request.Messages.Add(new(StaticValues.ChatMessageRoles.User,
                            $"Context for the next message: {closestDocument.Document.Text}"));
                    }
                }
            }
        }

        // Map the common parameters to the request
        request.Model = parameters.Model;
        SetModelTokenParameters(request, parameters, parameters.Model);
        request.N = parameters.N;
        request.Temperature = (float?)parameters.Temperature;
        request.TopP = (float?)parameters.TopP;
        request.Stream = parameters.Stream;
        request.Stop = parameters.Stop;
        request.PresencePenalty = (float?)parameters.PresencePenalty;
        request.FrequencyPenalty = (float?)parameters.FrequencyPenalty;
        request.LogitBias = parameters.LogitBias == null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, double>>(parameters.LogitBias);
        request.User = parameters.User;

        return request;
    }

    private static void SetModelTokenParameters(ChatCompletionCreateRequest request, GptOptions parameters, string modelName)
    {
        request.MaxTokens = null;
        request.MaxCompletionTokens = null;

        if (!parameters.MaxTokens.HasValue)
        {
            return;
        }

        if (RequiresCompletionTokens(modelName))
        {
        request.MaxCompletionTokens = parameters.MaxTokens;
        }
        else
        {
            request.MaxTokens = parameters.MaxTokens;
        }
    }

    private static bool RequiresCompletionTokens(string modelName)
    {
        return modelName?.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public static async Task<ChatCompletionCreateRequest> MapChatEdit(GptOptions parameters, OpenAILogic openAILogic, Stream inputStream)
    {
        using var streamReader = new StreamReader(inputStream);
        var input = await streamReader.ReadToEndAsync();
        await inputStream.DisposeAsync();

        var request = await ParameterMapping.MapCommon(parameters, openAILogic, new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage>()
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You will receive two messages from the user. The first message will be text for you to parse and understand. The next message will be a prompt describing how you should proceed. You will read through the text or code in the first message, understand it, and then apply the prompt in the second message, with the first message as your main context. Your final message after the prompt should only be the result of the prompt applied to the input text with no preamble."),
                new(StaticValues.ChatMessageRoles.Assistant,
                    "Sure. I will read through the first message and understand it. Then I'll wait for another message containing the prompt. After I apply the prompt to the original text, my final response will be the result of applying the prompt to my understanding of the input text."),
                new(StaticValues.ChatMessageRoles.User, input),
                new(StaticValues.ChatMessageRoles.Assistant,
                    "Thank you. Now I will wait for the prompt and then apply it in context.")
            }
        }, Mode.Completion);

        // This is placed here so the MapCommon method can add contextual embeddings before the prompt
        request.Messages.Add(new(StaticValues.ChatMessageRoles.User, parameters.Prompt));

        return request;

    }

    public static async Task<ChatCompletionCreateRequest> MapChatCreate(GptOptions parameters,
        OpenAILogic openAILogic)
    {
        var request = await ParameterMapping.MapCommon(parameters, openAILogic,new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage>()
        }, Mode.Completion);

        request.Messages.Add(new(StaticValues.ChatMessageRoles.System, parameters.Prompt));

        return request;
    }

    public enum Mode
    {
        Completion,
        Chat,
        Embed,
        Http,
        Discord
    }
}
