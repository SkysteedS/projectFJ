/// <summary>
/// 具体状态（Handing 层叶子）：空手（Unarmed）× 正常手部（Normal）× 攀爬（Climbing）。
/// 攀爬族行为由 ClimbingStateBase 整体承载（方案 A）；本叶为纯身份声明
/// （结构一致性 + 未来武器/手部差异扩展点）。参数见 PlayerMotionValuesSO【攀爬】区。
/// </summary>
public class Unarmed_Normal_Climbing_State : NormalClimbingStateBase
{
}
