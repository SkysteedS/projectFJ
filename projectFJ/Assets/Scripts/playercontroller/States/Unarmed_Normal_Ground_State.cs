using UnityEngine;

/// <summary>
/// 具体状态：空手（Unarmed）× 正常手部（Normal）× 着地（Ground）。
/// 地面移动参照 早期原型（Rotate / UpdateSpeed / UpdateVerticalVelocity 地面分支）：
/// - 旋转：WASD → 相机轴世界朝向，RotateTowards 插值转向（无输入不转向）；
/// - 水平速度：目标 = 无武器档位（走 2 / 跑 4 × 输入模长），常态 MoveTowards 恒加速度插值；
///   武器切换（空手↔持枪）后按 Switching Weapon 动画进度做旧→新档位插值（检测不到动画时兜底 weaponSwitchSpeedBlendTime）；
/// - 垂直：着地保持向下压速度；离地（走下边沿）累积重力，越过死区阈值时【提议】切滞空（请求边裁决）；
/// - 位移：OnAnimatorMove 沿用动画根运动水平分量 + 手写垂直分量（早期原型 同款）；
/// - 动画参数：body posture = 0；vertical/horizontal speed = 水平面内前后/左右分量（非瞄准：全速进前后、左右 0）；
///   falling speed = 垂直方向速度（原始 m/s，正值向上/负值向下，越界由混合树钳制；落地过渡期保持捕获值）。
///
/// 数值约定：所有可调参数集中在 PlayerMotionValuesSO（Inspector 可调）；
/// 状态内结构常量集中定义在本类顶部——禁止散落无说明的字面值。
/// 跳跃触发不在本类：Jump 信号边在 Tick 前先知裁决（见 PlayerControllerScript.InitStateMachine）。
/// </summary>
public class Unarmed_Normal_Ground_State : PlayerStateBase
{
    #region 状态内结构常量（语义见注释；可调参数见 PlayerMotionValuesSO）
    /// <summary>落地过渡计时归零值（计时结束判定）。</summary>
    static readonly float TimerExpired = 0f;
    /// <summary>无下落捕获时的 falling speed 分量（常态分量）。</summary>
    static readonly float NoLandingFallSpeed = 0f;
    #endregion

    float landingFallBlend;     // 落地时捕获的垂直速度（原始 m/s，负值向下；落地过渡期保持）
    float landingBlendTimer;    // 落地过渡保持计时（与动画 body posture 过渡时长对齐）
    bool landingBlendActive;    // 落地过渡是否进行中（布尔语义，代替裸 "!= 0" 判断）
    float weaponSwitchBlendTimer;       // 武器切换速度插值兜底计时（动画未检测到时用）
    float weaponSwitchStartSpeed;       // 武器切换插值起点（旧武器档位速度，Enter 捕获）
    bool weaponSwitchBlendActive;       // 武器切换速度插值进行中
    bool weaponSwitchAnimDriven;        // 已检测到切换动画（此后进度与动画 normalizedTime 同步，直到动画退出）

    public override void Enter(PlayerContext ctx)
    {
        // 跨状态交接读取（设计文档 §5 约定：写入 = 状态类 Tick 末尾，读取 = 新状态 Enter）：
        // 滞空→落地：仅"仍在下降"段（低于落地判定阈值）才捕获，供落地过渡保持下落姿态。
        landingBlendActive = ctx.Motion.VerticalVelocity < ctx.Values.landingVerticalThreshold;
        landingFallBlend = landingBlendActive ? ctx.Motion.VerticalVelocity : NoLandingFallSpeed;
        landingBlendTimer = landingBlendActive ? ctx.Values.landingBlendTime : TimerExpired;

        // 武器切换（状态机 SwitchTo 时写入 HandingChanged 标记）：捕获当前速度作为插值起点，
        // 启动"旧武器档位 → 新武器档位"的速度插值（进度与 Switching Weapon 动画 normalizedTime 同步，
        // 动画未检测到时用 weaponSwitchSpeedBlendTime 兜底计时；标记用后即毁）。
        if (ctx.Motion.HandingChanged)
        {
            ctx.Motion.HandingChanged = false;
            weaponSwitchBlendActive = true;
            weaponSwitchAnimDriven = false;
            weaponSwitchBlendTimer = ctx.Values.weaponSwitchSpeedBlendTime;
            weaponSwitchStartSpeed = ctx.Motion.HorizontalSpeed;
        }
        else
        {
            weaponSwitchBlendActive = false;
        }

        // 着地：保持与地面接触的向下压速度
        var velocity = ctx.Motion.Velocity;
        velocity.y = ctx.Values.groundStickSpeed;
        ctx.Motion.Velocity = velocity;
    }

