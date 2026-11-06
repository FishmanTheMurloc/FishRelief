using System.Text.Json.Nodes;
using FishRelief.Kernel.Manifest;

namespace FishRelief.Tests;

/// <summary>
/// 工具 parameters 可省略：省略时清单可加载且不向上游转发该键；一旦写了就必须是 JSON 对象。
/// </summary>
public sealed class ToolParametersTests
{
    [Fact]
    public void Load_OmittedParameters_IsAccepted()
    {
        var manifestPath = WriteManifest(manifest => FirstTool(manifest).Remove("parameters"));
        try
        {
            var loaded = ManifestLoader.Load(manifestPath);

            Assert.Null(loaded.Manifest.Tasks.Values.First().Tools[0].Parameters);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(manifestPath)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("[1, 2]")]
    [InlineData("42")]
    public void Load_NonObjectParameters_IsRejected(string parameters)
    {
        var manifestPath = WriteManifest(manifest =>
            FirstTool(manifest)["parameters"] = JsonNode.Parse(parameters));
        try
        {
            var exception = Assert.Throws<ManifestException>(() => ManifestLoader.Load(manifestPath));

            Assert.Contains("parameters 必须是 JSON 对象", exception.Message, StringComparison.Ordinal);
            Assert.Contains("dispatch", exception.Message, StringComparison.Ordinal);
            Assert.Contains("CheckStock", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(manifestPath)!, recursive: true);
        }
    }

    private static JsonObject FirstTool(JsonObject manifest) =>
        manifest["tasks"]!.AsObject()["dispatch"]!.AsObject()["tools"]!.AsArray()[0]!.AsObject();

    private static string WriteManifest(Action<JsonObject> mutate)
    {
        var demoDirectory = Path.Combine(AppContext.BaseDirectory, "manifests", "demo");
        var directory = Path.Combine(Path.GetTempPath(), "fishrelief-tool-params-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var manifest = JsonNode.Parse(
            File.ReadAllText(Path.Combine(demoDirectory, "manifest.json")))!.AsObject();
        mutate(manifest);

        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest.ToJsonString());
        File.Copy(Path.Combine(demoDirectory, "main.liquid"), Path.Combine(directory, "main.liquid"));

        return Path.Combine(directory, "manifest.json");
    }
}
