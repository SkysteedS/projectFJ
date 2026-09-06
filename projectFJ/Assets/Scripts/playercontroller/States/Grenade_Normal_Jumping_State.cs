using UnityEngine;

/// <summary>
/// 具体状态：手雷（Grenade）× 正常手部（Normal）× 滞空（Jumping）。
/// 复刻自 Pistol_Normal_Jumping_State（原样复刻，无特殊效果）：
/// - 水平：起跳瞬间【捕获】上一状态（地面）写入的速度向量作为跳跃移动速度——
///   滞空期间方向与大小均保持不变（纯惯性，不做输入操控 / 加速度插值）：
///   站立跳继承 0 → 纯向上；行走/奔跑跳出继承当前速度 → 带前冲；
/// - 垂直：手写重力累积；起跳初速度在 Enter 反推 v = √(2·|g|·h)；
/// - 落地：垂直速度 ≤ 落地判定阈值且物理着地时【提议】回（Grenade, Normal, Ground）（请求边裁决）；
/// - 位移：OnAnimatorMove 全量接管——跳跃动画没有水平根运动，水平位移由脚本按继承速度接管，
///   垂直由手写重力接管；
/// - 动画参数：本类只写水平/垂直速度分量；body posture / hand posture / player handing
///   由主体类按当前状态组合每帧同步。
/// 进入方式：手雷地面跳（Jump 信号边）/ 手雷地面走出边沿（请求边）。
/// </summary>
public class Grenade_Normal_Jumping_State : PlayerStateBase
{
    /// <summary>起跳瞬间捕获的惯性速度（上一状态的速度向量；滞空期间恒定，不随输入变化）。</summary>
    Vector3 horizontalVelocity;

    public override void Enter(PlayerContext ctx)
    {
        // 跨状态交接：水平分量捕获上一状态写入的速度向量（跳跃惯性 = 上一状态速度）；
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
            ctx.RequestTransition(PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground);
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

        WriteAnimatorParams(ctx, horizontalVelocity.magnitude, vertical);
    }

    public override void OnAnimatorMove(PlayerContext ctx)
    {
        // 滞空：跳跃动画无水平根运动，水平位移由脚本按继承速度接管；垂直由手写重力接管
        ctx.CharacterController.Move(ctx.Motion.Velocity * Time.deltaTime);
    }

    void WriteAnimatorParams(PlayerContext ctx, float horizontalSpeed, float verticalVelocity)
    {
        var anim = ctx.AnimParams;   // 值缓存写入器：同值跳过 SetFloat
        // 水平面内速度分量：非瞄准时角色朝向移动方向——全速进前后轴（vertical speed），左右轴固定为 0
        anim.SetFloat(PlayerControllerScript.AnimVerticalSpeed, horizontalSpeed);
        anim.SetFloat(PlayerControllerScript.AnimHorizontalSpeed, ctx.Values.nonAimLateralSpeed);
        // 垂直方向速度（原始 m/s，正值向上/负值向下；越界由混合树钳制）
        anim.SetFloat(PlayerControllerScript.AnimFallingSpeed, verticalVelocity);
    }
}
