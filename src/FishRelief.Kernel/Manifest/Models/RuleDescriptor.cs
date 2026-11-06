using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FishRelief.Kernel.Manifest.Models;

/// <summary>
/// 清单中的一条规则描述（JSON 配置形态，与信封侧的 RuleData 按 name 对应，不是同一结构）。
/// 与加载期编译出的 <see cref="FishRelief.Kernel.Manifest.Rule"/> 计划对象区分：本类型只承载契约字段，
/// 不含词表索引与条目构建逻辑；运行时行为在 <see cref="FishRelief.Kernel.Manifest.MatchRule"/> 里。
/// strategy 决定输入形状与命中算法，同时是清单 JSON 中的多态判别值（由 <see cref="RuleConverter"/> 分派到具体子类）。
/// </summary>
[JsonConverter(typeof(RuleConverter))]
public abstract class RuleDescriptor
{
    /// <summary>规则名，采用 camelCase（合法 Liquid 标识符）；同时是信封 data 项的 name 与模板变量名。</summary>
    [JsonRequired]
    public string Name { get; init; } = "";

    [JsonRequired]
    public RuleStrategy Strategy { get; init; } = RuleStrategy.ArrayKeys;

    /// <summary>该规则是否必须在信封 data 中上报；由内核在解析期检查（schema 只描述负载形状）。</summary>
    [JsonRequired]
    public bool Required { get; init; }

    /// <summary>该规则负载节点的普通 JSON Schema；arrayKeys 策略下即 data[i].keys，只描述这一项负载的形状。</summary>
    [JsonPropertyName("jsonSchema")]
    [JsonRequired]
    public JsonNode PayloadSchema { get; init; } = null!;
}

/// <summary>
/// 字面量匹配规则描述（strategy=arrayKeys）：cases 提供封闭词表，命中后并入 liquidObject。
/// 加载期由 <see cref="FishRelief.Kernel.Manifest.MatchRule"/> 计划对象把词表折叠成索引；本类型不参与请求期计算。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MatchRuleDescriptor : RuleDescriptor
{
    /// <summary>命中分支表：每个分支声明一组字面量键与命中后并入 Liquid 对象的字段。</summary>
    [JsonRequired]
    public List<MatchCase> Cases { get; init; } = [];
}

/// <summary>
/// RuleDescriptor 的多态反序列化器：按清单的 strategy 字段分派（由 <see cref="Handlers"/> 注册表驱动，
/// 新增策略 = 新增子类 + 注册一行）。不使用 [JsonDerivedType]：STJ 会消费判别属性，
/// 导致基类的 Strategy 无法落值，这也正是信封侧 RuleDataConverter 采用自定义转换器的原因。
/// </summary>
public sealed class RuleConverter : JsonConverter<RuleDescriptor>
{
    private sealed record StrategyHandler(
        Type DescriptorType,
        Func<JsonElement, JsonSerializerOptions, RuleDescriptor> Read);

    /// <summary>strategy 的 JSON 值（camelCase）→ 处理器（子类类型 + 构造委托）。同一策略值不可重复注册。</summary>
    private static readonly Dictionary<string, StrategyHandler> Handlers = new(StringComparer.Ordinal)
    {
        [StrategyKey(RuleStrategy.ArrayKeys)] = new(
            typeof(MatchRuleDescriptor),
            (payload, options) => payload.Deserialize<MatchRuleDescriptor>(options)
                ?? throw new JsonException("规则对象为空")),
    };

    private static string StrategyKey(RuleStrategy strategy) =>
        JsonNamingPolicy.CamelCase.ConvertName(strategy.ToString());

    public override RuleDescriptor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var element = JsonElement.ParseValue(ref reader);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"规则必须是对象，实际是 {element.ValueKind}");
        }

        if (!element.TryGetProperty("strategy", out var strategyNode))
        {
            throw new JsonException($"规则缺少 strategy 字段（支持：{string.Join(" / ", Handlers.Keys)}）");
        }

        if (strategyNode.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"规则的 strategy 必须是字符串，实际是 {strategyNode.ValueKind}");
        }

        var strategy = strategyNode.GetString()!;
        if (!Handlers.TryGetValue(strategy, out var handler))
        {
            throw new JsonException($"未知的规则 strategy \"{strategy}\"（支持：{string.Join(" / ", Handlers.Keys)}）");
        }

        return handler.Read(element, options);
    }

    public override void Write(Utf8JsonWriter writer, RuleDescriptor value, JsonSerializerOptions options)
    {
        if (!Handlers.Values.Any(handler => handler.DescriptorType == value.GetType()))
        {
            throw new JsonException($"未注册的 RuleDescriptor 类型：{value.GetType().Name}");
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
