using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GPT.CLI.Embeddings;
using Microsoft.DeepDev;
using Betalgo.Ranul.OpenAI.Contracts.Enums;
using Betalgo.Ranul.OpenAI.Interfaces;
using Betalgo.Ranul.OpenAI.ObjectModels;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;

namespace GPT.CLI;

public class OpenAILogic
{
    private static readonly HttpClient ResponsesHttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IOpenAIService _openAIService;
    private readonly GptOptions _options;
    private readonly HttpClient _responsesHttpClient;

    public OpenAILogic(IOpenAIService openAIService, GptOptions options, HttpClient responsesHttpClient = null)
    {
        _openAIService = openAIService;
        _options = options;
        _responsesHttpClient = responsesHttpClient ?? ResponsesHttpClient;
    }

    public async Task<ChatCompletionCreateResponse> CreateChatCompletionAsync(ChatCompletionCreateRequest request)
    {
        ApplyModelCompatibility(request);
        if (RequiresResponsesForTools(request))
        {
            return await CreateViaResponsesAsync(request);
        }

        return await _openAIService.ChatCompletion.CreateCompletion(request);
    }

    public async IAsyncEnumerable<ChatCompletionCreateResponse> CreateChatCompletionAsyncEnumerable(ChatCompletionCreateRequest request)
    {
        ApplyModelCompatibility(request);
        if (RequiresResponsesForTools(request))
        {
            yield return await CreateViaResponsesAsync(request);
            yield break;
        }

        await foreach (var response in _openAIService.ChatCompletion.CreateCompletionAsStream(request))
        {
            yield return response;
        }
    }

    public async Task<EmbeddingCreateResponse> CreateEmbedding(EmbeddingCreateRequest request)
    {
        return await _openAIService.Embeddings.CreateEmbedding(request);
    }

    public async Task<EmbeddingCreateResponse> CreateEmbeddings(List<Document> documents)
    {
        var embeddings = await _openAIService.Embeddings.CreateEmbedding(new EmbeddingCreateRequest()
        {
            Model = Models.TextEmbeddingAdaV2,
            InputAsList = documents.Select(d => d.Text).ToList()
        });

        for (int i = 0; i < embeddings.Data.Count; i++)
        {
            documents[i].Embedding = embeddings.Data[i].Embedding;
        }
        
        return embeddings;
    }

    public async Task<List<double>> GetEmbeddingForPrompt(string prompt)
    {
        return (await _openAIService.Embeddings.CreateEmbedding(new() { Input = prompt, Model = Models.TextEmbeddingAdaV2 }))
            .Data.First().Embedding;
    }

    public static async Task<int> CountTokensAsync(string prompt, string modelName)
    {
        var tokenizer = await TokenizerBuilder.CreateByModelNameAsync(modelName);
        var encoded = tokenizer.Encode(prompt, new List<string>());
        return encoded.Count;
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
    {
        return request?.Tools is { Count: > 0 } && IsGpt6Family(request.Model);
    }

    internal static bool IsGpt6Family(string model)
    {
        return !string.IsNullOrWhiteSpace(model) &&
               model.Trim().StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase);
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
            ["tools"] = ConvertTools(request.Tools),
            ["tool_choice"] = ConvertToolChoice(request.ToolChoice),
            ["parallel_tool_calls"] = request.ParallelToolCalls ?? false,
            ["max_output_tokens"] = ResolveResponsesMaxOutputTokens(request.MaxCompletionTokens),
            ["reasoning"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["effort"] = "low"
            }
        };

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
