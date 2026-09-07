/// <summary>
/// 具体状态（Handing 层叶子）：空手（Unarmed）× 正常手部（Normal）× 着地（Ground）。
/// 移动逻辑由 NormalGroundStateBase 承载（落地过渡/切枪速度插值/转向/垂直/参数写入）；
/// 空手不写手部 IK → 不覆写 WriteHands（默认空实现，无成员）。
/// 可调参数见 PlayerMotionValuesSO；跳跃/攀爬/切枪由机器信号边裁决
/// （见 PlayerControllerScript.InitStateMachine）。
/// </summary>
public class Unarmed_Normal_Ground_State : NormalGroundStateBase
{
}
