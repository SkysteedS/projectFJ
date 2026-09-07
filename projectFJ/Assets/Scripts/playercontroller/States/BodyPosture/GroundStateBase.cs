using UnityEngine;

/// <summary>
/// BodyPosture 层 · 地面族基类（Ground × 各手部变体共用）。
/// 职责：地面族公共计算与位移接管——
/// - 相机相对移动换算（CameraForward / CameraRight / CameraSpaceMoveDir / MoveDirection），
///   自 Player 层回收：Jumping/Climbing/TopOut 族不使用相机轴，故归地面族掌控；
/// - 垂直段（贴地压速 vs ApplyGravity）与"离地越界 → 提议滞空"守卫；
/// - OnAnimatorMove 根运动水平 + 手写垂直（Normal 与 Aiming 两分支共用）。
///
/// 本层不定义 Enter/Tick 模板：Normal 与 Aiming 的流程阶段顺序不同
/// （Normal：IK → 转向 → 速度；Aiming：rig 过渡 → 瞄准目标 → 两轴速度），
/// 由 HandPosture 层（NormalGroundStateBase / AimingGroundStateBase）各自实现。
/// 可调参数见 PlayerMotionValuesSO；提议目标组合经 ctx.Handing 推导（机器为当前组合唯一写入者）。
/// </summary>
public abstract class GroundStateBase : PlayerStateBase
{
    #region 地面族公共计算（相机相对移动/垂直/离地提议）
    /// <summary>摄像机水平前轴（投影到 XZ 平面并归一化；缺主摄像机时回退世界 forward）。</summary>
    protected static Vector3 CameraForward(PlayerContext ctx)
    {
        if (ctx.MainCamera != null)
        {
            var f = Vector3.ProjectOnPlane(ctx.MainCamera.transform.forward, Vector3.up);
            return f.sqrMagnitude > ctx.Values.minMoveSqrMagnitude ? f.normalized : Vector3.forward;
        }
        return Vector3.forward;
    }

    /// <summary>摄像机水平右轴（投影到 XZ 平面并归一化；缺主摄像机时回退世界 right）。</summary>
    protected static Vector3 CameraRight(PlayerContext ctx)
    {
        if (ctx.MainCamera != null)
        {
            var r = Vector3.ProjectOnPlane(ctx.MainCamera.transform.right, Vector3.up);
            return r.sqrMagnitude > ctx.Values.minMoveSqrMagnitude ? r.normalized : Vector3.right;
        }
        return Vector3.right;
    }

    /// <summary>WASD 输入换算为相机基准的世界方向（Normal×Ground 与未来 Ground 族手部变体共用）。</summary>
    protected static Vector3 CameraSpaceMoveDir(PlayerContext ctx)
        => (CameraForward(ctx) * ctx.Input.Move.y + CameraRight(ctx) * ctx.Input.Move.x).normalized;

    /// <summary>
    /// 移动方向（含"松开输入后的平滑衰减"语义）：
    /// 有输入时取相机轴输入方向并记录到 Motion.HorizontalDir；无输入时沿用最后有效方向。
    /// 这样速度标量（Motion.HorizontalSpeed）在松开输入后仍按加速率插值衰减，
    /// 不会被"方向归零 → 速度清零"打断——动画水平速度参数随插值平滑归零，而非一帧跳零。
    /// </summary>
    protected static Vector3 MoveDirection(PlayerContext ctx)
    {
        if (ctx.Input.Move.sqrMagnitude > ctx.Values.minMoveSqrMagnitude)
        {
            Vector3 dir = CameraSpaceMoveDir(ctx);
            ctx.Motion.HorizontalDir = dir;
            return dir;
        }
        return ctx.Motion.HorizontalDir;
    }

    /// <summary>垂直段公共计算：着地贴地压速 / 离地累积重力（地面族两分支共用）。</summary>
    protected static float NextGroundVertical(PlayerContext ctx, float currentVertical, float deltaTime)
        => ctx.IsGrounded ? ctx.Values.groundStickSpeed : ctx.Values.ApplyGravity(currentVertical, deltaTime);

    /// <summary>
    /// 离地越界守卫 + 提议滞空：着地假失/走下边沿后垂直速度越过死区阈值时，
    /// 请求切 (ctx.Handing, Normal, Jumping)（请求边裁决）。命中返回 true，Tick 应中止本帧后续写入。
    /// 与机器注册的"走出边沿进入滞空"请求边条件收敛为同源判定（见 PlayerControllerScript.InitStateMachine）。
    /// </summary>
    protected static bool TryRequestAirborneExit(PlayerContext ctx, float vertical)
    {
        if (!ctx.IsGrounded
            && (vertical < ctx.Values.airborneFallThreshold || vertical > ctx.Values.airborneRiseThreshold))
        {
            ctx.RequestTransition(ctx.Handing, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping);
            return true;
        }
        return false;
    }
    #endregion

    #region 地面位移接管（根运动水平 + 手写垂直；Normal/Aiming 共用）
    /// <summary>
    /// 地面：沿用动画根运动（水平）+ 手写垂直分量；
    /// 落地过渡只影响动画下落姿态参数，位移以贴地速度为准。
    /// </summary>
    public override void OnAnimatorMove(PlayerContext ctx)
    {
        Vector3 delta = ctx.Animator.deltaPosition;
        delta.y = ctx.Motion.VerticalVelocity * Time.deltaTime;
        ctx.CharacterController.Move(delta);
    }
    #endregion
}
