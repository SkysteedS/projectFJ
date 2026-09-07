using UnityEngine;

/// <summary>
/// BodyPosture 层 · 滞空族基类（Jumping × Normal；四把武器滞空逻辑逐字相同）。
/// 收走滞空族公共流程：
/// - 水平：起跳瞬间【捕获】上一状态（地面）写入的速度向量作为跳跃移动速度——
///   滞空期间方向与大小均保持不变（纯惯性，不做输入操控 / 加速度插值）；
/// - 垂直：手写重力累积；起跳初速度在 Enter 反推 v = √(2·|g|·h)；
/// - 落地：垂直速度 ≤ 落地判定阈值且物理着地时【提议】回 (ctx.Handing, Normal, Ground)
///   （请求边裁决；≤ 阈值防起跳瞬间误判）；
/// - 空中抓墙由机器持久边检测（见 PlayerControllerScript），本族不参与；
/// - 位移：OnAnimatorMove 全量接管（跳跃动画无水平根运动，水平按继承速度、垂直按手写重力）；
/// - 动画参数：经 Player 层 WriteNonAimSpeedParams 写非瞄准三参数。
///
/// 当前无武器差异，HandPosture/Handing 层（NormalJumpingStateBase 与四叶子）为空锚点：
/// 结构一致性占位 + 未来武器差异（如手雷空中投掷）扩展点。
/// 可调参数见 PlayerMotionValuesSO。
/// </summary>
public abstract class JumpingStateBase : PlayerStateBase
{
    #region 滞空状态字段（起跳惯性）
    /// <summary>起跳瞬间捕获的惯性速度（上一状态的速度向量；滞空期间恒定，不随输入变化）。</summary>
    Vector3 horizontalVelocity;
    #endregion

    #region 状态生命周期与滞空运动（Enter/Tick/OnAnimatorMove）
    public override void Enter(PlayerContext ctx)
    {
        // 跨状态交接：水平分量捕获地面状态写入的速度向量（跳跃惯性 = 上一状态速度）；
        // 垂直按进入方式区分——
        // ① 起跳（Jump 信号边，条件 = 着地）：地面写入的贴地速度替换为跳跃初速度；
        // ② 走出边沿（请求边，条件 = 离地且过死区）：保留已累积的下落速度，只捕获水平分量。
        var velocity = ctx.Motion.Velocity;
        horizontalVelocity.x = velocity.x;
        horizontalVelocity.z = velocity.z;
        if (ctx.IsGrounded)
        {
            velocity.y = ctx.Values.JumpSpeed;
        }
        ctx.Motion.Velocity = velocity;
    }

    public override void Tick(PlayerContext ctx)
    {
        float dt = Time.deltaTime;

        // 落地：垂直速度 ≤ 判定阈值（未再上升）且物理着地 → 提议回地面（请求边裁决）
        if (ctx.IsGrounded && ctx.Motion.VerticalVelocity <= ctx.Values.landingVerticalThreshold)
        {
            ctx.RequestTransition(ctx.Handing, PlayerHandPosture.Normal, PlayerBodyPosture.Ground);
            return;
        }

        // 垂直：重力累积
        float vertical = ctx.Values.ApplyGravity(ctx.Motion.VerticalVelocity, dt);

        // 写回共享运动槽（OnAnimatorMove 以它做本帧位移；水平保持继承的惯性速度，不做空中操控）
        var velocity = ctx.Motion.Velocity;
        velocity.x = horizontalVelocity.x;
        velocity.z = horizontalVelocity.z;
        velocity.y = vertical;
        ctx.Motion.Velocity = velocity;

        // 非瞄准三参数写入（Player 层跨族工具）
        WriteNonAimSpeedParams(ctx, horizontalVelocity.magnitude, vertical);
    }

    public override void OnAnimatorMove(PlayerContext ctx)
    {
        // 滞空：跳跃动画无水平根运动，水平位移由脚本按继承速度接管；垂直由手写重力接管
        ctx.CharacterController.Move(ctx.Motion.Velocity * Time.deltaTime);
    }
    #endregion
}
