using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text.Json;
using FishRelief.Kernel;
using FishRelief.Kernel.Admission;
using FishRelief.Kernel.Envelope;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Rewrite;
using FishRelief.Kernel.Transport;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace FishRelief.Gateway;

/// <summary>
/// /v1/chat/completions 端点处理：入站协议编解码（OpenAI JSON ↔ MEAI 类型）+
/// 内核中间件链调用 + 错误映射（OpenAI 错误体形状，响应侧行为像原生服务）。
/// 协议细节之外的一切业务都在内核（校验改写 / 准入 / 传输）。
/// </summary>
internal static class ChatCompletionsEndpoint
{
    public static async Task HandleAsync(
        HttpContext context, IChatClient chain, IManifestRepository repository, AuditLog audit)
    {
        var manifest = repository.Get().Manifest;

        var startedAt = Stopwatch.GetTimestamp();
        var outcome = "ok";
        bool stream = false;
        string? model = null;
        int? upstreamStatusCode = null;

        try
        {
            // 端点白名单 + 请求体上限（清单 limits），均为校验类错误 → 400
            if (!manifest.Endpoints.Contains("/v1/chat/completions", StringComparer.Ordinal))
            {
                outcome = "endpoint_not_allowed";
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, "invalid_request_error", "端点未在清单中开放");
                return;
            }

            context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = manifest.Limits.MaxBodyBytes;
            var contentLength = context.Request.ContentLength;
            if (contentLength is > 0 && contentLength > manifest.Limits.MaxBodyBytes)
            {
                outcome = "body_too_large";
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error",
                    $"请求体超过上限 {manifest.Limits.MaxBodyBytes} 字节");
                return;
            }

            Microsoft.Extensions.AI.ChatMessage[] incoming;
            IList<AITool> tools;
            try
            {
                var parsed = await InboundChatRequest.ReadAsync(context);
                stream = parsed.Stream;
                model = parsed.Model;
                incoming = [.. parsed.Messages];
                tools = parsed.Tools;
            }
            catch (Exception ex) when (ex is JsonException || IsClientValidationError(ex))
            {
                outcome = "invalid_request";
                var message = ex is JsonException json
                    ? $"请求体不是合法 JSON：{json.Message}"
                    : ex.Message;
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", message);
                return;
            }

            var options = new ChatOptions { ModelId = model, Tools = tools };

            try
            {
                if (stream)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "text/event-stream";
                    await foreach (var update in chain.GetStreamingResponseAsync(incoming, options, context.RequestAborted)
                                       .WithCancellation(context.RequestAborted))
                    {
                        // SSE 逐块重编码：优先取 SDK 原始增量序列化，字段保真上限 = OpenAI SDK 模型面
                        if (update.RawRepresentation is StreamingChatCompletionUpdate raw)
                        {
                            await context.Response.WriteAsync(
                                $"data: {ModelReaderWriter.Write(raw)}\n\n", context.RequestAborted);
                        }
                    }

                    await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
                }
                else
                {
                    var response = await chain.GetResponseAsync(incoming, options, context.RequestAborted);
                    if (response.RawRepresentation is not ChatCompletion raw)
                    {
                        throw new InvalidOperationException("上游响应缺少原始对象");
                    }

                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/json";
                    await context.Response.Body.WriteAsync(ModelReaderWriter.Write(raw), context.RequestAborted);
                }
            }
            catch (Exception ex) when (IsClientValidationError(ex))
            {
                outcome = "invalid_request";
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", ex.Message);
            }
            catch (AdmissionRejectedException ex)
            {
                outcome = "rate_limited";
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = JitteredRetryAfter(ex.RetryAfterSeconds).ToString("0");
                await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limit_exceeded", ex.Message);
            }
            catch (UpstreamException ex)
            {
                // 上游错误原样透传（状态码 + 响应体），行为上像原生服务
                outcome = "upstream_error";
                upstreamStatusCode = ex.StatusCode;
                context.Response.StatusCode = ex.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(ex.Body, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // 客户端断开：名额已在准入层 finally 释放，连接由 Kestrel 关闭
                outcome = "client_aborted";
            }
            catch (Exception ex)
            {
                outcome = "server_error";
                context.RequestServices.GetRequiredService<ILogger<Program>>()
                    .LogError(ex, "未处理异常（客户端 {RemoteIp}）", context.Connection.RemoteIpAddress);
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "server_error", "网关内部错误");
            }
        }
        finally
        {
            // 日志台账：一次请求一条，仅记录结果与结构化字段；写入失败不影响响应
            audit.Record(new AuditRecord(
                "request",
                context.Connection.RemoteIpAddress?.ToString(),
                model,
                stream,
                outcome,
                context.Response.StatusCode,
                upstreamStatusCode,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds));
        }
    }

    /// <summary>客户端可归责的错误（结构/上下文/上报键未命中词表），宿主映射为 400。</summary>
    internal static bool IsClientValidationError(Exception ex) =>
        ex is RequestValidationException or EnvelopeParseException or RuleKeyNotMatchedException;
    // PromptAssemblyException 不在此列：名单合法但模板组装失败是服务端清单问题，走 500

    /// <summary>Retry-After 抖动：基准值 × [0.8, 1.2]，delay-seconds 形式。</summary>
    private static double JitteredRetryAfter(int baseSeconds) =>
        baseSeconds * (0.8 + Random.Shared.NextDouble() * 0.4);

    /// <summary>按 OpenAI 错误体形状输出；调试端点复用。</summary>
    internal static async Task WriteErrorAsync(HttpContext context, int statusCode, string type, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                error = new
                {
                    message,
                    type,
                    param = (string?)null,
                    code = (string?)null,
                },
            }), context.RequestAborted);
    }
}
