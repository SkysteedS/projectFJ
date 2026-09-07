using UnityEngine;

/// <summary>
/// HandPosture 层 · 地面 × 手部 = Normal（默认手部）变体基类。
/// 收走 4 个 Normal×Ground 状态（Unarmed/Rifle/Pistol/Grenade）共有的：
/// - 落地过渡（landingFallBlend / landingBlendTimer / landingBlendActive）；
/// - 武器切换速度插值（weaponSwitchBlendTimer / StartSpeed / Active / AnimDriven）；
/// - Enter 公共三段（落地交接捕获 → HandingChanged 消费并启动切枪插值 → 贴地压速）；
/// - Tick 骨架与转向/参数写入/手部 IK 写入辅助。
/// 差异缝：WriteHands（Unarmed 空实现；Rifle/Pistol/Grenade 在 Handing 层覆写，
/// 经本层 IK 写入辅助只提供锚点数据）。
/// 可调参数（速度档位/时长/阈值）见 PlayerMotionValuesSO；档位按 ctx.Handing 查询。
/// </summary>
public abstract class NormalGroundStateBase : GroundStateBase
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

    #region 状态生命周期与移动（Enter/Tick/旋转/速度/垂直交接）
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

        // 武器手部 IK 差异点（Unarmed 空实现；Rifle/Pistol/Grenade 在 Handing 层覆写）
        WriteHands(ctx);

        RotateTowardMoveDirection(ctx);

        // 水平速度：目标 = 按当前手持查询的档位（走/跑 × 输入模长，数值层单点）；
        // 武器切换过渡期做"旧档位 → 新档位"的时间线性插值（与切换动画时长匹配），
        // 常态回到 MoveTowards 恒加速度插值（从 Motion.HorizontalSpeed 续值，无断点）。
        float targetSpeed = ctx.Values.GroundTargetSpeed(ctx.Input.Move.magnitude, ctx.Input.Run, ctx.Handing);
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
            speed = ctx.Values.MoveGroundSpeed(ctx.Motion.HorizontalSpeed,
                                              ctx.Input.Move.magnitude, ctx.Input.Run, dt, ctx.Handing);
        }

        // 垂直：着地贴地 / 离地累积重力（走下边沿）
        float vertical = NextGroundVertical(ctx, ctx.Motion.VerticalVelocity, dt);

        // 写回共享运动槽（水平分量叠加移动方向——无输入沿用最后方向，速度标量插值衰减；
        // 滞空/落地等新状态 Enter 从此读取）
        Vector3 moveDir = MoveDirection(ctx);
        var velocity = ctx.Motion.Velocity;
        velocity.x = moveDir.x * speed;
        velocity.z = moveDir.z * speed;
        velocity.y = vertical;
        ctx.Motion.Velocity = velocity;

        // 离地且垂直速度越过死区 → 提议进滞空（转换条件由请求边裁决；切换后中止本帧写入）
        if (TryRequestAirborneExit(ctx, vertical)) return;

        WriteGroundAnimatorParams(ctx, speed);
    }

    /// <summary>
    /// 武器手部 IK 差异缝（本类每帧 Tick 在转向/速度前调用）：
    /// 默认空实现 = 空手不写 IK；Rifle/Pistol/Grenade 叶子覆写，经本类 IK 写入辅助提供锚点数据。
    /// </summary>
    protected virtual void WriteHands(PlayerContext ctx) { }
    #endregion

    #region 转向与速度辅助
    void RotateTowardMoveDirection(PlayerContext ctx)
    {
        // 无输入不改变朝向
        if (ctx.Input.Move.sqrMagnitude <= ctx.Values.minMoveSqrMagnitude) return;

        Vector3 moveDir = CameraSpaceMoveDir(ctx);
        if (moveDir.sqrMagnitude <= ctx.Values.minMoveSqrMagnitude) return;

        Quaternion target = Quaternion.LookRotation(moveDir, Vector3.up);
        ctx.Transform.rotation = Quaternion.RotateTowards(ctx.Transform.rotation, target,
                                                          ctx.Values.rotateSpeed * Time.deltaTime);
    }

    void WriteGroundAnimatorParams(PlayerContext ctx, float horizontalSpeed)
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

        // 非瞄准三参数：全速进前后轴（vertical speed）、左右轴固定为 0；
        // falling speed：着地为 0，落地过渡期保持捕获值
        WriteNonAimSpeedParams(ctx, horizontalSpeed, landingBlendActive ? landingFallBlend : NoLandingFallSpeed);
    }
    #endregion

    #region 手部 IK 写入辅助（Handing 层只提供锚点数据与告警标志）
    /// <summary>
    /// 右手 TwoBoneIK 标定位写入（Chest 子物体 local）：脚本是唯一权威写入者。
    /// 只写 target local TRS；缺装配只告警一次（warnOnce 由调用方持有）。
    /// debugTag 用于区分武器日志前缀（如 [RightHandIK][Pistol]），逐帧日志由 rightHandIkFrameDebugLog 开关门控。
    /// </summary>
    protected void WriteRightHandCalibratedPose(PlayerContext ctx,
                                                Vector3 anchorLocalPosition, Vector3 anchorLocalEuler,
                                                string debugTag, ref bool warnOnce)
    {
        Transform target = ctx.RightHandConstraint != null ? ctx.RightHandConstraint.data.target : null;
        if (target == null)
        {
            if (!warnOnce)
            {
                warnOnce = true;
                Debug.LogWarning(
                    "[RightHandIK] 同步无效：右手 TwoBoneIK 约束 data.target 未装配",
                    ctx.Transform);
            }
            return;
        }

        target.localPosition = anchorLocalPosition;
        target.localRotation = Quaternion.Euler(anchorLocalEuler);

        // 逐帧调试日志（rightHandIkFrameDebugLog）：打印「脚本写入后」的值，与帧末日志对比定位覆盖来源
        if (ctx.Values.rightHandIkFrameDebugLog)
        {
            Debug.Log($"{debugTag} Write  f{Time.frameCount} {target.name} " +
                      $"localPos={target.localPosition:F3} localRot={target.localRotation.eulerAngles:F1} " +
                      $"| calibrated={anchorLocalPosition:F3}&{anchorLocalEuler:F1}");
        }
    }

    /// <summary>
    /// 左手 TwoBoneIK 贴握写入：目标 = 武器根当前位姿 × 锚点值（武器子物体 local）。
    /// 若 target 仍装配在武器层级内（武器子物体自动跟随）则不写入；
    /// 缺武器根/约束 target 只告警一次（warnOnce 由调用方持有）。
    /// </summary>
    protected void WriteLeftHandGripPose(PlayerContext ctx, Transform weaponRoot,
                                         Vector3 anchorLocalPosition, Vector3 anchorLocalEuler,
                                         string weaponFieldName, ref bool warnOnce)
    {
        Transform target = ctx.LeftHandConstraint != null ? ctx.LeftHandConstraint.data.target : null;
        if (weaponRoot == null || target == null)
        {
            if (!warnOnce)
            {
                warnOnce = true;
                Debug.LogWarning(
                    $"[LeftHandIK] 同步无效：{weaponFieldName}Root=" +
                    $"{(weaponRoot != null ? weaponRoot.name : $"null（PlayerControllerScript.{weaponFieldName} 未指派）")}，" +
                    $"leftHandTarget={(target != null ? target.name : "null（左手 TwoBoneIK 约束 data.target 未装配）")}",
                    ctx.Transform);
            }
            return;
        }

        // 只写 target 的 transform；参考引用（约束装配）不变
        if (target.IsChildOf(weaponRoot)) return;   // 武器子物体：自动跟随，无需写入

        target.SetPositionAndRotation(
            weaponRoot.TransformPoint(anchorLocalPosition),
            weaponRoot.rotation * Quaternion.Euler(anchorLocalEuler));
    }
    #endregion
}
