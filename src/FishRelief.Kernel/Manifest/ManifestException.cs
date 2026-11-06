namespace FishRelief.Kernel.Manifest;

/// <summary>
/// 清单加载或校验失败。网关对清单采取 fail-closed 策略：不合法即拒绝加载，不做容错修正。
/// </summary>
public sealed class ManifestException : Exception
{
    public ManifestException(string message) : base(message)
    {
    }

    public ManifestException(string message, IReadOnlyList<string> errors)
        : base(message + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
    {
    }
}
