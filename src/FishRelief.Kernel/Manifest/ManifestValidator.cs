using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FishRelief.Kernel.Manifest.Models;

namespace FishRelief.Kernel.Manifest;

/// <summary>
/// 清单校验：manifests/schema.json 能表达的约束由严格反序列化覆盖，这里只做跨字段引用与路径安全检查。
/// </summary>
internal static class ManifestValidator
{
    private static readonly Regex ProjectIdPattern = new("^[a-z][a-z0-9-]*$");
    private static readonly Regex TaskIdPattern = new("^[a-z][a-zA-Z0-9_-]*$");
    private static readonly Regex ModelNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._:/-]*$");
    private static readonly Regex ToolNamePattern = new("^[A-Za-z][A-Za-z0-9_]*$");
    private static readonly Regex EndpointPattern = new("^/v1/[a-z0-9/_-]+$");
    private static readonly Regex RuleNamePattern = new("^[a-z][a-zA-Z0-9]*$");

    private static readonly HashSet<string> PayloadSchemaKeywords = new(StringComparer.Ordinal)
    {
        "type", "items", "minItems", "maxItems", "description",
    };

    private static readonly HashSet<string> PayloadItemSchemaKeywords = new(StringComparer.Ordinal)
    {
        "type", "maxLength", "description",
    };

    public static void Validate(
        ProjectManifest manifest,
        string directory,
        List<string> errors,
        Dictionary<string, string> templatePaths)
    {
        if (!ProjectIdPattern.IsMatch(manifest.Project))
        {
            errors.Add($"project \"{manifest.Project}\" 不符合 ^[a-z][a-z0-9-]*$");
        }

        ValidateTasks(manifest, directory, errors, templatePaths);
        ValidateRules(manifest, errors);
        ValidateLimits(manifest, errors);
        ValidateAdmission(manifest, errors);
        ValidateEndpoints(manifest, errors);
    }

    private static void ValidateTasks(
        ProjectManifest manifest,
        string directory,
        List<string> errors,
        Dictionary<string, string> templatePaths)
    {
        if (manifest.Tasks.Count == 0)
        {
            errors.Add("tasks 不能为空");
            return;
        }

        foreach (var (taskId, task) in manifest.Tasks)
        {
            if (!TaskIdPattern.IsMatch(taskId))
            {
                errors.Add($"task id \"{taskId}\" 不符合 ^[a-z][a-zA-Z0-9_-]*$");
            }

            if (task.AvailableModels.Count == 0)
            {
                errors.Add($"任务 \"{taskId}\" 的 availableModels 不能为空");
            }
            else
            {
                foreach (var model in task.AvailableModels)
                {
                    if (!ModelNamePattern.IsMatch(model))
                    {
                        errors.Add($"任务 \"{taskId}\" 的模型名 \"{model}\" 不符合 ^[A-Za-z0-9][A-Za-z0-9._:/-]*$");
                    }
                }

                CheckDuplicates(task.AvailableModels, $"任务 \"{taskId}\" 的 availableModels", errors);
            }

            if (string.IsNullOrWhiteSpace(task.FirstUserMessage))
            {
                errors.Add($"任务 \"{taskId}\" 的 firstUserMessage 不能为空");
            }

            if (task.Tools.Count == 0)
            {
                errors.Add($"任务 \"{taskId}\" 的 tools 不能为空");
            }
            else
            {
                foreach (var tool in task.Tools)
                {
                    if (tool.Parameters is { } parameters && parameters.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"任务 \"{taskId}\" 的工具 \"{tool.Name}\" 的 parameters 必须是 JSON 对象");
                    }

                    if (!ToolNamePattern.IsMatch(tool.Name))
                    {
                        errors.Add($"任务 \"{taskId}\" 的工具名 \"{tool.Name}\" 不符合 ^[A-Za-z][A-Za-z0-9_]*$");
                    }
                }

                CheckDuplicates(task.Tools.Select(t => t.Name), $"任务 \"{taskId}\" 的工具名", errors);
            }

            if (task.PromptAssembly is null)
            {
                errors.Add($"任务 \"{taskId}\" 缺少 promptAssembly");
                continue;
            }

            var templatePath = ResolveTemplatePath(directory, task.PromptAssembly.TemplateFile);
            if (templatePath is null)
            {
                errors.Add($"任务 \"{taskId}\" 的 templateFile \"{task.PromptAssembly.TemplateFile}\" 无效（必须是相对路径且不越出清单目录）");
                continue;
            }

            if (!File.Exists(templatePath))
            {
                errors.Add($"任务 \"{taskId}\" 的模板文件不存在：{templatePath}");
                continue;
            }

            templatePaths[taskId] = templatePath;
        }
    }

    private static void ValidateRules(ProjectManifest manifest, List<string> errors)
    {
        if (manifest.Rules.Count == 0)
        {
            errors.Add("rules 不能为空");
            return;
        }

        var ruleNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in manifest.Rules)
        {
            if (!RuleNamePattern.IsMatch(rule.Name))
            {
                errors.Add($"rules 名称 \"{rule.Name}\" 不符合 ^[a-z][a-zA-Z0-9]*$");
            }

            if (!ruleNames.Add(rule.Name))
            {
                errors.Add($"rules 名称重复：{rule.Name}");
            }

            if (rule.Strategy != RuleStrategy.ArrayKeys)
            {
                errors.Add($"规则 \"{rule.Name}\" 的 strategy 当前只支持 arrayKeys");
            }

            ValidatePayloadSchema(rule, errors);

            if (rule is not MatchRuleDescriptor matchRule)
            {
                errors.Add($"规则 \"{rule.Name}\" 的实现类型 {rule.GetType().Name} 未接入校验");
                continue;
            }

            if (matchRule.Cases.Count == 0)
            {
                errors.Add($"规则 \"{rule.Name}\" 的 cases 不能为空");
                continue;
            }

            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var matchCase in matchRule.Cases)
            {
                if (matchCase.Keys.Count == 0)
                {
                    errors.Add($"规则 \"{rule.Name}\" 的 case 必须声明至少一个键");
                    continue;
                }

                var label = string.Join(" / ", matchCase.Keys);

                foreach (var key in matchCase.Keys)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        errors.Add($"case [{label}] 含空白键（规则 \"{rule.Name}\"）");
                        continue;
                    }

                    if (key.Any(char.IsControl))
                    {
                        errors.Add($"case [{label}] 的键 \"{key}\" 含控制字符（规则 \"{rule.Name}\"）");
                    }

                    if (!seenKeys.Add(key))
                    {
                        errors.Add($"同一规则内键重复：\"{key}\"（规则 \"{rule.Name}\"）");
                    }
                }

                foreach (var (field, value) in matchCase.LiquidObject)
                {
                    if (string.IsNullOrWhiteSpace(field))
                    {
                        errors.Add($"case [{label}] 的 liquidObject 含空白字段名（规则 \"{rule.Name}\"）");
                    }
                    else if (field == "term")
                    {
                        errors.Add($"case [{label}] 的 liquidObject 不能使用保留字段名 \"term\"（规则 \"{rule.Name}\"）");
                    }

                    if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
                    {
                        errors.Add($"case [{label}] 的字段 \"{field}\" 必须是字符串或字符串数组（规则 \"{rule.Name}\"）");
                    }
                    else if (value.ValueKind == JsonValueKind.Array &&
                             value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                    {
                        errors.Add($"case [{label}] 的字段 \"{field}\" 数组元素必须是字符串（规则 \"{rule.Name}\"）");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 规则负载 schema 校验：每条规则自带一份普通 JSON Schema，只允许受控关键词子集，并与策略要求的负载形状对齐。
    /// 信封结构（data 是数组、每项必须有 name、哪些规则必报）由内核核验，不写进 schema。
    /// </summary>
    private static void ValidatePayloadSchema(RuleDescriptor rule, List<string> errors)
    {
        var what = $"规则 \"{rule.Name}\" 的 jsonSchema";

        if (rule.PayloadSchema is not JsonObject schema)
        {
            errors.Add($"规则 \"{rule.Name}\" 缺少 jsonSchema（必须是 JSON 对象）");
            return;
        }

        CheckAllowedKeywords(schema, PayloadSchemaKeywords, what, errors);

        if (!TryGetString(schema, "type", out var type) || type != "array")
        {
            errors.Add($"{what}.type 必须为 array（arrayKeys 策略的负载是字符串数组）");
        }

        if (!TryGetObject(schema, "items", out var items))
        {
            errors.Add($"{what}.items 必须是对象");
        }
        else
        {
            CheckAllowedKeywords(items, PayloadItemSchemaKeywords, $"{what}.items", errors);

            if (!TryGetString(items, "type", out var itemType) || itemType != "string")
            {
                errors.Add($"{what}.items.type 必须为 string");
            }

            if (!TryGetInt(items, "maxLength", out var maxLength) || maxLength < 1)
            {
                errors.Add($"{what}.items.maxLength 必须为正整数");
            }
        }

        if (!TryGetInt(schema, "maxItems", out var maxItems) || maxItems < 1)
        {
            errors.Add($"{what}.maxItems 必须为正整数");
        }

        if (schema.TryGetPropertyValue("minItems", out _))
        {
            if (!TryGetInt(schema, "minItems", out var minItems) || minItems < 0)
            {
                errors.Add($"{what}.minItems 必须为非负整数");
            }
            else if (maxItems >= 1 && minItems > maxItems)
            {
                errors.Add($"{what} 的 minItems 不能大于 maxItems");
            }
        }
    }

    private static void CheckAllowedKeywords(
        JsonObject schema,
        HashSet<string> allowed,
        string what,
        List<string> errors)
    {
        foreach (var (keyword, _) in schema)
        {
            if (!allowed.Contains(keyword))
            {
                errors.Add($"{what} 包含不支持的 JSON Schema 关键字 \"{keyword}\"");
            }
        }
    }

    private static bool TryGetString(JsonObject schema, string name, out string value)
    {
        if (schema.TryGetPropertyValue(name, out var node) &&
            node is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = "";
        return false;
    }

    private static bool TryGetInt(JsonObject schema, string name, out int value)
    {
        if (schema.TryGetPropertyValue(name, out var node) &&
            node is JsonValue jsonValue &&
            jsonValue.TryGetValue<int>(out var number))
        {
            value = number;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetObject(JsonObject schema, string name, out JsonObject value)
    {
        if (schema.TryGetPropertyValue(name, out var node) && node is JsonObject objectValue)
        {
            value = objectValue;
            return true;
        }

        value = null!;
        return false;
    }

    private static void ValidateLimits(ProjectManifest manifest, List<string> errors)
    {
        if (manifest.Limits is null)
        {
            errors.Add("limits 不能为空");
            return;
        }

        if (manifest.Limits.MaxMessages < 2)
        {
            errors.Add($"limits.maxMessages 必须 >= 2，实际为 {manifest.Limits.MaxMessages}");
        }

        if (manifest.Limits.MaxBodyBytes < 1)
        {
            errors.Add($"limits.maxBodyBytes 必须 >= 1，实际为 {manifest.Limits.MaxBodyBytes}");
        }
    }

    private static void ValidateAdmission(ProjectManifest manifest, List<string> errors)
    {
        if (manifest.Admission is null)
        {
            errors.Add("admission 不能为空");
            return;
        }

        if (manifest.Admission.MaxConcurrentGlobal < 1)
        {
            errors.Add($"admission.maxConcurrentGlobal 必须 >= 1，实际为 {manifest.Admission.MaxConcurrentGlobal}");
        }

        if (manifest.Admission.WaitSeconds < 0)
        {
            errors.Add($"admission.waitSeconds 不能为负，实际为 {manifest.Admission.WaitSeconds}");
        }

        if (manifest.Admission.MaxWaiters < 0)
        {
            errors.Add($"admission.maxWaiters 不能为负，实际为 {manifest.Admission.MaxWaiters}");
        }

        if (manifest.Admission.RetryAfterSeconds < 1)
        {
            errors.Add($"admission.retryAfterSeconds 必须 >= 1，实际为 {manifest.Admission.RetryAfterSeconds}");
        }
    }

    private static void ValidateEndpoints(ProjectManifest manifest, List<string> errors)
    {
        if (manifest.Endpoints.Count == 0)
        {
            errors.Add("endpoints 不能为空");
            return;
        }

        foreach (var endpoint in manifest.Endpoints)
        {
            if (!EndpointPattern.IsMatch(endpoint))
            {
                errors.Add($"endpoint \"{endpoint}\" 不符合 ^/v1/[a-z0-9/_-]+$");
            }
        }

        CheckDuplicates(manifest.Endpoints, "endpoints", errors);
    }

    /// <summary>
    /// 把模板相对路径解析为绝对路径；绝对路径或越出清单目录时返回 null。
    /// </summary>
    private static string? ResolveTemplatePath(string directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var root = Path.GetFullPath(directory);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    private static void CheckDuplicates<T>(IEnumerable<T> items, string what, List<string> errors) where T : notnull
    {
        var seen = new HashSet<T>();
        foreach (var item in items)
        {
            if (!seen.Add(item))
            {
                errors.Add($"{what}重复：{item}");
            }
        }
    }
}
