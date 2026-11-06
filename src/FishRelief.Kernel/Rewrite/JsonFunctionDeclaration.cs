using System.Text.Json;
using Microsoft.Extensions.AI;

namespace FishRelief.Kernel.Rewrite;

/// <summary>
/// 按清单声明的参数 schema 构造的函数工具声明。
/// 不做反射、不可调用，只携带"发给模型的工具描述"——网关权威组装的 tools 就是它。
/// 参数 schema 可为空：空表示该工具没有参数，序列化时不写 parameters 键。
/// </summary>
public sealed class JsonFunctionDeclaration : AIFunctionDeclaration
{
    public JsonFunctionDeclaration(string name, string description, JsonElement? parameters)
    {
        Name = name;
        Description = description;
        // 省略参数时用 Undefined 表示"没有 schema"，序列化层据此省略 parameters 键
        JsonSchema = parameters ?? default;
    }

    public override string Name { get; }

    public override string Description { get; }

    public override JsonElement JsonSchema { get; }
}
