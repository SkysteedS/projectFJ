using UnityEngine;

/// <summary>
/// 具体状态（Handing 层叶子）：手雷（Grenade）× 瞄准（Aiming）× 着地（Ground）。
/// 瞄准移动/程序化 IK 由 AimingGroundStateBase 承载；本叶只提供手雷差异：
/// - 枪根/轴点/offset = ctx.GrenadeRoot / ctx.GrenadeAxisPoint（肘部附近）/ ctx.GrenadeAxisOffset；
/// - localRotation = ctx.GrenadeAxisLocalRotation（横握手雷相对肘部轴点的旋转标定）；
/// - AlwaysAimToFront = true：稳定期手雷朝向 = 角色 yaw × 相机 pitch（抛掷偏航占位，后续由落点模块给出）；
/// - 弧线预览：Enter/Exit 同步 GrenadeArcPreview 生命周期，Tick 经 OnPostAimTargetsUpdated 更新。
/// 可调参数/标定值见 PlayerMotionValuesSO。
/// </summary>
public class Grenade_Aiming_Ground_State : AimingGroundStateBase
{
    // 弧线预览：独立工具类（渲染器管理/落点探测/淡入在其内部，见 GrenadeArcPreview）
    readonly GrenadeArcPreview arcPreview = new GrenadeArcPreview();

    protected override Transform AimWeaponRoot(PlayerContext ctx) => ctx.GrenadeRoot;
    protected override Transform AimPivot(PlayerContext ctx) => ctx.GrenadeAxisPoint;
    protected override Vector3 AimAxisOffset(PlayerContext ctx) => ctx.GrenadeAxisOffset;
    protected override Quaternion AimLocalRotation(PlayerContext ctx) => ctx.GrenadeAxisLocalRotation;
    protected override bool AlwaysAimToFront => true;

    public override void Enter(PlayerContext ctx)
    {
        base.Enter(ctx);
        // 弧线预览：进入瞄准后取渲染器并准备世界空间采样与淡入状态
        arcPreview.Enter(ctx);
    }

    public override void Exit(PlayerContext ctx)
    {
        base.Exit(ctx);
        // 退出瞄准：清空弧线预览
        arcPreview.Exit();
    }

    protected override void OnPostAimTargetsUpdated(PlayerContext ctx)
        => arcPreview.Tick(ctx, RigHeld, RigTransitioning);
}
