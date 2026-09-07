using UnityEngine;

/// <summary>
/// 具体状态（Handing 层叶子）：手雷（Grenade）× 正常手部（Normal）× 着地（Ground）。
/// 移动逻辑由 NormalGroundStateBase 承载；本叶只提供手雷手部 IK 差异：
/// 手雷以右手动作为主——仅右手 TwoBoneIK target 每帧写 grenadeRightHandAnchor*
/// （Chest 子物体 local，脚本唯一权威）；左手不写 IK，左臂由动画驱动。
/// 右手标定值全 0（未标定）时跳过写入并告警一次——避免把 target 错误拉到 Chest 原点。
/// 可调参数/锚点标定值见 PlayerMotionValuesSO。
/// </summary>
public class Grenade_Normal_Ground_State : NormalGroundStateBase
{
    bool rightHandIkWarned;     // 右手 IK 锚点装配缺失时只警告一次（防刷屏）
    bool rightHandCalibWarned;  // 右手 IK 标定值未设置（全 0）时只警告一次（防刷屏）

    protected override void WriteHands(PlayerContext ctx)
    {
        // 标定守卫：约束 target 已装配且位置/欧拉仍全 0 视为未标定，跳过写入并告警一次
        Transform target = ctx.RightHandConstraint != null ? ctx.RightHandConstraint.data.target : null;
        if (target != null
            && ctx.Values.grenadeRightHandAnchorLocalPosition == Vector3.zero
            && ctx.Values.grenadeRightHandAnchorLocalEuler == Vector3.zero)
        {
            if (!rightHandCalibWarned)
            {
                rightHandCalibWarned = true;
                Debug.LogWarning(
                    "[RightHandIK][Grenade] 标定值未设置：grenadeRightHandAnchorLocalPosition/Euler 全 0，已跳过写入（待编辑器标定）",
                    ctx.Transform);
            }
            return;
        }

        WriteRightHandCalibratedPose(ctx,
                                     ctx.Values.grenadeRightHandAnchorLocalPosition,
                                     ctx.Values.grenadeRightHandAnchorLocalEuler,
                                     "[RightHandIK][Grenade]", ref rightHandIkWarned);
    }
}
