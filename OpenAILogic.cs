using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GPT.CLI.Embeddings;
using Microsoft.DeepDev;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using OpenAI.Responses;

namespace GPT.CLI;

public class OpenAILogic
{
    private const string DefaultEmbeddingModel = "text-embedding-ada-002";
    private static readonly HttpClient ResponsesHttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly GptOptions _options;
    private readonly HttpClient _responsesHttpClient;
    private OpenAIClient _openAIClient;

    public OpenAILogic(GptOptions options, HttpClient responsesHttpClient = null)
    {
        _options = options;
        _responsesHttpClient = responsesHttpClient ?? ResponsesHttpClient;
    }

    public async Task<ChatCompletionCreateResponse> CreateChatCompletionAsync(ChatCompletionCreateRequest request)
    {
        ApplyModelCompatibility(request);
        if (RequiresResponsesApi(request))
        {
            return await CreateViaResponsesAsync(request);
        }

        return await CreateViaChatCompletionsAsync(request);
    }

    public async IAsyncEnumerable<ChatCompletionCreateResponse> CreateChatCompletionAsyncEnumerable(ChatCompletionCreateRequest request)
    {
        ApplyModelCompatibility(request);
        if (RequiresResponsesApi(request))
        {
            yield return await CreateViaResponsesAsync(request);
            yield break;
        }

        await foreach (var response in CreateViaChatCompletionsStreamingAsync(request))
        {
            yield return response;
        }
    }

