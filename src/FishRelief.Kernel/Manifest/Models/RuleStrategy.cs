namespace FishRelief.Kernel.Manifest.Models;

/// <summary>
/// 命中策略。当前只实现 ArrayKeys：输入是字符串数组，逐元素做字面量匹配，输出对象数组。
/// 预留扩展点：未来可加入 Keys（单字符串字面量）、Bert（单字符串分类）、ArrayBert（数组逐元素分类）。
/// JSON 值为 camelCase（如 "arrayKeys"）。
/// </summary>
public enum RuleStrategy
{
    ArrayKeys,
}
