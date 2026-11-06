using System.Net;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Rewrite;
using FishRelief.Kernel.Transport;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiFinishReason = Microsoft.Extensions.AI.ChatFinishReason;

namespace FishRelief.Tests;

/// <summary>
/// 上游传输层（路线 A）：请求体序列化（raw 优先 / max_tokens / tools schema）、
/// 响应解析（SDK 强类型 + RawRepresentation 透传）、SSE 流式解析、上游错误透传。
/// HTTP 用假 handler，不发真网络请求。
/// </summary>
public sealed class UpstreamChatClientTests
{
    [Fact]
    public async Task NonStream_SerializesRawHistoryAndCallsUpstream()
    {
        string? capturedBody = null;
        var client = Create(
            _ => CompletionJson("你好", finish: "stop"),
            body => capturedBody = body);
        var rawHistory = new MeaiChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-9", "CheckStock", new Dictionary<string, object?>())])
        {
            RawRepresentation = new AssistantChatMessage([
                ChatToolCall.CreateFunctionToolCall("call-9", "CheckStock", BinaryData.FromString("""{"fishName":"电子大肥鱼"}""")),
            ]),
        };
        var messages = new List<MeaiChatMessage>
        {
            new(ChatRole.System, "权威 system"),
            new(ChatRole.User, "权威 user"),
            rawHistory,
        };
        var options = new ChatOptions
        {
            ModelId = "demo-model",
            MaxOutputTokens = 2048,
            Tools = [new JsonFunctionDeclaration("CheckStock", "查库存", System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone())],
        };

        var response = await client.GetResponseAsync(messages, options);

        var body = capturedBody!;
        // raw 优先：历史 assistant 消息按 SDK 原对象序列化（arguments 作为 JSON 字符串原样嵌入，无字典往返）
        Assert.Contains("CheckStock", body);
        Assert.Contains(@"{\""fishName\"":\""电子大肥鱼\""}", body);
        Assert.DoesNotContain(@"""fishName"":""电子大肥鱼""", body);
        // 权威新建消息走手写映射
        Assert.Contains("\"content\":\"权威 system\"", body);
        Assert.Contains("\"model\":\"demo-model\"", body);
        // max_tokens 显式写法（非 max_completion_tokens）
        Assert.Contains("\"max_tokens\":2048", body);
        Assert.Contains("\"parameters\":{\"type\":\"object\"}", body);
        Assert.DoesNotContain("\"stream\":true", body);
        // 响应解析：标准字段 + raw
        Assert.Equal("你好", response.Text);
        Assert.Equal(MeaiFinishReason.Stop, response.FinishReason);
        Assert.IsType<ChatCompletion>(response.RawRepresentation);
    }

    [Fact]
    public async Task NonStream_OmittedToolParameters_IsAbsentFromRequestBody()
    {
        string? capturedBody = null;
        var client = Create(_ => CompletionJson("ok", finish: "stop"), body => capturedBody = body);
        var options = new ChatOptions
        {
            ModelId = "demo-model",
            Tools = [new JsonFunctionDeclaration("NoArg", "无参工具", null)],
        };

        await client.GetResponseAsync([new MeaiChatMessage(ChatRole.User, "hi")], options);

        var body = capturedBody!;
        Assert.Contains("\"name\":\"NoArg\"", body);
        Assert.DoesNotContain("\"parameters\"", body);
    }

    [Fact]
    public async Task Stream_ParsesSseWithRawUpdates()
    {
        const string sse = """
            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"he"},"finish_reason":null}]}

            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"llo"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

            data: [DONE]

            """;
        var client = Create(_ => sse, _ => { });

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new MeaiChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.Equal("he", updates[0].Text);
        Assert.Equal("llo", updates[1].Text);
        Assert.All(updates, u => Assert.IsType<StreamingChatCompletionUpdate>(u.RawRepresentation));
        Assert.Equal(MeaiFinishReason.Stop, updates[1].FinishReason);
        Assert.Equal(5, updates[1].Contents.OfType<UsageContent>().Single().Details.TotalTokenCount);
    }

    [Fact]
    public async Task UpstreamError_ThrowsWithStatusAndBody()
    {
        var client = Create(_ => "上游错误体", _ => { }, statusCode: HttpStatusCode.TooManyRequests);

        var exception = await Assert.ThrowsAsync<UpstreamException>(
            () => client.GetResponseAsync([new MeaiChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(429, exception.StatusCode);
        Assert.Equal("上游错误体", exception.Body);
    }

    [Fact]
    public async Task Stream_RequestsEventStreamAndSetsStreamTrue()
    {
        string? capturedBody = null;
        HttpRequestMessage? capturedRequest = null;
        var client = Create(
            _ => "data: [DONE]\n\n",
            body => capturedBody = body,
            onRequestHeaders: request => capturedRequest = request);

        await foreach (var _ in client.GetStreamingResponseAsync([new MeaiChatMessage(ChatRole.User, "hi")]))
        {
        }

        var body = capturedBody!;
        Assert.Contains("\"stream\":true", body);
        Assert.Equal("text/event-stream", capturedRequest!.Headers.Accept.ToString());
    }

    private static UpstreamChatClient Create(
        Func<HttpRequestMessage, string> responder,
        Action<string> onRequest,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Action<HttpRequestMessage>? onRequestHeaders = null)
    {
        var handler = new FakeHandler(responder, body => onRequest(body), statusCode, onRequestHeaders);
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifests", "demo", "manifest.json");
        _ = new FileSystemManifestRepository(manifestPath); // 确认清单可用（与链路其他测试保持一致）
        return new UpstreamChatClient(new HttpMessageInvoker(handler), new Uri("http://upstream.test/v1/chat/completions"), () => "sk-test");
    }

    private static string CompletionJson(string text, string finish) =>
        """
        {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"m",
         "choices":[{"index":0,"message":{"role":"assistant","content":"@text"},"finish_reason":"@finish"}],
         "usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
        """.Replace("@text", text).Replace("@finish", finish);

    private sealed class FakeHandler(
        Func<HttpRequestMessage, string> responder,
        Action<string> onRequest,
        HttpStatusCode statusCode,
        Action<HttpRequestMessage>? onRequestHeaders) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            onRequestHeaders?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responder(request), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