    /// <summary>
    /// Strict JSON via the official OpenAI SDK Responses API (<c>text.format</c> json_schema).
    /// </summary>
    public async Task<ChatCompletionCreateResponse> CreateStructuredJsonCompletionAsync(
        ChatCompletionCreateRequest request,
        string jsonSchemaName,
        BinaryData jsonSchema)
    {
        ApplyModelCompatibility(request);
        var apiKey = _options?.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return FailedResponse("Missing OpenAI API key.", HttpStatusCode.Unauthorized);
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Model))
        {
            return FailedResponse("Structured JSON request is missing a model.", HttpStatusCode.BadRequest);
        }

        if (string.IsNullOrWhiteSpace(jsonSchemaName) || jsonSchema == null || jsonSchema.ToMemory().Length == 0)
        {
            return FailedResponse("Structured JSON request is missing a schema.", HttpStatusCode.BadRequest);
        }

        var options = new CreateResponseOptions
        {
            Model = request.Model,
            MaxOutputTokenCount = ResolveResponsesMaxOutputTokens(request.MaxCompletionTokens),
            ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = ResponseReasoningEffortLevel.Low
            },
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    jsonSchemaName.Trim(),
                    jsonSchema,
                    jsonSchemaIsStrict: true)
            }
        };
        AddInputMessages(options, request.Messages);
        if (options.InputItems.Count == 0)
        {
            return FailedResponse("Responses request has no input messages.", HttpStatusCode.BadRequest);
        }

        try
        {
            var client = CreateResponsesClient(request.Model, apiKey);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            ResponseResult result = await client.CreateResponseAsync(options, timeoutCts.Token);
            return MapOfficialResponse(request.Model, result);
        }
        catch (ClientResultException ex)
        {
            var status = ex.Status > 0 ? (HttpStatusCode)ex.Status : HttpStatusCode.BadGateway;
            return FailedResponse(ex.Message, status);
        }
        catch (OperationCanceledException)
        {
            return FailedResponse("Timed out after 120s.", HttpStatusCode.RequestTimeout);
        }
        catch (Exception ex)
        {
            return FailedResponse($"{ex.GetType().Name}: {ex.Message}", HttpStatusCode.BadGateway);
        }
    }

    public async Task<EmbeddingCreateResponse> CreateEmbedding(EmbeddingCreateRequest request)
    {
        var inputs = new List<string>();
        if (request?.InputAsList is { Count: > 0 })
        {
            inputs.AddRange(request.InputAsList.Where(s => s != null));
        }
        else if (!string.IsNullOrEmpty(request?.Input))
        {
            inputs.Add(request.Input);
        }

        return await CreateEmbeddingsCoreAsync(request?.Model, inputs);
    }

    public async Task<EmbeddingCreateResponse> CreateEmbeddings(List<Document> documents)
    {
        var inputs = documents?.Select(d => d?.Text ?? string.Empty).ToList() ?? new List<string>();
        var embeddings = await CreateEmbeddingsCoreAsync(DefaultEmbeddingModel, inputs);
        if (embeddings.Successful && embeddings.Data != null)
        {
            for (var i = 0; i < embeddings.Data.Count && i < documents.Count; i++)
            {
                documents[i].Embedding = embeddings.Data[i].Embedding;
            }
        }

        return embeddings;
    }

    public async Task<List<double>> GetEmbeddingForPrompt(string prompt)
    {
        var embeddings = await CreateEmbeddingsCoreAsync(DefaultEmbeddingModel, new List<string> { prompt ?? string.Empty });
        return embeddings.Data?.FirstOrDefault()?.Embedding;
    }

    public static async Task<int> CountTokensAsync(string prompt, string modelName)
    {
        var tokenizer = await TokenizerBuilder.CreateByModelNameAsync(modelName);
        var encoded = tokenizer.Encode(prompt, new List<string>());
        return encoded.Count;
    }

    private async Task<ChatCompletionCreateResponse> CreateViaChatCompletionsAsync(ChatCompletionCreateRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Model))
        {
            return FailedResponse("Chat request is missing a model.", HttpStatusCode.BadRequest);
        }

        var messages = ConvertToOfficialMessages(request.Messages);
        if (messages.Count == 0)
        {
            return FailedResponse("Chat request has no input messages.", HttpStatusCode.BadRequest);
        }

        try
        {
            var client = GetClient().GetChatClient(request.Model);
            var options = ConvertToChatCompletionOptions(request);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await client.CompleteChatAsync(messages, options, timeoutCts.Token);
            return MapOfficialChatCompletion(request.Model, result.Value);
        }
        catch (ClientResultException ex)
        {
            var status = ex.Status > 0 ? (HttpStatusCode)ex.Status : HttpStatusCode.BadGateway;
            return FailedResponse(ex.Message, status);
        }
        catch (OperationCanceledException)
        {
            return FailedResponse("Timed out after 120s.", HttpStatusCode.RequestTimeout);
        }
        catch (Exception ex)
        {
            return FailedResponse($"{ex.GetType().Name}: {ex.Message}", HttpStatusCode.BadGateway);
        }
    }

    private async IAsyncEnumerable<ChatCompletionCreateResponse> CreateViaChatCompletionsStreamingAsync(ChatCompletionCreateRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Model))
        {
            yield return FailedResponse("Chat request is missing a model.", HttpStatusCode.BadRequest);
            yield break;
        }

        var messages = ConvertToOfficialMessages(request.Messages);
        if (messages.Count == 0)
        {
            yield return FailedResponse("Chat request has no input messages.", HttpStatusCode.BadRequest);
            yield break;
        }

        IAsyncEnumerable<StreamingChatCompletionUpdate> stream = null;
        ChatCompletionCreateResponse startError = null;
        try
        {
            var client = GetClient().GetChatClient(request.Model);
            var options = ConvertToChatCompletionOptions(request);
            stream = client.CompleteChatStreamingAsync(messages, options);
        }
        catch (ClientResultException ex)
        {
            var status = ex.Status > 0 ? (HttpStatusCode)ex.Status : HttpStatusCode.BadGateway;
            startError = FailedResponse(ex.Message, status);
        }
        catch (Exception ex)
        {
            startError = FailedResponse($"{ex.GetType().Name}: {ex.Message}", HttpStatusCode.BadGateway);
        }

        if (startError != null)
        {
            yield return startError;
            yield break;
        }

        var yielded = false;
        await foreach (var update in stream)
        {
            yielded = true;
            yield return MapOfficialStreamingUpdate(request.Model, update);
        }

        if (!yielded)
        {
            yield return FailedResponse("Empty streamed response from OpenAI Chat Completions.", HttpStatusCode.BadGateway);
        }
    }

    private async Task<EmbeddingCreateResponse> CreateEmbeddingsCoreAsync(string model, List<string> inputs)
    {
        if (inputs == null || inputs.Count == 0)
        {
            return new EmbeddingCreateResponse
            {
                HttpStatusCode = HttpStatusCode.OK,
                Data = new List<EmbeddingResponse>()
            };
        }

        try
        {
            var client = GetClient().GetEmbeddingClient(string.IsNullOrWhiteSpace(model) ? DefaultEmbeddingModel : model);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await client.GenerateEmbeddingsAsync(inputs, cancellationToken: timeoutCts.Token);
            var data = new List<EmbeddingResponse>();
            foreach (var item in result.Value)
            {
                var vector = item.ToFloats();
                data.Add(new EmbeddingResponse
                {
                    Index = item.Index,
                    Embedding = vector.ToArray().Select(v => (double)v).ToList()
                });
            }

            return new EmbeddingCreateResponse
            {
                HttpStatusCode = HttpStatusCode.OK,
                Data = data
            };
        }
        catch (ClientResultException ex)
        {
            var status = ex.Status > 0 ? (HttpStatusCode)ex.Status : HttpStatusCode.BadGateway;
            return new EmbeddingCreateResponse
            {
                HttpStatusCode = status,
                Error = new Error
                {
                    Code = ((int)status).ToString(),
                    Type = "api_error",
                    MessageObject = ex.Message
                }
            };
        }
        catch (OperationCanceledException)
        {
            return new EmbeddingCreateResponse
            {
                HttpStatusCode = HttpStatusCode.RequestTimeout,
                Error = new Error
                {
                    Code = "408",
                    Type = "api_error",
                    MessageObject = "Timed out after 120s."
                }
            };
        }
        catch (Exception ex)
        {
            return new EmbeddingCreateResponse
            {
                HttpStatusCode = HttpStatusCode.BadGateway,
                Error = new Error
                {
                    Code = "502",
                    Type = "api_error",
                    MessageObject = $"{ex.GetType().Name}: {ex.Message}"
                }
            };
        }
    }

    private OpenAIClient GetClient()
    {
        if (_openAIClient != null)
        {
            return _openAIClient;
        }

        var apiKey = string.IsNullOrWhiteSpace(_options?.ApiKey) ? "sk-missing" : _options.ApiKey.Trim();
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(ResolveOpenAiV1Endpoint(_options?.BaseDomain)),
            Transport = new HttpClientPipelineTransport(_responsesHttpClient),
            NetworkTimeout = TimeSpan.FromSeconds(120)
        };
        _openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        return _openAIClient;
    }

    internal static List<OpenAI.Chat.ChatMessage> ConvertToOfficialMessages(IList<ChatMessage> messages)
    {
        var result = new List<OpenAI.Chat.ChatMessage>();
        if (messages == null)
        {
            return result;
        }

        foreach (var message in messages)
        {
            if (message == null)
            {
                continue;
            }

            var role = NormalizeRole(message.Role);
            if (role == null)
            {
                continue;
            }

            var parts = ConvertToOfficialContentParts(message);
            if (parts.Count == 0 && !string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                continue;
            }

            if (parts.Count == 0)
            {
                parts.Add(ChatMessageContentPart.CreateTextPart(string.Empty));
            }

            OpenAI.Chat.ChatMessage official = role switch
            {
                "system" => new SystemChatMessage(parts),
                "developer" => new DeveloperChatMessage(parts),
                "assistant" => new AssistantChatMessage(parts),
                "user" => new UserChatMessage(parts),
                _ => null
            };
            if (official != null)
            {
                result.Add(official);
            }
        }

        return result;
    }

    private static List<ChatMessageContentPart> ConvertToOfficialContentParts(ChatMessage message)
    {
        var parts = new List<ChatMessageContentPart>();
        if (message.Contents is { Count: > 0 })
        {
            foreach (var part in message.Contents)
            {
                if (part == null)
                {
                    continue;
                }

                if (string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(part.Text))
                {
                    parts.Add(ChatMessageContentPart.CreateTextPart(part.Text));
                    continue;
                }

                if (part.ImageBytes is { Length: > 0 })
                {
                    var mime = string.IsNullOrWhiteSpace(part.MediaType) ? "image/png" : part.MediaType;
                    var detail = ParseImageDetail(part.ImageUrl?.Detail);
                    parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(part.ImageBytes), mime, detail));
                    continue;
                }

                if (TryParseDataUrl(part.ImageUrl?.Url, out var bytes, out var mediaType))
                {
                    parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(bytes), mediaType, ParseImageDetail(part.ImageUrl?.Detail)));
                }
            }

            return parts;
        }

        var text = ExtractMessageText(message);
        if (!string.IsNullOrEmpty(text))
        {
            parts.Add(ChatMessageContentPart.CreateTextPart(text));
        }

        return parts;
    }

    private static ChatImageDetailLevel? ParseImageDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return ChatImageDetailLevel.Auto;
        }

        return detail.Trim().ToLowerInvariant() switch
        {
            "low" => ChatImageDetailLevel.Low,
            "high" => ChatImageDetailLevel.High,
            _ => ChatImageDetailLevel.Auto
        };
    }

    private static bool TryParseDataUrl(string url, out byte[] bytes, out string mediaType)
    {
        bytes = null;
        mediaType = "image/png";
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = url.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        var header = url[5..comma];
        var payload = url[(comma + 1)..];
        var slashEnd = header.IndexOf(';');
        if (slashEnd > 0)
        {
            mediaType = header[..slashEnd];
        }

        try
        {
            bytes = Convert.FromBase64String(payload);
            return bytes is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    internal static ChatCompletionOptions ConvertToChatCompletionOptions(ChatCompletionCreateRequest request)
    {
        var options = new ChatCompletionOptions();
        if (request == null)
        {
            return options;
        }

        if (request.MaxCompletionTokens.HasValue)
        {
            options.MaxOutputTokenCount = request.MaxCompletionTokens.Value;
        }
        else if (request.MaxTokens.HasValue)
        {
            options.MaxOutputTokenCount = request.MaxTokens.Value;
        }

        if (request.Temperature.HasValue)
        {
            options.Temperature = request.Temperature.Value;
        }

        if (request.TopP.HasValue)
        {
            options.TopP = request.TopP.Value;
        }

        if (request.PresencePenalty.HasValue)
        {
            options.PresencePenalty = request.PresencePenalty.Value;
        }

        if (request.FrequencyPenalty.HasValue)
        {
            options.FrequencyPenalty = request.FrequencyPenalty.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.User))
        {
            options.EndUserId = request.User;
        }

        if (!string.IsNullOrWhiteSpace(request.Stop))
        {
            options.StopSequences.Add(request.Stop);
        }

        if (request.LogProbs.HasValue)
        {
            options.IncludeLogProbabilities = request.LogProbs.Value;
        }

        if (request.TopLogprobs.HasValue)
        {
            options.TopLogProbabilityCount = request.TopLogprobs.Value;
        }

        if (request.LogitBias != null)
        {
            foreach (var pair in request.LogitBias)
            {
                if (int.TryParse(pair.Key, out var token) && pair.Value is >= int.MinValue and <= int.MaxValue)
                {
                    options.LogitBiases[token] = (int)Math.Round(pair.Value);
                }
            }
        }

        if (request.ParallelToolCalls.HasValue)
        {
            options.AllowParallelToolCalls = request.ParallelToolCalls.Value;
        }

        foreach (var tool in ConvertToOfficialTools(request.Tools))
        {
            options.Tools.Add(tool);
        }

        if (options.Tools.Count > 0)
        {
            options.ToolChoice = ConvertToOfficialToolChoice(request.ToolChoice);
        }

        return options;
    }

    internal static List<ChatTool> ConvertToOfficialTools(IList<ToolDefinition> tools)
    {
        var result = new List<ChatTool>();
        if (tools == null)
        {
            return result;
        }

        foreach (var tool in tools)
        {
            var fn = tool?.Function;
            if (fn == null || string.IsNullOrWhiteSpace(fn.Name))
            {
                continue;
            }

            BinaryData parameters = null;
            if (fn.Parameters != null)
            {
                parameters = BinaryData.FromString(JsonSerializer.Serialize(fn.Parameters, JsonOptions));
            }

            result.Add(ChatTool.CreateFunctionTool(
                fn.Name,
                string.IsNullOrWhiteSpace(fn.Description) ? fn.Name : fn.Description,
                parameters ?? BinaryData.FromString("""{"type":"object","properties":{}}"""),
                fn.Strict));
        }

        return result;
    }

    internal static ChatToolChoice ConvertToOfficialToolChoice(ToolChoice toolChoice)
    {
        if (toolChoice == null)
        {
            return ChatToolChoice.CreateAutoChoice();
        }

        var type = toolChoice.Type?.ToString();
        if (string.Equals(type, "none", StringComparison.OrdinalIgnoreCase))
        {
            return ChatToolChoice.CreateNoneChoice();
        }

        if (string.Equals(type, "required", StringComparison.OrdinalIgnoreCase))
        {
            return ChatToolChoice.CreateRequiredChoice();
        }

        return ChatToolChoice.CreateAutoChoice();
    }

    internal static ChatCompletionCreateResponse MapOfficialChatCompletion(string model, ChatCompletion completion)
    {
        if (completion == null)
        {
            return FailedResponse("Empty response from OpenAI Chat Completions.", HttpStatusCode.BadGateway);
        }

        var text = ExtractOfficialChatText(completion.Content);
        var toolCalls = ExtractOfficialChatToolCalls(completion.ToolCalls);
        var message = new ChatMessage
        {
            Role = ChatCompletionRole.Assistant,
            Content = text,
            ToolCalls = toolCalls.Count == 0 ? null : toolCalls
        };
        if (toolCalls.Count > 0)
        {
            message.FunctionCall = toolCalls[0].FunctionCall;
        }

        return new ChatCompletionCreateResponse
        {
            Id = completion.Id,
            Model = string.IsNullOrWhiteSpace(completion.Model) ? model : completion.Model,
            ObjectTypeName = "chat.completion",
            HttpStatusCode = HttpStatusCode.OK,
            Choices = new List<ChatChoiceResponse>
            {
                new()
                {
                    Index = 0,
                    FinishReason = MapFinishReason(completion.FinishReason, toolCalls.Count > 0),
                    Message = message
                }
            }
        };
    }

    internal static ChatCompletionCreateResponse MapOfficialStreamingUpdate(string model, StreamingChatCompletionUpdate update)
    {
        var text = ExtractOfficialChatText(update?.ContentUpdate);
        var toolCalls = ExtractOfficialStreamingToolCalls(update?.ToolCallUpdates);
        return new ChatCompletionCreateResponse
        {
            Id = update?.CompletionId,
            Model = string.IsNullOrWhiteSpace(update?.Model) ? model : update.Model,
            ObjectTypeName = "chat.completion.chunk",
            HttpStatusCode = HttpStatusCode.OK,
            Choices = new List<ChatChoiceResponse>
            {
                new()
                {
                    Index = 0,
                    FinishReason = update?.FinishReason == null
                        ? null
                        : MapFinishReason(update.FinishReason.Value, toolCalls.Count > 0),
                    Message = new ChatMessage
                    {
                        Role = ChatCompletionRole.Assistant,
                        Content = text,
                        ToolCalls = toolCalls.Count == 0 ? null : toolCalls
                    }
                }
            }
        };
    }

    private static List<ToolCall> ExtractOfficialChatToolCalls(IReadOnlyList<ChatToolCall> toolCalls)
    {
        var result = new List<ToolCall>();
        if (toolCalls == null)
        {
            return result;
        }

        for (var i = 0; i < toolCalls.Count; i++)
        {
            var call = toolCalls[i];
            if (call == null || string.IsNullOrWhiteSpace(call.FunctionName))
            {
                continue;
            }

            result.Add(new ToolCall
            {
                Index = i,
                Id = call.Id,
                Type = "function",
                FunctionCall = new FunctionCall
                {
                    Name = call.FunctionName,
                    Arguments = call.FunctionArguments?.ToString() ?? "{}"
                }
            });
        }

        return result;
    }

    private static List<ToolCall> ExtractOfficialStreamingToolCalls(IReadOnlyList<StreamingChatToolCallUpdate> toolCalls)
    {
        var result = new List<ToolCall>();
        if (toolCalls == null)
        {
            return result;
        }

        for (var i = 0; i < toolCalls.Count; i++)
        {
            var call = toolCalls[i];
            if (call == null || string.IsNullOrWhiteSpace(call.FunctionName))
            {
                continue;
            }

            result.Add(new ToolCall
            {
                Index = i,
                Id = call.ToolCallId,
                Type = "function",
                FunctionCall = new FunctionCall
                {
                    Name = call.FunctionName,
                    Arguments = call.FunctionArgumentsUpdate?.ToString() ?? "{}"
                }
            });
        }

        return result;
    }

    private static string ExtractOfficialChatText(ChatMessageContent content)
    {
        if (content == null || content.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var part in content)
        {
            if (part == null || string.IsNullOrEmpty(part.Text))
            {
                continue;
            }

            sb.Append(part.Text);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string MapFinishReason(ChatFinishReason finishReason, bool hasToolCalls)
    {
        if (finishReason == ChatFinishReason.ToolCalls || hasToolCalls && finishReason == ChatFinishReason.Stop)
        {
            return "tool_calls";
        }

        if (finishReason == ChatFinishReason.Length)
        {
            return "length";
        }

        if (finishReason == ChatFinishReason.ContentFilter)
        {
            return "content_filter";
        }

        if (finishReason == ChatFinishReason.FunctionCall)
        {
            return "function_call";
        }

        return "stop";
    }

    internal static void ApplyModelCompatibility(ChatCompletionCreateRequest request)
    {
        if (request == null || !RejectsSamplingParameters(request.Model))
        {
            return;
        }

        request.Temperature = null;
        request.TopP = null;
        request.LogProbs = null;
        request.TopLogprobs = null;
        request.LogitBias = null;
        request.PresencePenalty = null;
        request.FrequencyPenalty = null;
    }

    internal static bool RequiresResponsesForTools(ChatCompletionCreateRequest request)
        => RequiresResponsesApi(request);

    internal static bool RequiresResponsesApi(ChatCompletionCreateRequest request)
    {
        if (request == null)
        {
            return false;
        }

        // gpt-5.6-sol rejects function tools (and is unreliable for JSON) on chat/completions.
        if (IsGpt56Family(request.Model))
        {
            return true;
        }

        return request.Tools is { Count: > 0 } && IsGpt6Family(request.Model);
    }

    internal static bool IsGpt6Family(string model)
    {
        return !string.IsNullOrWhiteSpace(model) &&
               model.Trim().StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsGpt56Family(string model)
    {
        return !string.IsNullOrWhiteSpace(model) &&
               model.Trim().StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Prefer the current default over persisted leftovers (gpt-5 / gpt-5.2 / gpt-6-astra, plus the misnamed gpt-6-sol).
    /// GPT-5.6 models (including gpt-5.6-sol) and other explicit current names are kept.
    /// </summary>
    public static string ResolveCurrentTextModel(string requested, string defaultModel)
    {
        var fallback = string.IsNullOrWhiteSpace(defaultModel) ? "gpt-5.6-sol" : defaultModel.Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return fallback;
        }

        var name = requested.Trim();
        if (IsStalePersistedTextModel(name))
        {
            return fallback;
        }

        return name;
    }

    internal static bool IsStalePersistedTextModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var name = model.Trim();
        if (string.Equals(name, "gpt-6-astra", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "gpt-6-sol", StringComparison.OrdinalIgnoreCase))
        {
            // gpt-6-sol was a leftover name for gpt-5.6-sol.
            return true;
        }

        if (!name.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Keep current GPT-5.6 models; remap older gpt-5 / gpt-5.2 leftovers.
        return !name.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool RejectsSamplingParameters(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var name = model.Trim();
        return name.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ChatCompletionCreateResponse> CreateViaResponsesAsync(ChatCompletionCreateRequest request)
    {
        var apiKey = _options?.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return FailedResponse("Missing OpenAI API key.", HttpStatusCode.Unauthorized);
        }

        var input = ConvertMessages(request.Messages);
        if (input.Count == 0)
        {
            return FailedResponse("Responses request has no input messages.", HttpStatusCode.BadRequest);
        }

        var body = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = request.Model,
            ["input"] = input,
            ["max_output_tokens"] = ResolveResponsesMaxOutputTokens(request.MaxCompletionTokens),
            ["reasoning"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["effort"] = "low"
            }
        };

        var tools = ConvertTools(request.Tools);
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = ConvertToolChoice(request.ToolChoice);
            body["parallel_tool_calls"] = request.ParallelToolCalls ?? false;
        }

        var endpoint = ResolveResponsesEndpoint(_options?.BaseDomain);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var resp = await _responsesHttpClient.SendAsync(req, timeoutCts.Token);
            var raw = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return FailedResponse(ExtractResponsesError(raw) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}".Trim(), resp.StatusCode);
            }

            if (!TryParseJsonElement(raw, out var root))
            {
                return FailedResponse("Invalid JSON response from OpenAI Responses API.", HttpStatusCode.BadGateway);
            }

            return MapResponsesToChatCompletion(request.Model, root);
        }
        catch (OperationCanceledException)
        {
            return FailedResponse("Timed out after 120s.", HttpStatusCode.RequestTimeout);
        }
        catch (Exception ex)
        {
            return FailedResponse($"{ex.GetType().Name}: {ex.Message}", HttpStatusCode.BadGateway);
        }
    }

    internal static int ResolveResponsesMaxOutputTokens(int? requested)
    {
        // GPT-6 Astra always reasons, and reasoning tokens count against max_output_tokens.
        var value = requested ?? 2048;
        return Math.Max(value, 2048);
    }

    internal static string ResolveResponsesEndpoint(string baseDomain)
    {
        if (string.IsNullOrWhiteSpace(baseDomain))
        {
            return "https://api.openai.com/v1/responses";
        }

        var trimmed = baseDomain.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{trimmed}/responses";
        }

        return $"{trimmed}/v1/responses";
    }

    internal static string ResolveOpenAiV1Endpoint(string baseDomain)
    {
        if (string.IsNullOrWhiteSpace(baseDomain))
        {
            return "https://api.openai.com/v1";
        }

        var trimmed = baseDomain.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^"/responses".Length];
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/v1";
    }

    private ResponsesClient CreateResponsesClient(string model, string apiKey)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(ResolveOpenAiV1Endpoint(_options?.BaseDomain)),
            Transport = new HttpClientPipelineTransport(_responsesHttpClient),
            NetworkTimeout = TimeSpan.FromSeconds(120)
        };
        return new ResponsesClient(model, new ApiKeyCredential(apiKey.Trim()), options);
    }

    private static void AddInputMessages(CreateResponseOptions options, IList<ChatMessage> messages)
    {
        if (options == null || messages == null)
        {
            return;
        }

        foreach (var message in messages)
        {
            if (message == null)
            {
                continue;
            }

            var role = NormalizeRole(message.Role);
            var text = ExtractMessageText(message);
            if (string.IsNullOrWhiteSpace(text) && !string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                continue;
            }

            text ??= string.Empty;
            ResponseItem item = role switch
            {
                "system" => ResponseItem.CreateSystemMessageItem(text),
                "developer" => ResponseItem.CreateDeveloperMessageItem(text),
                "assistant" => ResponseItem.CreateAssistantMessageItem(text),
                "user" => ResponseItem.CreateUserMessageItem(text),
                _ => null
            };
            if (item != null)
            {
                options.InputItems.Add(item);
            }
        }
    }

    internal static ChatCompletionCreateResponse MapOfficialResponse(string model, ResponseResult result)
    {
        if (result == null)
        {
            return FailedResponse("Empty response from OpenAI Responses API.", HttpStatusCode.BadGateway);
        }

        if (result.Error != null && !string.IsNullOrWhiteSpace(result.Error.Message))
        {
            return FailedResponse(result.Error.Message, HttpStatusCode.BadGateway);
        }

        var outputText = ExtractOfficialOutputText(result);
        return new ChatCompletionCreateResponse
        {
            Id = result.Id,
            Model = string.IsNullOrWhiteSpace(result.Model) ? model : result.Model,
            ObjectTypeName = "chat.completion",
            HttpStatusCode = HttpStatusCode.OK,
            Choices = new List<ChatChoiceResponse>
            {
                new()
                {
                    Index = 0,
                    FinishReason = "stop",
                    Message = new ChatMessage
                    {
                        Role = ChatCompletionRole.Assistant,
                        Content = outputText
                    }
                }
            }
        };
    }

    internal static string ExtractOfficialOutputText(ResponseResult result)
    {
        if (result?.OutputItems == null)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var item in result.OutputItems)
        {
            if (item is not MessageResponseItem message || message.Content == null)
            {
                continue;
            }

            foreach (var part in message.Content)
            {
                if (part == null || string.IsNullOrWhiteSpace(part.Text))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(part.Text.Trim());
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    internal static List<Dictionary<string, object>> ConvertMessages(IList<ChatMessage> messages)
    {
        var result = new List<Dictionary<string, object>>();
        if (messages == null)
        {
            return result;
        }

        foreach (var message in messages)
        {
            if (message == null)
            {
                continue;
            }

            var role = NormalizeRole(message.Role);
            if (role == null)
            {
                continue;
            }

            var text = ExtractMessageText(message);
            if (string.IsNullOrWhiteSpace(text) && !string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["role"] = role,
                ["content"] = text ?? string.Empty
            });
        }

        return result;
    }

    private static string NormalizeRole(ChatCompletionRole? role)
    {
        var value = role?.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "developer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "user", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return value.Trim().ToLowerInvariant();
        }

        return null;
    }

    private static string ExtractMessageText(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            return message.Content;
        }

        if (message.Contents is { Count: > 0 })
        {
            var sb = new StringBuilder();
            foreach (var part in message.Contents)
            {
                if (part != null &&
                    string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(part.Text))
                {
                    sb.Append(part.Text);
                }
            }

            if (sb.Length > 0)
            {
                return sb.ToString();
            }
        }

        return message.ContentCalculated as string;
    }

    internal static List<Dictionary<string, object>> ConvertTools(IList<ToolDefinition> tools)
    {
        var result = new List<Dictionary<string, object>>();
        if (tools == null)
        {
            return result;
        }

        foreach (var tool in tools)
        {
            var fn = tool?.Function;
            if (fn == null || string.IsNullOrWhiteSpace(fn.Name))
            {
                continue;
            }

            object parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            };
            if (fn.Parameters != null)
            {
                parameters = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(fn.Parameters, JsonOptions));
            }

            result.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "function",
                ["name"] = fn.Name,
                ["description"] = string.IsNullOrWhiteSpace(fn.Description) ? fn.Name : fn.Description,
                ["strict"] = fn.Strict ?? false,
                ["parameters"] = parameters
            });
        }

        return result;
    }

    internal static object ConvertToolChoice(ToolChoice toolChoice)
    {
        if (toolChoice == null)
        {
            return "auto";
        }

        var type = toolChoice.Type.ToString();
        if (string.IsNullOrWhiteSpace(type) || string.Equals(type, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        if (string.Equals(type, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "required", StringComparison.OrdinalIgnoreCase))
        {
            return type.Trim().ToLowerInvariant();
        }

        return "auto";
    }

    internal static ChatCompletionCreateResponse MapResponsesToChatCompletion(string model, JsonElement root)
    {
        var outputText = ExtractResponsesOutputText(root);
        var toolCalls = ExtractResponsesToolCalls(root);
        var message = new ChatMessage
        {
            Role = ChatCompletionRole.Assistant,
            Content = outputText,
            ToolCalls = toolCalls.Count == 0 ? null : toolCalls
        };
        if (toolCalls.Count > 0)
        {
            message.FunctionCall = toolCalls[0].FunctionCall;
        }

        var id = TryGetString(root, "id");
        return new ChatCompletionCreateResponse
        {
            Id = id,
            Model = TryGetString(root, "model") ?? model,
            ObjectTypeName = "chat.completion",
            HttpStatusCode = HttpStatusCode.OK,
            Choices = new List<ChatChoiceResponse>
            {
                new()
                {
                    Index = 0,
                    FinishReason = toolCalls.Count > 0 ? "tool_calls" : "stop",
                    Message = message
                }
            }
        };
    }

    internal static List<ToolCall> ExtractResponsesToolCalls(JsonElement root)
    {
        var calls = new List<ToolCall>();
        if (!TryGetPropertyIgnoreCase(root, "output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return calls;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(item, "type", out var typeEl) ||
                typeEl.ValueKind != JsonValueKind.String ||
                !string.Equals(typeEl.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = TryGetString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var callId = TryGetString(item, "call_id") ?? TryGetString(item, "id") ?? Guid.NewGuid().ToString("n");
            var arguments = "{}";
            if (TryGetPropertyIgnoreCase(item, "arguments", out var argsEl))
            {
                arguments = argsEl.ValueKind == JsonValueKind.String ? argsEl.GetString() : argsEl.GetRawText();
            }

            calls.Add(new ToolCall
            {
                Id = callId,
                Type = "function",
                FunctionCall = new FunctionCall
                {
                    Name = name.Trim(),
                    Arguments = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments
                }
            });
        }

        return calls;
    }

    internal static string ExtractResponsesOutputText(JsonElement root)
    {
        if (TryGetPropertyIgnoreCase(root, "output_text", out var outputTextEl) &&
            outputTextEl.ValueKind == JsonValueKind.String)
        {
            var direct = outputTextEl.GetString();
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct.Trim();
            }
        }

        if (!TryGetPropertyIgnoreCase(root, "output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(item, "type", out var typeEl) ||
                typeEl.ValueKind != JsonValueKind.String ||
                !string.Equals(typeEl.GetString(), "message", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetPropertyIgnoreCase(item, "content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (TryGetPropertyIgnoreCase(part, "text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }

                        sb.Append(text.Trim());
                    }
                }
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string ExtractResponsesError(string rawJson)
    {
        if (!TryParseJsonElement(rawJson, out var root))
        {
            return null;
        }

        if (TryGetPropertyIgnoreCase(root, "error", out var errorEl))
        {
            if (errorEl.ValueKind == JsonValueKind.String)
            {
                return errorEl.GetString();
            }

            if (TryGetPropertyIgnoreCase(errorEl, "message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
            {
                return messageEl.GetString();
            }
        }

        return null;
    }

    internal static ChatCompletionCreateResponse FailedResponse(string message, HttpStatusCode statusCode)
    {
        return new ChatCompletionCreateResponse
        {
            HttpStatusCode = statusCode,
            Error = new Error
            {
                Code = ((int)statusCode).ToString(),
                Type = "api_error",
                MessageObject = message
            }
        };
    }

    private static string TryGetString(JsonElement obj, string propertyName)
    {
        return TryGetPropertyIgnoreCase(obj, propertyName, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryParseJsonElement(string json, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
