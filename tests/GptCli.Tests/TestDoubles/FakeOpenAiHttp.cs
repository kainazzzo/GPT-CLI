using System.Net;
using System.Text;
using System.Text.Json;

namespace GptCli.Tests.TestDoubles;

internal sealed class FakeOpenAiHttp : HttpMessageHandler
{
    public string ChatContent { get; set; } = "stub-reply";
    public List<double> Embedding { get; set; } = new() { 1.0, 0.0 };
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public string ResponseBody { get; set; }
    public Exception ThrowOnSend { get; set; }
    public HttpRequestMessage LastRequest { get; private set; }
    public string LastBody { get; private set; }
    public int SendCount { get; private set; }
    public int ChatCalls { get; private set; }
    public int EmbedCalls { get; private set; }

    public HttpClient CreateClient() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SendCount++;
        LastRequest = request;
        LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (ThrowOnSend != null)
        {
            throw ThrowOnSend;
        }

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            EmbedCalls++;
            return Json(StatusCode, ResponseBody ?? BuildEmbeddingBody());
        }

        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            ChatCalls++;
            if (IsStreamingRequest(LastBody))
            {
                return new HttpResponseMessage(StatusCode)
                {
                    Content = new StringContent(BuildChatSse(ChatContent), Encoding.UTF8, "text/event-stream")
                };
            }

            return Json(StatusCode, ResponseBody ?? BuildChatBody(ChatContent));
        }

        return Json(StatusCode, ResponseBody ?? "{}");
    }

    public int LastEmbedInputCount()
    {
        if (string.IsNullOrWhiteSpace(LastBody))
        {
            return 0;
        }

        using var doc = JsonDocument.Parse(LastBody);
        if (!doc.RootElement.TryGetProperty("input", out var input))
        {
            return 0;
        }

        return input.ValueKind == JsonValueKind.Array ? input.GetArrayLength() : 1;
    }

    private static bool IsStreamingRequest(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("stream", out var stream) &&
                   stream.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private string BuildEmbeddingBody()
    {
        var count = 1;
        if (!string.IsNullOrWhiteSpace(LastBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(LastBody);
                if (doc.RootElement.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
                {
                    count = Math.Max(1, input.GetArrayLength());
                }
            }
            catch
            {
                // Keep the default single-vector payload.
            }
        }

        var vector = JsonSerializer.Serialize(Embedding);
        var data = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                data.Append(',');
            }

            data.Append("{\"object\":\"embedding\",\"index\":").Append(i).Append(",\"embedding\":").Append(vector).Append('}');
        }

        return "{\"object\":\"list\",\"data\":[" + data + "],\"model\":\"text-embedding-ada-002\",\"usage\":{\"prompt_tokens\":1,\"total_tokens\":1}}";
    }

    private static string BuildChatBody(string content)
    {
        var encoded = JsonSerializer.Serialize(content ?? string.Empty);
        return $$"""
            {
              "id": "chat-1",
              "object": "chat.completion",
              "created": 1,
              "model": "gpt-4o",
              "choices": [
                {
                  "index": 0,
                  "message": { "role": "assistant", "content": {{encoded}} },
                  "finish_reason": "stop"
                }
              ]
            }
            """;
    }

    private static string BuildChatSse(string content)
    {
        var encoded = JsonSerializer.Serialize(content ?? string.Empty);
        return
            "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":" +
            encoded +
            "},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json"),
            ReasonPhrase = status.ToString()
        };
    }
}
