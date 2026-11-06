using System.Net;
using System.Text;
using FishRelief.Kernel.Transport;

namespace FishRelief.Tests;

/// <summary>
/// 上游模型清单探测：GET 请求形状、data[].id 解析、上游错误透传、结构非法抛错。
/// HTTP 用假 handler，不发真网络请求。
/// </summary>
public sealed class UpstreamModelCatalogClientTests
{
    [Fact]
    public async Task ListModels_ParsesIdsAndSendsBearerToken()
    {
        string? capturedUrl = null;
        string? capturedAuthorization = null;
        var client = Create(
            """{"object":"list","data":[{"id":"model-b","object":"model"},{"id":"model-a","object":"model"},{"id":""},{"id":"   "}]}""",
            request =>
            {
                capturedUrl = request.RequestUri!.ToString();
                capturedAuthorization = request.Headers.Authorization?.ToString();
            });

        var models = await client.ListModelsAsync();

        Assert.Equal(["model-a", "model-b"], models.OrderBy(m => m, StringComparer.Ordinal));
        Assert.Equal("http://upstream.test/v1/models", capturedUrl);
        Assert.Equal("Bearer sk-test", capturedAuthorization);
    }

    [Fact]
    public async Task ListModels_UpstreamError_ThrowsWithStatusAndBody()
    {
        var client = Create("上游错误体", _ => { }, HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<UpstreamException>(() => client.ListModelsAsync());

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal("上游错误体", exception.Body);
    }

    [Fact]
    public async Task ListModels_MissingDataArray_Throws()
    {
        var client = Create("""{"object":"list"}""", _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync());
    }

    [Fact]
    public async Task ListModels_EmptyCatalog_Throws()
    {
        var client = Create("""{"object":"list","data":[]}""", _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync());
    }

    private static UpstreamModelCatalogClient Create(
        string responseBody,
        Action<HttpRequestMessage> onRequest,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new FakeHandler(responseBody, onRequest, statusCode);
        return new UpstreamModelCatalogClient(
            new HttpMessageInvoker(handler),
            new Uri("http://upstream.test/v1/models"),
            () => "sk-test");
    }

    private sealed class FakeHandler(
        string responseBody,
        Action<HttpRequestMessage> onRequest,
        HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest(request);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
