using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using FishRelief.Kernel.Manifest.Models;
using Json.Schema;

namespace FishRelief.Kernel.Manifest;

/// <summary>
/// 从项目包目录加载清单（manifests/{project}/manifest.json）及其引用的模板。
/// 加载即校验，任何不合法项都抛 <see cref="ManifestException"/>。
/// </summary>
public static class ManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static LoadedManifest Load(string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ManifestException($"无法确定清单所在目录：{manifestPath}");

        ProjectManifest manifest;
        try
        {
            var json = File.ReadAllText(fullPath);
            manifest = JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions)
                ?? throw new ManifestException($"清单内容为空：{manifestPath}");
        }
        catch (JsonException ex)
        {
            throw new ManifestException($"清单解析失败：{manifestPath}：{ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ManifestException($"清单读取失败：{manifestPath}：{ex.Message}");
        }

        var errors = new List<string>();
        var templatePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        ManifestValidator.Validate(manifest, directory, errors, templatePaths);
        if (errors.Count > 0)
        {
            throw new ManifestException($"清单校验失败：{manifestPath}", errors);
        }

        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (taskId, path) in templatePaths)
        {
            try
            {
                templates[taskId] = File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                throw new ManifestException($"任务 \"{taskId}\" 的模板读取失败：{path}", [ex.Message]);
            }
        }

        // 规则计划只能在 ManifestValidator 通过后构建：重复键已被拒绝，此处“键 → case”折叠才是无损的
        var rulePlans = new List<Rule>(manifest.Rules.Count);
        foreach (var descriptor in manifest.Rules)
        {
            if (descriptor is not MatchRuleDescriptor matchDescriptor)
            {
                throw new ManifestException($"规则 \"{descriptor.Name}\" 的实现类型 {descriptor.GetType().Name} 未接入编译");
            }

            var keyToCase = new Dictionary<string, MatchCase>(StringComparer.Ordinal);
            foreach (var matchCase in matchDescriptor.Cases)
            {
                foreach (var key in matchCase.Keys)
                {
                    keyToCase[key] = matchCase;
                }
            }

            rulePlans.Add(new MatchRule(
                matchDescriptor.Name,
                matchDescriptor.Required,
                CompilePayloadSchema(matchDescriptor, manifestPath),
                keyToCase.ToFrozenDictionary(StringComparer.Ordinal)));
        }

        return new LoadedManifest
        {
            Manifest = manifest,
            ManifestDirectory = directory,
            TaskTemplates = templates,
            RulePlans = rulePlans,
            PlansByName = rulePlans.ToFrozenDictionary(plan => plan.Name, StringComparer.Ordinal),
        };
    }

    /// <summary>编译单条规则的负载 schema；节点自身是普通 JSON Schema，不再移除 $id（校验器已不允许该关键字）。</summary>
    private static JsonSchema CompilePayloadSchema(RuleDescriptor descriptor, string manifestPath)
    {
        try
        {
            return JsonSchema.FromText(descriptor.PayloadSchema.ToJsonString());
        }
        catch (Exception ex)
        {
            throw new ManifestException($"规则 \"{descriptor.Name}\" 的 jsonSchema 编译失败：{manifestPath}", [ex.Message]);
        }
    }
}
