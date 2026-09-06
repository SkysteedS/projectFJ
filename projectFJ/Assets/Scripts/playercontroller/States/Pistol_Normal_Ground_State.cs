using UnityEngine;

/// <summary>
/// 具体状态：手枪（Pistol）× 正常手部（Normal）× 着地（Ground）。
/// 复刻自 Rifle_Normal_Ground_State（rifle 方案直接复制到手枪，行为要点一一对应）：
/// - 旋转：WASD → 相机轴世界朝向，RotateTowards 插值转向（无输入不转向）；
/// - 水平速度：目标 = 手枪（持枪）档位（走 2 / 跑 4 × 输入模长，见 PlayerMotionValuesSO，可调），
///   常态 MoveTowards 恒加速度插值；武器切换（空手↔持枪）后按 Switching Weapon 动画进度做
///   旧→新档位插值（检测不到动画时兜底 weaponSwitchSpeedBlendTime）；
/// - 垂直：着地保持向下压速度；离地（走下边沿）累积重力，越过死区阈值时【提议】切滞空
///   （目标组合 Pistol×Normal×Jumping，请求边见 PlayerControllerScript）；
/// - 位移：OnAnimatorMove 沿用动画根运动水平分量 + 手写垂直分量；
/// - 动画参数：本类只写水平/垂直速度分量；body posture / hand posture / player handing
///   由主体类按当前状态组合每帧同步。
///
/// 程序化 IK（复刻步枪锚点体系，锚点标定值见 PlayerMotionValuesSO）：
/// - 左手：目标 = 手枪根当前位姿 × pistolLeftHandAnchorLocalPosition/Euler（手枪子物体 local）；
/// - 右手：TwoBoneIK target 每帧写入 pistolRightHandAnchorLocalPosition/Euler（Chest 子物体 local）。
///
/// 接入状态：控制器已注册本组合，SlotPistol 进出信号边已接入（空手按 2 拔枪、再按 2 收回）；
/// 手枪模型/挂点/拔收枪动画事件由编辑器装配，装配前 PistolRoot 为空时左手写入会告警跳过。
/// </summary>
public class Pistol_Normal_Ground_State : PlayerStateBase
{
    #region 状态内结构常量（语义见注释；可调参数见 PlayerMotionValuesSO）
    /// <summary>落地过渡计时归零值（计时结束判定）。</summary>
    static readonly float TimerExpired = 0f;
    /// <summary>无下落捕获时的 falling speed 分量（常态分量）。</summary>
    static readonly float NoLandingFallSpeed = 0f;
    #endregion

    float landingFallBlend;     // 落地时捕获的垂直速度（原始 m/s，负值向下；落地过渡期保持）
    float landingBlendTimer;    // 落地过渡保持计时（与动画 body posture 过渡时长对齐）
    bool landingBlendActive;    // 落地过渡是否进行中
    bool leftHandIkWarned;      // 左手 IK 写入无效时只警告一次（防刷屏）
    bool rightHandIkWarned;     // 右手 IK 锚点装配缺失时只警告一次（防刷屏）
    float weaponSwitchBlendTimer;       // 武器切换速度插值兜底计时（动画未检测到时用）
    float weaponSwitchStartSpeed;       // 武器切换插值起点（旧武器档位速度，Enter 捕获）
    bool weaponSwitchBlendActive;       // 武器切换速度插值进行中
    bool weaponSwitchAnimDriven;        // 已检测到切换动画（此后进度与动画 normalizedTime 同步，直到动画退出）

    #region 状态生命周期与移动（Enter/Exit/Tick/旋转/速度/垂直交接）
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

