using System.Net;
using System.Text;

namespace GptCli.Tests.TestDoubles;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage LastRequest { get; private set; }
    public string LastBody { get; private set; }
    public int SendCount { get; private set; }
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public string ResponseBody { get; set; } = "{}";
    public Exception ThrowOnSend { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SendCount++;
        LastRequest = request;
        LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (ThrowOnSend != null)
        {
            throw ThrowOnSend;
        }

        return new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(ResponseBody ?? string.Empty, Encoding.UTF8, "application/json"),
            ReasonPhrase = StatusCode.ToString()
        };
    }
}
