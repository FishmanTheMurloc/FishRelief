using System.ClientModel.Primitives;
using System.Text.Json;
using FishRelief.Kernel.Rewrite;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

#pragma warning disable OPENAI001 // AsChatMessages 处于实验阶段

namespace FishRelief.Gateway;

/// <summary>
/// 入站 OpenAI 请求的公共解析：body JSON → SDK 消息对象 → MEAI 消息（挂 raw representation），
/// 另解析客户端申报的模型与工具名。
/// 线上端点与 /debug/rewrite 共用同一份解析，避免"预览与线上不一致"。
/// </summary>
internal static class InboundChatRequest
{
    /// <summary>解析结果：MEAI 消息、客户端申报的模型名与工具集、是否流式。</summary>
    public sealed record Parsed(
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> Messages, string? Model, bool Stream, IList<AITool> Tools);

    /// <summary>解析请求体；调用方负责请求体上限与 JsonException 的错误映射。</summary>
    public static async Task<Parsed> ReadAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        var root = document.RootElement;

        var stream = root.TryGetProperty("stream", out var streamValue) && streamValue.ValueKind == JsonValueKind.True;
        // 客户端申报的模型名：仅作选项，改写层按任务 availableModels 判定是否生效
        var model = root.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String
            ? modelValue.GetString()
            : null;

        var messages = ParseMessages(root).AsChatMessages().ToList();
        return new Parsed(messages, model, stream, ParseTools(root));
    }

    /// <summary>messages 数组 → SDK 消息对象（随后 AsChatMessages 转为 MEAI 消息并挂 raw）。</summary>
    private static IEnumerable<OpenAI.Chat.ChatMessage> ParseMessages(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            throw new RequestValidationException("缺少 messages 数组");
        }

        foreach (var element in messages.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new RequestValidationException("messages 元素必须是对象");
            }

            var role = element.TryGetProperty("role", out var roleValue) && roleValue.ValueKind == JsonValueKind.String
                ? roleValue.GetString()
                : throw new RequestValidationException("消息缺少 role");

            var data = BinaryData.FromString(element.GetRawText());
            yield return role switch
            {
                "system" => ReadMessage<SystemChatMessage>(data),
                "user" => ReadMessage<UserChatMessage>(data),
                "assistant" => ReadMessage<AssistantChatMessage>(data),
                "tool" => ReadMessage<ToolChatMessage>(data),
                _ => throw new RequestValidationException($"不支持的消息角色 \"{role}\""),
            };
        }
    }

    /// <summary>
    /// tools 数组 → 只留名字的工具声明桩；描述与参数 schema 由改写层按清单同名项重建，
    /// 因此这里只需结构性校验（function 类型、非空唯一名字），其余字段丢弃。
    /// </summary>
    private static List<AITool> ParseTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (tools.ValueKind != JsonValueKind.Array)
        {
            throw new RequestValidationException("tools 必须是数组");
        }

        var result = new List<AITool>(tools.GetArrayLength());
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in tools.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new RequestValidationException("tools 元素必须是对象");
            }

            var type = element.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString()
                : throw new RequestValidationException("工具声明缺少 type");
            if (type != "function")
            {
                throw new RequestValidationException($"不支持的工具类型 \"{type}\"");
            }

            if (!element.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
            {
                throw new RequestValidationException("function 工具声明缺少 function 对象");
            }

            var name = function.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()
                : throw new RequestValidationException("工具声明缺少 name");
            if (string.IsNullOrEmpty(name))
            {
                throw new RequestValidationException("工具 name 不能为空");
            }

            if (!names.Add(name))
            {
                throw new RequestValidationException($"工具 \"{name}\" 重复声明");
            }

            result.Add(new JsonFunctionDeclaration(name, description: "", EmptySchema));
        }

        return result;
    }

    /// <summary>工具声明桩的占位参数 schema（不会被发给上游，改写层会用清单定义替换整个声明）。</summary>
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement.Clone();

    private static T ReadMessage<T>(BinaryData data) where T : OpenAI.Chat.ChatMessage
    {
        try
        {
            return ModelReaderWriter.Read<T>(data) ?? throw new RequestValidationException("消息解析失败");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new RequestValidationException($"消息解析失败：{ex.Message}");
        }
    }
}
