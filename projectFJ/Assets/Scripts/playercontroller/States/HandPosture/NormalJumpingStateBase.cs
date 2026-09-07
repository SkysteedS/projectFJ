/// <summary>
/// HandPosture 层 · 滞空 × 手部 = Normal（默认手部）变体基类。
/// 当前滞空族仅 Normal 一支（Aiming×Jumping 非法组合），本层为空锚点：
/// 用于保持"所有具体状态类统一为 Handing 层叶子"的结构一致性，
/// 并为未来手部变体（如瞄准跳跃）提供扩展位。
/// </summary>
public abstract class NormalJumpingStateBase : JumpingStateBase
{
}
