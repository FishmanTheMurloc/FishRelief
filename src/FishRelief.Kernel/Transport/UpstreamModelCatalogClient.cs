using System.Text;
using System.Text.Json;

namespace FishRelief.Kernel.Transport;

/// <summary>
/// 上游模型清单探测：GET 上游模型端点，解析 OpenAI 兼容响应中的 data[].id。
/// 只做协议与传输；探测时机、超时与缓存策略由宿主负责。
/// </summary>
public sealed class UpstreamModelCatalogClient
{
    private readonly HttpMessageInvoker _invoker;
    private readonly Uri _modelsEndpoint;
    private readonly Func<string> _apiKeyProvider;

    public UpstreamModelCatalogClient(HttpMessageInvoker invoker, Uri modelsEndpoint, Func<string> apiKeyProvider)
    {
        _invoker = invoker;
        _modelsEndpoint = modelsEndpoint;
        _apiKeyProvider = apiKeyProvider;
    }

    /// <summary>拉取上游可用模型 id 集合；非成功状态码抛 <see cref="UpstreamException"/>。</summary>
    public async Task<IReadOnlySet<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _modelsEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKeyProvider()}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _invoker.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpstreamException((int)response.StatusCode, Encoding.UTF8.GetString(content));
        }

        return ParseModelIds(content);
    }

    /// <summary>解析 {"data":[{"id":"..."}]}；结构不符或清单为空抛 <see cref="InvalidOperationException"/>。</summary>
    private static IReadOnlySet<string> ParseModelIds(byte[] content)
    {
        using var document = JsonDocument.Parse(content.AsMemory());
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("上游模型清单响应缺少 data 数组");
        }

        var models = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString()))
            {
                models.Add(id.GetString()!);
            }
        }

        if (models.Count == 0)
        {
            throw new InvalidOperationException("上游模型清单为空");
        }

        return models;
    }
}
