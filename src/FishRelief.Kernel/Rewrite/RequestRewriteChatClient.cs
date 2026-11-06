using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace FishRelief.Kernel.Rewrite;

/// <summary>
/// 请求改写中间件：把改写委托给 <see cref="RequestRewriter"/>（校验 + 权威组装），
/// 随后向下游透传，流式与非流式共用同一段改写（请求侧正交于响应形态）。
/// </summary>
public sealed class RequestRewriteChatClient : DelegatingChatClient
{
    private readonly RequestRewriter _rewriter;

    /// <param name="innerClient">下游客户端（准入层 / 传输层）。</param>
    /// <param name="rewriter">请求改写服务（校验 + 权威组装）。</param>
    public RequestRewriteChatClient(IChatClient innerClient, RequestRewriter rewriter)
        : base(innerClient)
    {
        _rewriter = rewriter;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await _rewriter.RewriteAsync(messages, options);
        return await InnerClient.GetResponseAsync(result.Messages, result.Options, cancellationToken);
    }

    /// <summary>流式与非流式共用同一段校验改写（请求侧正交于响应形态），直接向下游透传流式。</summary>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await _rewriter.RewriteAsync(messages, options);
        await foreach (var update in InnerClient.GetStreamingResponseAsync(result.Messages, result.Options, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }
}
