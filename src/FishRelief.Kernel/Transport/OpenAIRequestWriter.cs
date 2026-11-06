using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAIChatMessage = OpenAI.Chat.ChatMessage;

namespace FishRelief.Kernel.Transport;

/// <summary>
/// OpenAI chat completions 请求体序列化。raw 优先：消息携带
/// <see cref="ChatMessage.RawRepresentation"/>（OpenAI SDK 消息对象）时按原对象序列化，
/// 绕开类型往返；否则从 MEAI 内容手写映射（网关权威新建的 system/user 走这条路）。
/// max_tokens 显式写 max_tokens 字段（SDK 默认发 max_completion_tokens）。
/// </summary>
public static class OpenAIRequestWriter
{
    public static BinaryData Write(IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var root = new JsonObject();
        if (!string.IsNullOrEmpty(options?.ModelId))
        {
            root["model"] = options.ModelId;
        }

        root["stream"] = stream;
        if (options?.MaxOutputTokens is { } maxTokens)
        {
            root["max_tokens"] = maxTokens;
        }

        if (options?.StopSequences is { Count: > 0 } stops)
        {
            root["stop"] = new JsonArray(stops.Select(s => JsonValue.Create(s)).ToArray());
        }

        var messagesNode = new JsonArray();
        foreach (var message in messages)
        {
            messagesNode.Add(WriteMessage(message));
        }

        root["messages"] = messagesNode;

        var declarations = options?.Tools?.OfType<AIFunctionDeclaration>().ToList();
        if (declarations is { Count: > 0 })
        {
            var toolsNode = new JsonArray();
            foreach (var declaration in declarations)
            {
                var function = new JsonObject
                {
                    ["name"] = declaration.Name,
                    ["description"] = declaration.Description,
                };
                if (declaration.JsonSchema.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    function["parameters"] = JsonNode.Parse(declaration.JsonSchema.GetRawText());
                }

                toolsNode.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = function,
                });
            }

            root["tools"] = toolsNode;
            if (options?.ToolMode is { } mode)
            {
                root["tool_choice"] = mode switch
                {
                    AutoChatToolMode => "auto",
                    NoneChatToolMode => "none",
                    RequiredChatToolMode => "required",
                    _ => "auto",
                };
            }
        }

        return BinaryData.FromString(root.ToJsonString(Options));
    }

    /// <summary>宽松转义：中文与引号按字面输出（JSON 语义不变），避免 \uXXXX 转义膨胀请求体。</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonNode WriteMessage(ChatMessage message)
    {
        // raw 优先：客户端历史（Gateway 解析时挂上的 SDK 消息对象）按原对象序列化，零往返
        if (message.RawRepresentation is OpenAIChatMessage raw)
        {
            return JsonNode.Parse(ModelReaderWriter.Write(raw).ToString()) ?? throw new InvalidOperationException("raw 消息序列化为空");
        }

        var node = new JsonObject { ["role"] = WriteRole(message.Role) };
        switch (message.Role)
        {
            case var role when role == ChatRole.System || role == ChatRole.User:
                node["content"] = message.Text;
                break;

            case var role when role == ChatRole.Assistant:
            {
                var text = message.Contents.OfType<TextContent>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
                node["content"] = string.IsNullOrEmpty(text) ? null : text;
                var calls = message.Contents.OfType<FunctionCallContent>().ToList();
                if (calls.Count > 0)
                {
                    var callsNode = new JsonArray();
                    foreach (var call in calls)
                    {
                        callsNode.Add(new JsonObject
                        {
                            ["id"] = call.CallId,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = call.Arguments is null ? "{}" : JsonSerializer.Serialize(call.Arguments),
                            },
                        });
                    }

                    node["tool_calls"] = callsNode;
                }

                break;
            }

            case var role when role == ChatRole.Tool:
            {
                var result = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
                node["tool_call_id"] = result?.CallId;
                node["content"] = result is null ? "" : WriteResult(result.Result);
                break;
            }

            default:
                throw new NotSupportedException($"不支持的角色 {message.Role}");
        }

        return node;
    }

    private static string WriteRole(ChatRole role) =>
        role == ChatRole.System ? "system"
        : role == ChatRole.User ? "user"
        : role == ChatRole.Assistant ? "assistant"
        : role == ChatRole.Tool ? "tool"
        : throw new NotSupportedException($"不支持的角色 {role}");

    private static string WriteResult(object? result) => result switch
    {
        null => "",
        string text => text,
        _ => JsonSerializer.Serialize(result),
    };
}
