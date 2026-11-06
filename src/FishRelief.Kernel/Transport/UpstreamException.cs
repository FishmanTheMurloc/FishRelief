namespace FishRelief.Kernel.Transport;

/// <summary>
/// 上游返回非成功状态码。网关应把状态码与响应体原样透传给客户端（响应侧行为像原生服务）。
/// </summary>
public sealed class UpstreamException : Exception
{
    public UpstreamException(int statusCode, string body) : base($"上游返回 {(int)statusCode}")
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; }

    /// <summary>上游响应体（原样字节），供网关透传。</summary>
    public string Body { get; }
}
