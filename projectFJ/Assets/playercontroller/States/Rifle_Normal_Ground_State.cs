using UnityEngine;

/// <summary>
/// 具体状态：步枪（Rifle）× 正常手部（Normal）× 着地（Ground）。
/// 移动方式与 test.cs 完全一致（与空手地面状态同款，仅装备不同）：
/// - 旋转：WASD → 相机轴世界朝向，RotateTowards 插值转向（无输入不转向）；
/// - 水平速度：目标 = 步枪（持枪）档位（走 1.5 / 跑 3.5 × 输入模长），常态 MoveTowards 恒加速度插值；
///   武器切换（空手↔持枪）后按 Switching Weapon 动画进度做旧→新档位插值（检测不到动画时兜底 weaponSwitchSpeedBlendTime）；
/// - 垂直：着地保持向下压速度；离地（走下边沿）累积重力，越过死区阈值时【提议】切滞空（请求边裁决）；
/// - 位移：OnAnimatorMove 沿用动画根运动水平分量 + 手写垂直分量（test.cs 同款）；
/// - 动画参数：本类只写水平/垂直速度分量；body posture / hand posture / player handing
///   由主体类按当前状态组合每帧同步（切换瞄准/跳跃即自动切换动画分支）。
/// 瞄准进入由持久边驱动（按住右键），开局进本状态经 SlotRifle 信号边。
/// </summary>
public class Rifle_Normal_Ground_State : PlayerStateBase
{
    #region 状态内结构常量（语义见注释；可调参数见 PlayerMotionValues）
    /// <summary>落地过渡计时归零值（计时结束判定）。</summary>
    static readonly float TimerExpired = 0f;
    /// <summary>无下落捕获时的 falling speed 分量（常态分量）。</summary>
    static readonly float NoLandingFallSpeed = 0f;
    #endregion

    float landingFallBlend;     // 落地时捕获的垂直速度（原始 m/s，负值向下；落地过渡期保持）
    float landingBlendTimer;    // 落地过渡保持计时（与动画 body posture 过渡时长对齐）
    bool landingBlendActive;    // 落地过渡是否进行中
    float weaponSwitchBlendTimer;       // 武器切换速度插值兜底计时（动画未检测到时用）
    float weaponSwitchStartSpeed;       // 武器切换插值起点（旧武器档位速度，Enter 捕获）
    bool weaponSwitchBlendActive;       // 武器切换速度插值进行中
    bool weaponSwitchAnimDriven;        // 已检测到切换动画（此后进度与动画 normalizedTime 同步，直到动画退出）

    public override void Enter(PlayerContext ctx)
    {
        // 跨状态交接：滞空→落地仅"仍在下降"段才捕获，供落地过渡保持下落姿态。
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

        // 水平速度：目标 = 步枪（持枪）档位（走 1.5 / 跑 3.5）× 输入模长（模长为准）；
        // 武器切换过渡期做"旧档位 → 新档位"的时间线性插值（与切换动画时长匹配），
        // 常态回到 MoveTowards 恒加速度插值（从 Motion.HorizontalSpeed 续值，无断点）。
        float targetSpeed = (ctx.Input.Run ? ctx.Values.rifleRunSpeed : ctx.Values.rifleWalkSpeed)
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
            speed = ctx.Values.MoveRifleSpeed(ctx.Motion.HorizontalSpeed,
                                              ctx.Input.Move.magnitude, ctx.Input.Run, dt);
        }

        // 垂直：着地贴地 / 离地累积重力（走下边沿）
        float vertical = ctx.Motion.VerticalVelocity;
        vertical = ctx.IsGrounded
            ? ctx.Values.groundStickSpeed
            : ctx.Values.ApplyGravity(vertical, dt);

        // 写回共享运动槽（水平分量叠加移动方向——无输入沿用最后方向，速度标量插值衰减）
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
            ctx.RequestTransition(PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping);
            return;
        }

        WriteAnimatorParams(ctx, speed);
    }

    public override void OnAnimatorMove(PlayerContext ctx)
    {
        // 地面：沿用动画根运动（水平）+ 手写垂直分量（test.cs 同款）；
        // 落地过渡期 landingFallBlend 只影响动画下落姿态参数，位移以贴地速度为准。
        Vector3 delta = ctx.Animator.deltaPosition;
        delta.y = ctx.Motion.VerticalVelocity * Time.deltaTime;
        ctx.CharacterController.Move(delta);
    }

    void RotateTowardMoveDirection(PlayerContext ctx)
    {
        // 无输入不改变朝向（test.cs 同款）
        if (ctx.Input.Move.sqrMagnitude <= ctx.Values.minMoveSqrMagnitude) return;

        Vector3 moveDir = CameraSpaceMoveDir(ctx);
        if (moveDir.sqrMagnitude <= ctx.Values.minMoveSqrMagnitude) return;

        Quaternion target = Quaternion.LookRotation(moveDir, Vector3.up);
        ctx.Transform.rotation = Quaternion.RotateTowards(ctx.Transform.rotation, target,
                                                          ctx.Values.rotateSpeed * Time.deltaTime);
    }

    void WriteAnimatorParams(PlayerContext ctx, float horizontalSpeed)
    {
        // 落地过渡：保持捕获的下落速度（动画下落姿态参数）；计时结束释放
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
