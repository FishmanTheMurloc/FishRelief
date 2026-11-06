using Serilog;
using Serilog.Core;
using Serilog.Formatting.Compact;

namespace FishRelief.Gateway;

/// <summary>
/// 日志台账：与运行日志（Log.Logger）完全独立的 Serilog 实例，只写结构化文件（JSON Lines）。
/// 仅记录不执行——台账不参与任何请求判定，也不与运行日志共用滚动删除策略。
/// </summary>
public sealed class AuditLog : IDisposable
{
    private readonly Logger _logger;

    public AuditLog(string path, int retainedFileCountLimit)
    {
        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                new CompactJsonFormatter(),
                path,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedFileCountLimit)
            .CreateLogger();
    }

    /// <summary>记一条请求台账；字段名即 JSON 键，便于后续检索与统计。</summary>
    public void Record(AuditRecord record)
    {
        _logger.ForContext("Event", record.Event)
            .ForContext("RemoteIp", record.RemoteIp)
            .ForContext("Model", record.Model)
            .ForContext("Stream", record.Stream)
            .ForContext("Outcome", record.Outcome)
            .ForContext("StatusCode", record.StatusCode)
            .ForContext("UpstreamStatusCode", record.UpstreamStatusCode)
            .ForContext("ElapsedMs", record.ElapsedMs)
            .Information("{Event}", record.Event);
    }

    public void Dispose() => _logger.Dispose();
}

/// <summary>一次请求的台账记录：只含结构字段，不含消息内容与任何凭证。</summary>
public sealed record AuditRecord(
    string Event,
    string? RemoteIp,
    string? Model,
    bool Stream,
    string Outcome,
    int StatusCode,
    int? UpstreamStatusCode,
    long ElapsedMs);
