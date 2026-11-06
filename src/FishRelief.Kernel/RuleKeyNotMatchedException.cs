namespace FishRelief.Kernel;

/// <summary>
/// 客户端上报的键未命中清单中该规则的任何 case。
/// 语义上是 400（客户端错误），由宿主按 <c>RequestRewriter</c> 的客户端错误约定映射成 400；
/// 它不是服务端组装错误，不得并入 <c>PromptAssemblyException</c>（那会落进 500）。
/// </summary>
public sealed class RuleKeyNotMatchedException(string message) : Exception(message)
{
}
