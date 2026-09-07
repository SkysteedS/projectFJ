/// <summary>
/// 具体状态（Handing 层叶子）：手雷（Grenade）× 正常手部（Normal）× 滞空（Jumping）。
/// 滞空逻辑由 JumpingStateBase 承载（惯性捕获/重力/落地提议/位移/参数写入）；
/// 当前四把武器滞空无差异，本叶为纯身份声明（结构一致性 + 未来武器差异扩展点）。
/// 可调参数见 PlayerMotionValuesSO。
/// </summary>
public class Grenade_Normal_Jumping_State : NormalJumpingStateBase
{
}
