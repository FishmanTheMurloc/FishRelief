using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace FishRelief.Kernel.Transport;

/// <summary>
/// 上游传输：手写 HttpClient 转发的 IChatClient 实现（不使用 OpenAI SDK 的传输层）。
/// 请求体由 OpenAIRequestWriter 序列化（raw 优先 + max_tokens 显式写法），
/// 响应由 OpenAIResponseParser 解析（SDK 强类型 + RawRepresentation 透传）。
/// 由宿主注入 HttpMessageInvoker：整体无超时、禁自动重定向等策略在 HttpClient 构造处实施。
/// </summary>
public sealed class UpstreamChatClient : IChatClient
{
    private readonly HttpMessageInvoker _invoker;
    private readonly Uri _endpoint;
    private readonly Func<string> _apiKeyProvider;

    public UpstreamChatClient(HttpMessageInvoker invoker, Uri endpoint, Func<string> apiKeyProvider)
    {
        _invoker = invoker;
        _endpoint = endpoint;
        _apiKeyProvider = apiKeyProvider;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var incoming = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var body = OpenAIRequestWriter.Write(incoming, options, stream: false);

        using var response = await SendAsync(body, acceptEventStream: false, cancellationToken);
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpstreamException((int)response.StatusCode, System.Text.Encoding.UTF8.GetString(content));
        }

        return OpenAIResponseParser.ParseCompletion(content);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var incoming = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var body = OpenAIRequestWriter.Write(incoming, options, stream: true);

        using var response = await SendAsync(body, acceptEventStream: true, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            throw new UpstreamException((int)response.StatusCode, System.Text.Encoding.UTF8.GetString(content));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        async IAsyncEnumerable<OpenAI.Chat.StreamingChatCompletionUpdate> ParseUpdatesAsync(
            [EnumeratorCancellation] CancellationToken token)
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue; // 忽略注释行与空行；本实现不依赖 event: 字段
                }

                var payload = line["data:".Length..].Trim();
                if (payload == "[DONE]")
                {
                    break;
                }

                if (payload.Length == 0)
                {
                    continue;
                }

                yield return OpenAIResponseParser.ParseStreamingUpdate(System.Text.Encoding.UTF8.GetBytes(payload));
            }
        }

        await foreach (var update in OpenAIResponseParser.ConvertUpdatesAsync(ParseUpdatesAsync(cancellationToken))
                           .WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _invoker.Dispose();

    private async Task<HttpResponseMessage> SendAsync(BinaryData body, bool acceptEventStream, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(body.ToArray()),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKeyProvider()}");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        if (acceptEventStream)
        {
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        }

        return await _invoker.SendAsync(request, cancellationToken);
    }
}
