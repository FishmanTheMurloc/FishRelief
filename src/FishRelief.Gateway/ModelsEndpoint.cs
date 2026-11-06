using FishRelief.Kernel.Manifest;

namespace FishRelief.Gateway;

/// <summary>
/// /v1/models 端点：返回清单内所有任务 availableModels 的模型名并集（OpenAI 列表形状）。
/// 只回清单声明的模型名，不代理上游模型清单；
/// 客户端据此选择 model；请求的 model 不在本任务可用集内时，改写层返回 400。
/// </summary>
internal static class ModelsEndpoint
{
    public static async Task HandleAsync(HttpContext context, IManifestRepository repository)
    {
        var manifest = repository.Get().Manifest;

        var ids = manifest.Tasks.Values
            .SelectMany(task => task.AvailableModels)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(model => model, StringComparer.Ordinal)
            .ToList();

        var response = new
        {
            @object = "list",
            data = ids.Select(id => new { id, @object = "model" }).ToList(),
        };

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(response, context.RequestAborted);
    }
}
