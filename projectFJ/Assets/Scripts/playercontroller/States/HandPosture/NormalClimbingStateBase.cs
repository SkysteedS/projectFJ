/// <summary>
/// HandPosture 层 · 攀爬 × 手部 = Normal（默认手部）变体基类。
/// 当前攀爬族仅 Normal 一支（持械攀爬非法组合），本层为空锚点：
/// 用于保持"所有具体状态类统一为 Handing 层叶子"的结构一致性，
/// 并为未来手部变体（如持物攀爬，若能力表放开）提供扩展位。
/// </summary>
public abstract class NormalClimbingStateBase : ClimbingStateBase
{
}
