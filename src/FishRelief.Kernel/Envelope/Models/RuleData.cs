using System.Text.Json;
using System.Text.Json.Serialization;

namespace FishRelief.Kernel.Envelope.Models;

/// <summary>
/// 信封 data 数组中单个规则的上报数据（客户端数据，与清单侧的 RuleDescriptor / MatchRuleDescriptor 配置对象
/// 以及加载期编译出的规则计划只是按 name 对应，不是同一结构）。
/// 数据形状由清单该规则的 strategy 决定：arrayKeys（字符串数组）、bert（单字符串，分类器尚未接入）等。
/// </summary>
[JsonConverter(typeof(RuleDataConverter))]
public abstract class RuleData
{
    /// <summary>对应的清单规则名（camelCase），客户端按同名上报；未知/重复名由解析器拒绝。</summary>
    [JsonRequired]
    public string Name { get; init; } = "";
}

/// <summary>
/// 字面量匹配规则（strategy=arrayKeys）的上报数据：字符串数组，逐元素由清单 cases 做精确匹配。
/// 信封 JSON 表示为 {"name": ..., "keys": [...]}。
/// </summary>
public sealed class MatchRuleData : RuleData
{
    /// <summary>本类型在信封对象中的负载字段名（即多态判别符）。</summary>
    public const string PayloadProperty = "keys";

    /// <summary>上报的键，逐元素精确匹配 cases[].keys，未命中即 400。</summary>
    public List<string> Keys { get; init; } = [];
}

/// <summary>
/// RuleData 的多态反序列化器：按对象内的负载字段分发（由 <see cref="Handlers"/> 注册表驱动，
/// 新增策略 = 新增数据类型（自述 PayloadProperty）+ 注册一行）。
/// 判别依据是负载字段而非客户端自报的策略字段——策略的权威在清单，信封不可信。
/// </summary>
public sealed class RuleDataConverter : JsonConverter<RuleData>
{
    /// <summary>负载字段名 → 处理器（数据类型 + 读写委托）。同一负载字段不可重复注册。</summary>
    private static readonly Dictionary<string, PayloadHandler> Handlers = new()
    {
        [MatchRuleData.PayloadProperty] = new(
            typeof(MatchRuleData),
            Read: (name, payload, options) => new MatchRuleData
            {
                Name = name,
                Keys = payload.Deserialize<List<string>>(options)
                    ?? throw new JsonException($"{MatchRuleData.PayloadProperty} 不能为 null"),
            },
            Write: (writer, data, options) =>
                JsonSerializer.Serialize(writer, ((MatchRuleData)data).Keys, options)),
    };

    private sealed record PayloadHandler(
        Type DataType,
        Func<string, JsonElement, JsonSerializerOptions, RuleData> Read,
        Action<Utf8JsonWriter, RuleData, JsonSerializerOptions> Write);

    public override RuleData? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"规则数据必须是对象，实际是 {reader.TokenType}");
        }

        string? name = null;
        PayloadHandler? handler = null;
        string? handlerProperty = null;
        JsonElement payload = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = reader.GetString();
            reader.Read();
            if (property == "name")
            {
                name = JsonSerializer.Deserialize<string>(ref reader, options)
                    ?? throw new JsonException("name 不能为 null");
            }
            else if (Handlers.TryGetValue(property!, out var matched))
            {
                // 多个负载字段同时出现即拒绝，不做静默二选一
                if (handlerProperty is not null)
                {
                    throw new JsonException($"规则数据同时包含 \"{handlerProperty}\" 与 \"{property}\" 两种负载字段");
                }

                handler = matched;
                handlerProperty = property;
                payload = JsonElement.ParseValue(ref reader);
            }
            else
            {
                throw new JsonException($"规则数据包含未知字段 \"{property}\"");
            }
        }

        if (name is null)
        {
            throw new JsonException("规则数据缺少 name");
        }

        if (handler is null)
        {
            throw new JsonException($"规则数据缺少负载字段（{string.Join(" / ", Handlers.Keys)}）");
        }

        return handler.Read(name, payload, options);
    }

    public override void Write(Utf8JsonWriter writer, RuleData value, JsonSerializerOptions options)
    {
        var handler = Handlers.Values.FirstOrDefault(h => h.DataType == value.GetType())
            ?? throw new JsonException($"未注册的 RuleData 类型：{value.GetType().Name}");
        var payloadProperty = Handlers.Single(kv => ReferenceEquals(kv.Value, handler)).Key;

        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WritePropertyName(payloadProperty);
        handler.Write(writer, value, options);
        writer.WriteEndObject();
    }
}
