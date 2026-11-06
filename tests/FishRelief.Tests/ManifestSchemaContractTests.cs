using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace FishRelief.Tests;

/// <summary>
/// manifests/schema.json 与 demo 清单的一致性：schema 只作为清单作者的参考契约，不进入运行时，
/// 因此用测试保证 demo 始终符合它，并锁定几类作者容易写错的负例。
/// </summary>
public sealed class ManifestSchemaContractTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifests", "schema.json")));

    private static readonly EvaluationOptions Options = new();

    [Fact]
    public void DemoManifest_MatchesSchema()
    {
        var result = Schema.Evaluate(DemoElement(), Options);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void DemoManifestWithoutToolParameters_MatchesSchema()
    {
        var demo = DemoNode();
        demo["tasks"]!.AsObject().First().Value!["tools"]!.AsArray()[0]!.AsObject().Remove("parameters");

        var result = Schema.Evaluate(JsonSerializer.SerializeToElement(demo), Options);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("templateFile 为绝对路径")]
    [InlineData("templateFile 含 .. 越界段")]
    [InlineData("firstUserMessage 为纯空白")]
    [InlineData("liquidObject 键为空字符串")]
    [InlineData("liquidObject 键为保留名 term")]
    [InlineData("keys 含纯空白键")]
    public void Schema_RejectsManifestWithKnownMistake(string mutation)
    {
        var demo = DemoNode();

        Mutate(demo, mutation);

        var result = Schema.Evaluate(JsonSerializer.SerializeToElement(demo), Options);

        Assert.False(result.IsValid);
    }

    private static JsonElement DemoElement() => JsonSerializer.SerializeToElement(DemoNode());

    private static JsonNode DemoNode() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "manifests", "demo", "manifest.json")))!;

    private static void Mutate(JsonNode demo, string mutation)
    {
        var task = demo["tasks"]!.AsObject().First().Value!.AsObject();
        var matchCase = demo["rules"]!.AsArray()[0]!.AsObject()["cases"]!.AsArray()[0]!.AsObject();

        switch (mutation)
        {
            case "templateFile 为绝对路径":
                task["promptAssembly"]!.AsObject()["templateFile"] = JsonValue.Create("/abs/x.liquid");
                break;
            case "templateFile 含 .. 越界段":
                task["promptAssembly"]!.AsObject()["templateFile"] = JsonValue.Create("../x.liquid");
                break;
            case "firstUserMessage 为纯空白":
                task["firstUserMessage"] = JsonValue.Create(" ");
                break;
            case "liquidObject 键为空字符串":
                matchCase["liquidObject"]!.AsObject()[""] = JsonValue.Create("x");
                break;
            case "liquidObject 键为保留名 term":
                matchCase["liquidObject"]!.AsObject()["term"] = JsonValue.Create("x");
                break;
            case "keys 含纯空白键":
                matchCase["keys"]!.AsArray().Add(JsonValue.Create(" "));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }
}