        // 右手手部 IK：脚本是唯一权威写入者——标定值由每帧 WriteRightHandCalibratedPose 无条件维持。
        // 手枪不采用步枪的"切换中段轨迹接管"：拔枪/持枪动画帧直接写标定值；退出（收枪）不还原
        // "进入前原值"——那通常是场景序列化的调试位，还原会在 put 开头（IK 权重混合期）把右手
        // 拉回场景调试位（旧 Write Defaults 问题的同类来源）。
    }

    public override void Tick(PlayerContext ctx)
    {
        float dt = Time.deltaTime;

        // 左手手部 IK 目标实时计算（手枪正常状态每帧）：目标 = 手枪根当前位姿 × 标定锚点，
        // 左手贴握、随武器/动画运行（复刻步枪体系；标定值空时由编辑器标定后生效）
        UpdateLeftHandIkTarget(ctx);
        // 右手手部 IK：每帧写入标定值（脚本唯一权威；退出不还原，保持末帧标定值进入 put 混合）
        WriteRightHandCalibratedPose(ctx);

        RotateTowardMoveDirection(ctx);

        // 水平速度：目标 = 手枪（持枪）档位（走 2 / 跑 4，可调）× 输入模长（模长为准）；
        // 武器切换过渡期做"旧档位 → 新档位"的时间线性插值（与切换动画时长匹配），
        // 常态回到 MoveTowards 恒加速度插值（从 Motion.HorizontalSpeed 续值，无断点）。
        float targetSpeed = (ctx.Input.Run ? ctx.Values.pistolRunSpeed : ctx.Values.pistolWalkSpeed)
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
            speed = ctx.Values.MovePistolSpeed(ctx.Motion.HorizontalSpeed,
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
            ctx.RequestTransition(PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping);
            return;
        }

        WriteAnimatorParams(ctx, speed);
    }
    #endregion

    #region 手部 IK（左手贴握同步 / 右手标定锚点写入）
    /// <summary>
    /// 左手手部 IK 目标实时计算（手枪正常状态，每帧）。
    /// 遵循项目 IK 约定【只更新 transform、不修改 target】：
    /// 经左手 TwoBoneIK 约束（LeftHandConstraint）取其装配固定的 data.target（只读引用），
    /// 每帧只写该 target 的 transform（世界位姿 = 手枪根当前位姿 × 锚点值，
    /// pistolLeftHandAnchorLocalPosition/Euler，见 PlayerMotionValuesSO）。
    /// 若 target 仍装配在武器层级内（武器子物体自动跟随），则不写入（写了会被武器覆盖）。
    /// </summary>
    void UpdateLeftHandIkTarget(PlayerContext ctx)
    {
        Transform pistolRoot = ctx.PistolRoot;
        Transform target = ctx.LeftHandConstraint != null ? ctx.LeftHandConstraint.data.target : null;

        if (pistolRoot == null || target == null)
        {
            if (!leftHandIkWarned)
            {
                leftHandIkWarned = true;
                Debug.LogWarning(
                    $"[LeftHandIK] 同步无效：pistolRoot={(pistolRoot != null ? pistolRoot.name : "null（PlayerControllerScript.Pistol 未指派）")}，" +
                    $"leftHandTarget={(target != null ? target.name : "null（左手 TwoBoneIK 约束 data.target 未装配）")}",
                    ctx.Transform);
            }
            return;
        }

        // 只写 target 的 transform；参考引用（约束装配）不变
        if (target.IsChildOf(pistolRoot)) return;   // 武器子物体：自动跟随，无需写入

        target.SetPositionAndRotation(
            pistolRoot.TransformPoint(ctx.Values.pistolLeftHandAnchorLocalPosition),
            pistolRoot.rotation * Quaternion.Euler(ctx.Values.pistolLeftHandAnchorLocalEuler));
    }

    /// <summary>
    /// 每帧【写入】标定值（Chest 基准局部坐标）——脚本是唯一权威写入者：
    /// target 局部值 = 标定值（pistolRightHandAnchorLocalPosition/Euler，见 PlayerMotionValuesSO）。
    /// 不做 Enter 记录/Exit 还原：还原会把 target 拉回场景序列化的默认位（put 开头可见的"调试位置"）。
    /// </summary>
    void WriteRightHandCalibratedPose(PlayerContext ctx)
    {
        Transform target = ctx.RightHandConstraint != null ? ctx.RightHandConstraint.data.target : null;
        if (target == null)
        {
            if (!rightHandIkWarned)
            {
                rightHandIkWarned = true;
                Debug.LogWarning(
                    "[RightHandIK] 同步无效：右手 TwoBoneIK 约束 data.target 未装配",
                    ctx.Transform);
            }
            return;
        }

        target.localPosition = ctx.Values.pistolRightHandAnchorLocalPosition;
        target.localRotation = Quaternion.Euler(ctx.Values.pistolRightHandAnchorLocalEuler);

        // 逐帧调试日志（rightHandIkFrameDebugLog）：打印「脚本写入后」的值，与帧末日志对比定位覆盖来源
        if (ctx.Values.rightHandIkFrameDebugLog)
        {
            Debug.Log($"[RightHandIK][Pistol] Write  f{Time.frameCount} {target.name} " +
                      $"localPos={target.localPosition:F3} localRot={target.localRotation.eulerAngles:F1} " +
                      $"| calibrated={ctx.Values.pistolRightHandAnchorLocalPosition:F3}&{ctx.Values.pistolRightHandAnchorLocalEuler:F1}");
        }
    }
    #endregion

    #region 位移与动画参数（OnAnimatorMove / WriteAnimatorParams）
    public override void OnAnimatorMove(PlayerContext ctx)
    {
        // 地面：沿用动画根运动（水平）+ 手写垂直分量；
        // 落地过渡期 landingFallBlend 只影响动画下落姿态参数，位移以贴地速度为准。
        Vector3 delta = ctx.Animator.deltaPosition;
        delta.y = ctx.Motion.VerticalVelocity * Time.deltaTime;
        ctx.CharacterController.Move(delta);
    }

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

        var anim = ctx.AnimParams;   // 值缓存写入器：同值跳过 SetFloat
        // 水平面内速度分量：非瞄准时角色朝向移动方向——全速进前后轴（vertical speed），左右轴固定为 0
        anim.SetFloat(PlayerControllerScript.AnimVerticalSpeed, horizontalSpeed);
        anim.SetFloat(PlayerControllerScript.AnimHorizontalSpeed, ctx.Values.nonAimLateralSpeed);
        // 垂直方向速度（原始 m/s，正值向上/负值向下；越界由混合树钳制）：着地为 0，落地过渡期保持捕获值
        anim.SetFloat(PlayerControllerScript.AnimFallingSpeed,
                      landingBlendActive ? landingFallBlend : NoLandingFallSpeed);
    }
    #endregion
}
