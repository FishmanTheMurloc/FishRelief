using System.Text.Json.Nodes;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Manifest.Models;

namespace FishRelief.Tests;

/// <summary>
/// 清单规则的负载 schema 加载期校验：每条规则自带一份普通 JSON Schema，须与策略要求的负载形状对齐，
/// 且不得混入 oneOf/contains 一类信封结构约定（信封结构由内核核验）。
/// 直接在已加载的 demo 清单上原地变形再跑 ManifestValidator，避免落盘临时清单。
/// </summary>
public sealed class ManifestSchemaValidationTests
{
    [Fact]
    public void Validate_RuleWithoutJsonSchema_IsRejected()
    {
        var loaded = Load();
        var source = (MatchRuleDescriptor)loaded.Manifest.Rules[0];
        var broken = new MatchRuleDescriptor
        {
            Name = source.Name,
            Strategy = source.Strategy,
            Required = source.Required,
            PayloadSchema = null!,
            Cases = source.Cases,
        };

        var errors = Collect(loaded, WithRules(loaded.Manifest, [broken]));

        Assert.Contains(errors, error => error.Contains("缺少 jsonSchema", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RuleSchemaWithStructuralKeyword_IsRejected()
    {
        var loaded = Load();
        var schema = (JsonObject)loaded.Manifest.Rules[0].PayloadSchema;
        schema["oneOf"] = new JsonArray();

        var errors = Collect(loaded, loaded.Manifest);

        Assert.Contains(
            errors,
            error => error.Contains("不支持的 JSON Schema 关键字 \"oneOf\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RuleSchemaWithoutArrayType_IsRejected()
    {
        var loaded = Load();
        var schema = (JsonObject)loaded.Manifest.Rules[0].PayloadSchema;
        schema.Remove("type");

        var errors = Collect(loaded, loaded.Manifest);

        Assert.Contains(
            errors,
            error => error.Contains("jsonSchema.type 必须为 array", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RuleSchemaItemsNotString_IsRejected()
    {
        var loaded = Load();
        var schema = (JsonObject)loaded.Manifest.Rules[0].PayloadSchema;
        ((JsonObject)schema["items"]!)["type"] = "number";

        var errors = Collect(loaded, loaded.Manifest);

        Assert.Contains(
            errors,
            error => error.Contains("jsonSchema.items.type 必须为 string", StringComparison.Ordinal));
    }

    private static ProjectManifest WithRules(ProjectManifest source, List<RuleDescriptor> rules) => new()
    {
        Project = source.Project,
        Tasks = source.Tasks,
        Rules = rules,
        Limits = source.Limits,
        Admission = source.Admission,
        Endpoints = source.Endpoints,
    };

    private static LoadedManifest Load()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifests", "demo", "manifest.json");
        return ManifestLoader.Load(manifestPath);
    }

    private static List<string> Collect(LoadedManifest loaded, ProjectManifest manifest)
    {
        var errors = new List<string>();
        ManifestValidator.Validate(
            manifest,
            loaded.ManifestDirectory,
            errors,
            new Dictionary<string, string>(StringComparer.Ordinal));
        return errors;
    }
}
