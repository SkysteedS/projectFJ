/// <summary>
/// 具体状态（Handing 层叶子）：手枪（Pistol）× 正常手部（Normal）× 着地（Ground）。
/// 移动逻辑由 NormalGroundStateBase 承载；本叶只提供手枪手部 IK 差异：
/// - 左手：目标 = 手枪根当前位姿 × pistolLeftHandAnchor*（贴握）；
/// - 右手：TwoBoneIK target 每帧写 pistolRightHandAnchor*（Chest 子物体 local，脚本唯一权威）。
/// 可调参数/锚点标定值见 PlayerMotionValuesSO（当前手枪锚点为占位 0，待编辑器标定）。
/// </summary>
public class Pistol_Normal_Ground_State : NormalGroundStateBase
{
    bool leftHandIkWarned;      // 左手 IK 写入无效时只警告一次（防刷屏）
    bool rightHandIkWarned;     // 右手 IK 锚点装配缺失时只警告一次（防刷屏）

    protected override void WriteHands(PlayerContext ctx)
    {
        WriteLeftHandGripPose(ctx, ctx.PistolRoot,
                              ctx.Values.pistolLeftHandAnchorLocalPosition,
                              ctx.Values.pistolLeftHandAnchorLocalEuler,
                              "Pistol", ref leftHandIkWarned);
        WriteRightHandCalibratedPose(ctx,
                                     ctx.Values.pistolRightHandAnchorLocalPosition,
                                     ctx.Values.pistolRightHandAnchorLocalEuler,
                                     "[RightHandIK][Pistol]", ref rightHandIkWarned);
    }
}
