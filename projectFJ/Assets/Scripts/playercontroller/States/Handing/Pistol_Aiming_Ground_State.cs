using UnityEngine;

/// <summary>
/// 具体状态（Handing 层叶子）：手枪（Pistol）× 瞄准（Aiming）× 着地（Ground）。
/// 瞄准移动/程序化 IK 由 AimingGroundStateBase 承载；本叶只解析手枪武器数据：
/// 枪根 = ctx.PistolRoot、轴点 = ctx.AimAxisPoint、offset = ctx.PistolAxisOffset；
/// localRotation 恒等（AimLocalRotation 默认）、稳定期用视线远点（AlwaysAimToFront = false）、
/// 无弧线扩展。可调参数/标定值见 PlayerMotionValuesSO（手枪与步枪到轴点距离不同，独立标定）。
/// </summary>
public class Pistol_Aiming_Ground_State : AimingGroundStateBase
{
    protected override Transform AimWeaponRoot(PlayerContext ctx) => ctx.PistolRoot;
    protected override Transform AimPivot(PlayerContext ctx) => ctx.AimAxisPoint;
    protected override Vector3 AimAxisOffset(PlayerContext ctx) => ctx.PistolAxisOffset;
}
