/// <summary>
/// 具体状态（Handing 层叶子）：空手（Unarmed）× 正常手部（Normal）× 登顶（ClimbTopOut）。
/// 登顶走廊族行为由 ClimbTopOutStateBase 整体承载（方案 A）；本叶为纯身份声明
/// （结构一致性 + 未来差异扩展点）。参数见 PlayerMotionValuesSO【登顶】区。
/// </summary>
public class Unarmed_Normal_ClimbTopOut_State : NormalClimbTopOutStateBase
{
}
