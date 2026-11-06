using System.Text.Json;
using FishRelief.Kernel.Envelope.Models;
using FishRelief.Kernel.Manifest;
using Json.Schema;

namespace FishRelief.Kernel.Envelope;

/// <summary>
/// 信封解析器：从 system 消息里的渲染信封解析任务选择与各规则上报的数据。
/// </summary>
public static class EnvelopeParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 从 system 消息文本解析出任务与各规则上报的数据。
    /// 失败抛 <see cref="EnvelopeParseException"/>，由宿主映射成 400。
    /// </summary>
    public static TaskData Resolve(LoadedManifest loaded, string systemMessageText)
    {
        var manifest = loaded.Manifest;

        // 只解析一遍文本：JsonDocument 提供原始负载节点，反序列化直接基于同一份文档
        using var document = ParseDocument(systemMessageText);
        RequestEnvelope envelope = ReadEnvelope(document.RootElement);

        // data 属性缺失按“一条规则都没上报”处理；存在但不是数组（含 null）一律拒绝
        JsonElement rawData = TryGetData(document.RootElement, out var present) ? present : default;
        if (rawData.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Array))
        {
            throw new EnvelopeParseException("信封 data 必须是数组");
        }

        if (!manifest.Tasks.ContainsKey(envelope.Task))
        {
            throw new EnvelopeParseException($"信封声明的任务 \"{envelope.Task}\" 不在清单中");
        }

        // 原始项与反序列化结果同序同数，配对后按规则取出待校验的负载节点
        JsonElement[] rawItems = rawData.ValueKind == JsonValueKind.Array
            ? rawData.EnumerateArray().ToArray()
            : [];
        var reported = envelope.Data ?? [];
        if (rawItems.Length != reported.Count)
        {
            throw new EnvelopeParseException("信封 data 项数与反序列化结果不一致");
        }

        // 信封 data 里出现的规则名必须全部是清单声明的规则名（动态集合，不硬编码），且不得重复
        var dataByName = new Dictionary<string, (RuleData Data, JsonElement Raw)>(StringComparer.Ordinal);
        for (var index = 0; index < reported.Count; index++)
        {
            var item = reported[index];
            if (!loaded.PlansByName.ContainsKey(item.Name))
            {
                throw new EnvelopeParseException($"信封包含未声明的键 \"{item.Name}\"");
            }

            if (!dataByName.TryAdd(item.Name, (item, rawItems[index])))
            {
                throw new EnvelopeParseException($"信封重复声明键 \"{item.Name}\"");
            }
        }

        // 负载实现类型必须与规则计划声明的策略一致；交给 schema 会退化成“Expected array but got string”一类模糊文案
        foreach (var plan in loaded.RulePlans)
        {
            if (!dataByName.TryGetValue(plan.Name, out var entry))
            {
                continue;
            }

            if (entry.Data.GetType() != plan.PayloadType)
            {
                throw new EnvelopeParseException($"键 \"{plan.Name}\" 的负载类型与清单策略不匹配");
            }
        }

        // 按清单声明序：必报规则缺项即拒绝；已上报的规则只把该项负载节点交给它自己的 jsonSchema
        foreach (var plan in loaded.RulePlans)
        {
            if (!dataByName.TryGetValue(plan.Name, out var entry))
            {
                if (plan.Required)
                {
                    throw new EnvelopeParseException($"规则 \"{plan.Name}\" 必须上报");
                }

                continue;
            }

            if (!TryGetPayload(entry.Raw, plan.PayloadProperty, out var payload))
            {
                throw new EnvelopeParseException($"规则 \"{plan.Name}\" 缺少负载字段 \"{plan.PayloadProperty}\"");
            }

            var results = plan.PayloadSchema.Evaluate(payload, EvaluationOptions.Default);
            if (!results.IsValid)
            {
                var messages = CollectErrors(results)
                    .Distinct(StringComparer.Ordinal)
                    .Take(3);
                throw new EnvelopeParseException(
                    $"规则 \"{plan.Name}\" 的负载不符合其 jsonSchema：{string.Join("；", messages)}");
            }
        }

        return new TaskData
        {
            TaskId = envelope.Task,
            Data = reported,
        };
    }

    /// <summary>递归收集 schema 校验错误文案，供 400 响应说明使用。</summary>
    private static IEnumerable<string> CollectErrors(EvaluationResults result)
    {
        if (result.Errors is not null)
        {
            foreach (var message in result.Errors.Values)
            {
                yield return message;
            }
        }

        if (result.Details is not null)
        {
            foreach (var detail in result.Details)
            {
                foreach (var message in CollectErrors(detail))
                {
                    yield return message;
                }
            }
        }
    }

    /// <summary>解析信封文本；语法非法一律拒绝。</summary>
    private static JsonDocument ParseDocument(string systemMessageText)
    {
        try
        {
            return JsonDocument.Parse(systemMessageText);
        }
        catch (JsonException ex)
        {
            throw new EnvelopeParseException($"system 消息不是合法的渲染信封：{ex.Message}");
        }
    }

    /// <summary>从已解析的根节点反序列化信封；未知字段、类型不合法一律拒绝。</summary>
    private static RequestEnvelope ReadEnvelope(JsonElement root)
    {
        RequestEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RequestEnvelope>(root, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new EnvelopeParseException($"system 消息不是合法的渲染信封：{ex.Message}");
        }

        if (envelope is null)
        {
            throw new EnvelopeParseException("system 消息为空");
        }

        return envelope;
    }

    /// <summary>取信封根上的 data 原始节点；根不是对象或属性缺失时返回 false（按“一条规则都没上报”处理）。</summary>
    private static bool TryGetData(JsonElement root, out JsonElement data)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out data))
        {
            return true;
        }

        data = default;
        return false;
    }

    /// <summary>取 data 项上的负载节点；缺字段或项不是对象时返回 false（不抛导航异常）。</summary>
    private static bool TryGetPayload(JsonElement item, string property, out JsonElement payload)
    {
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out payload))
        {
            return true;
        }

        payload = default;
        return false;
    }
}
