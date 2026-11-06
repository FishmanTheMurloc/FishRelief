using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FishRelief.Kernel;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Rewrite;
using FishRelief.Kernel.Transport;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;

namespace FishRelief.Gateway;

/// <summary>
/// 改写预览：输入与 /v1/chat/completions 同形（system 消息里是渲染信封），
/// 走与线上完全相同的解析与改写路径，返回覆写后的完整上游请求。
/// 不进入准入、不调用上游、不写台账。输出字段用项目中立词汇（task / keys / request）。
/// </summary>
internal static class DebugRewriteEndpoint
{
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task HandleAsync(HttpContext context, RequestRewriter rewriter, IManifestRepository repository)
    {
        try
        {
            var manifest = repository.Get().Manifest;
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = manifest.Limits.MaxBodyBytes;

            InboundChatRequest.Parsed parsed;
            try
            {
                parsed = await InboundChatRequest.ReadAsync(context);
            }
            catch (JsonException ex)
            {
                await ChatCompletionsEndpoint.WriteErrorAsync(
                    context, StatusCodes.Status400BadRequest, "invalid_request_error", $"请求体不是合法 JSON：{ex.Message}");
                return;
            }

            var result = await rewriter.RewriteAsync(
                parsed.Messages, new ChatOptions { ModelId = parsed.Model, Tools = parsed.Tools });
            var upstreamRequest = OpenAIRequestWriter.Write(result.Messages, result.Options, stream: false);

            var response = new JsonObject
            {
                ["task"] = result.TaskId,
                ["data"] = JsonNode.Parse(JsonSerializer.Serialize(result.Data)),
                ["request"] = JsonNode.Parse(upstreamRequest.ToString()),
            };

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response.ToJsonString(OutputOptions), context.RequestAborted);
        }
        catch (Exception ex) when (ChatCompletionsEndpoint.IsClientValidationError(ex))
        {
            await ChatCompletionsEndpoint.WriteErrorAsync(
                context, StatusCodes.Status400BadRequest, "invalid_request_error", ex.Message);
        }
        catch (PromptAssemblyException ex)
        {
            // 名单合法但模板组装失败属服务端清单问题
            await ChatCompletionsEndpoint.WriteErrorAsync(
                context, StatusCodes.Status500InternalServerError, "server_error", ex.Message);
        }
        catch (Exception ex)
        {
            context.RequestServices.GetRequiredService<ILogger<Program>>().LogError(ex, "调试改写失败");
            await ChatCompletionsEndpoint.WriteErrorAsync(
                context, StatusCodes.Status500InternalServerError, "server_error", "网关内部错误");
        }
    }
}
