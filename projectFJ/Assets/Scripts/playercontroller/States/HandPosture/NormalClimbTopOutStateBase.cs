/// <summary>
/// HandPosture 层 · 登顶 × 手部 = Normal（默认手部）变体基类。
/// 当前登顶走廊族仅 Normal 一支（持械登顶非法组合），本层为空锚点：
/// 用于保持"所有具体状态类统一为 Handing 层叶子"的结构一致性，
/// 并为未来手部变体提供扩展位。
/// </summary>
public abstract class NormalClimbTopOutStateBase : ClimbTopOutStateBase
{
}