    public override void Tick(PlayerContext ctx)
    {
        float dt = Time.deltaTime;

        RotateTowardMoveDirection(ctx);

        // 水平速度：目标 = 无武器档位（走 2 / 跑 4）× 输入模长（模长为准）；
        // 武器切换过渡期做"旧档位 → 新档位"的时间线性插值（与切换动画时长匹配），
        // 常态回到 MoveTowards 恒加速度插值（从 Motion.HorizontalSpeed 续值，无断点）。
        float targetSpeed = (ctx.Input.Run ? ctx.Values.unarmedRunSpeed : ctx.Values.unarmedWalkSpeed)
                            * ctx.Input.Move.magnitude;
        float speed;
        if (weaponSwitchBlendActive)
        {
            // 武器切换速度插值：进度优先与 Switching Weapon 动画同步（nt = 动画 normalizedTime），
            // 动画播完/退出即收尾（nt↗1 → 速度为新档位）；动画未检测到时用兜底计时。
            float nt;
            if (ctx.TryGetWeaponSwitchAnimProgress(out float animNt))
            {
                weaponSwitchAnimDriven = true;
                nt = animNt;
                if (nt >= 1f) weaponSwitchBlendActive = false;
            }
            else if (weaponSwitchAnimDriven)
            {
                weaponSwitchBlendActive = false;
                nt = 1f;
            }
            else
            {
                weaponSwitchBlendTimer -= dt;
                nt = 1f - Mathf.Clamp01(weaponSwitchBlendTimer / ctx.Values.weaponSwitchSpeedBlendTime);
                if (weaponSwitchBlendTimer <= 0f) weaponSwitchBlendActive = false;
            }
            speed = Mathf.Lerp(weaponSwitchStartSpeed, targetSpeed, Mathf.Clamp01(nt));
        }
        else
        {
            speed = ctx.Values.MoveUnarmedSpeed(ctx.Motion.HorizontalSpeed,
                                                ctx.Input.Move.magnitude, ctx.Input.Run, dt);
        }

        // 垂直：着地贴地 / 离地累积重力（走下边沿）
        float vertical = ctx.Motion.VerticalVelocity;
        vertical = ctx.IsGrounded
            ? ctx.Values.groundStickSpeed
            : ctx.Values.ApplyGravity(vertical, dt);

        // 写回共享运动槽（水平分量叠加移动方向——无输入沿用最后方向，速度标量插值衰减；
        // 滞空/落地等新状态 Enter 从此读取）
        Vector3 moveDir = MoveDirection(ctx);
        var velocity = ctx.Motion.Velocity;
        velocity.x = moveDir.x * speed;
        velocity.z = moveDir.z * speed;
        velocity.y = vertical;
        ctx.Motion.Velocity = velocity;

        // 离地且垂直速度越过死区 → 提议进滞空（转换条件由请求边裁决；切换后中止本帧写入）
        if (!ctx.IsGrounded
            && (vertical < ctx.Values.airborneFallThreshold || vertical > ctx.Values.airborneRiseThreshold))
        {
            ctx.RequestTransition(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping);
            return;
        }

        WriteAnimatorParams(ctx, speed);
    }

    public override void OnAnimatorMove(PlayerContext ctx)
    {
        // 地面：沿用动画根运动（水平）+ 手写垂直分量（早期原型 同款）；
        // 落地过渡期 landingFallBlend 只影响动画下落姿态参数，位移以贴地速度为准。
        Vector3 delta = ctx.Animator.deltaPosition;
        delta.y = ctx.Motion.VerticalVelocity * Time.deltaTime;
        ctx.CharacterController.Move(delta);
    }

    void RotateTowardMoveDirection(PlayerContext ctx)
    {
        // 无输入不改变朝向（早期原型 同款）
        if (ctx.Input.Move.sqrMagnitude <= ctx.Values.minMoveSqrMagnitude) return;

        Vector3 moveDir = CameraSpaceMoveDir(ctx);
        if (moveDir.sqrMagnitude <= ctx.Values.minMoveSqrMagnitude) return;

        Quaternion target = Quaternion.LookRotation(moveDir, Vector3.up);
        ctx.Transform.rotation = Quaternion.RotateTowards(ctx.Transform.rotation, target,
                                                          ctx.Values.rotateSpeed * Time.deltaTime);
    }

    void WriteAnimatorParams(PlayerContext ctx, float horizontalSpeed)
    {
        // 落地过渡：保持捕获的下落速度（动画下落姿态参数）；计时结束释放（对应 早期原型 stance 回 0 后归零）
        if (landingBlendActive)
        {
            landingBlendTimer -= Time.deltaTime;
            if (landingBlendTimer <= TimerExpired)
            {
                landingFallBlend = NoLandingFallSpeed;
                landingBlendTimer = TimerExpired;
                landingBlendActive = false;
            }
        }

        var anim = ctx.Animator;
        // 水平面内速度分量：非瞄准时角色朝向移动方向——全速进前后轴（vertical speed），左右轴固定为 0
        anim.SetFloat(PlayerControllerScript.AnimVerticalSpeed, horizontalSpeed);
        anim.SetFloat(PlayerControllerScript.AnimHorizontalSpeed, ctx.Values.nonAimLateralSpeed);
        // 垂直方向速度（原始 m/s，正值向上/负值向下；越界由混合树钳制）：着地为 0，落地过渡期保持捕获值
        anim.SetFloat(PlayerControllerScript.AnimFallingSpeed,
                      landingBlendActive ? landingFallBlend : NoLandingFallSpeed);
    }
}
