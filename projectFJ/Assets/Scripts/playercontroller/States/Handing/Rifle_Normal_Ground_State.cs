/// <summary>
/// 具体状态（Handing 层叶子）：步枪（Rifle）× 正常手部（Normal）× 着地（Ground）。
/// 移动逻辑由 NormalGroundStateBase 承载；本叶只提供步枪手部 IK 差异：
/// - 左手：目标 = 步枪根当前位姿 × gunLeftHandAnchor*（护木贴握，见 WriteHands）；
/// - 右手：TwoBoneIK target 每帧写 chestRightHandAnchor*（Chest 子物体 local，脚本唯一权威）；
/// - 拔/收枪切换帧由主体类 UpdateRightHandSwitchIkTarget 覆盖为动画轨迹，本类不参与。
/// 可调参数/锚点标定值见 PlayerMotionValuesSO。
/// </summary>
public class Rifle_Normal_Ground_State : NormalGroundStateBase
{
    bool leftHandIkWarned;      // 左手 IK 写入无效时只警告一次（防刷屏）
    bool rightHandIkWarned;     // 右手 IK 锚点装配缺失时只警告一次（防刷屏）

    protected override void WriteHands(PlayerContext ctx)
    {
        WriteLeftHandGripPose(ctx, ctx.RifleRoot,
                              ctx.Values.gunLeftHandAnchorLocalPosition,
                              ctx.Values.gunLeftHandAnchorLocalEuler,
                              "Rifle", ref leftHandIkWarned);
        WriteRightHandCalibratedPose(ctx,
                                     ctx.Values.chestRightHandAnchorLocalPosition,
                                     ctx.Values.chestRightHandAnchorLocalEuler,
                                     "[RightHandIK]", ref rightHandIkWarned);
    }
}
