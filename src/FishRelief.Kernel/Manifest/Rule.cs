using System.Collections.Frozen;
using System.Text.Json;
using FishRelief.Kernel.Envelope.Models;
using FishRelief.Kernel.Manifest.Models;
using Json.Schema;

namespace FishRelief.Kernel.Manifest;

/// <summary>
/// 一条规则的加载期编译产物：绑定规则名、策略、信封负载类型、负载 schema 与命中算法，
/// 请求期不再按策略分派，只做一次负载类型核对、一次 schema 校验与一次查找。
/// 由 <see cref="ManifestLoader"/> 在清单校验通过后构建，并按清单声明序排列（即渲染顺序）。
/// 与清单描述 <see cref="RuleDescriptor"/> 区分：那是 JSON 契约形态，本类型是运行时计划。
/// </summary>
internal abstract class Rule
{
    /// <summary>规则名，与清单 rules[].name、信封 data[].name、模板变量名一致。</summary>
    public abstract string Name { get; }

    /// <summary>命中策略，与清单 strategy 对应。</summary>
    public abstract RuleStrategy Strategy { get; }

    /// <summary>该策略在信封里接受的负载实现类型；解析器以此做精确类型核对。</summary>
    public abstract Type PayloadType { get; }

    /// <summary>该策略在信封 data 项里的负载属性名（arrayKeys 即 keys）；解析器据此取出待校验的负载节点。</summary>
    public abstract string PayloadProperty { get; }

    /// <summary>该规则负载节点的编译后 schema；解析器只校验这一项负载，信封结构由内核核验。</summary>
    public abstract JsonSchema PayloadSchema { get; }

    /// <summary>该规则是否必须上报；解析期缺项即 400。</summary>
    public abstract bool Required { get; }

    /// <summary>把已校验的负载转换为模板条目；规则未上报（null）时为空列表。</summary>
    public abstract IReadOnlyList<Dictionary<string, object>> BuildEntries(RuleData? data);
}

/// <summary>
/// 字面量匹配规则（strategy=arrayKeys）的运行时计划：词表已在加载期折叠成“键 → case”，
/// 请求期逐键一次查找，未命中即客户端错误（400）。
/// </summary>
internal sealed class MatchRule : Rule
{
    private readonly FrozenDictionary<string, MatchCase> _keyToCase;

    public MatchRule(
        string name,
        bool required,
        JsonSchema payloadSchema,
        FrozenDictionary<string, MatchCase> keyToCase)
    {
        Name = name;
        Required = required;
        PayloadSchema = payloadSchema;
        _keyToCase = keyToCase;
    }

    public override string Name { get; }

    public override RuleStrategy Strategy => RuleStrategy.ArrayKeys;

    public override Type PayloadType => typeof(MatchRuleData);

    public override string PayloadProperty => MatchRuleData.PayloadProperty;

    public override JsonSchema PayloadSchema { get; }

    public override bool Required { get; }

    public override IReadOnlyList<Dictionary<string, object>> BuildEntries(RuleData? data)
    {
        if (data is null)
        {
            return [];
        }

        if (data is not MatchRuleData matchData)
        {
            throw new PromptAssemblyException($"规则 \"{Name}\" 的负载类型与清单策略不匹配");
        }

        var entries = new List<Dictionary<string, object>>();
        foreach (var key in matchData.Keys)
        {
            if (!_keyToCase.TryGetValue(key, out var matchCase))
            {
                // 客户端上报了清单词表里没有的键：语义上是 400，必须抛出而不是静默丢行
                throw new RuleKeyNotMatchedException($"键 \"{Name}\" 的值 \"{key}\" 未命中任何 case");
            }

            var entry = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                // term：键命中策略下即命中的上报键
                ["term"] = key,
            };
            foreach (var (field, value) in matchCase.LiquidObject)
            {
                entry[field] = ToTemplateValue(value);
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static object ToTemplateValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array => value.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty : x.ToString())
            .ToArray(),
        _ => value.ToString(),
    };
}
