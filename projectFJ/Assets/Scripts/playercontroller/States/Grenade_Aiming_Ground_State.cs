using UnityEngine;
using UnityEngine.Animations.Rigging;

/// <summary>
/// 具体状态：手雷（Grenade）× 瞄准（Aiming）× 着地（Ground）。
/// 复刻自 Pistol_Aiming_Ground_State，并按手雷投掷语义做差异：
/// - 轴点改放肘部：手雷根（ctx.GrenadeRoot）挂到 ctx.GrenadeAxisPoint 下，
///   offset / localRotation 独立标定（grenadeAimAxisOffset / grenadeAimAxisLocalEuler，横握姿态）；
/// - 稳定期手雷朝向方向 = 角色 yaw × 相机 pitch（GrenadeFrontTarget），抛掷偏航由后续落点模块给出；
/// - 弧线预览已接入（初速 = 手雷 BulletManage.bulletData.bulletInitialSpeed）。行为要点：
/// - 旋转：始终朝向相机水平前方（look 改变相机朝向时角色随之旋转；SmoothDampAngle 阻尼追角，
///   参数 aimRotateSmoothTime / aimRotateMaxSpeed；无移动输入也持续追踪——"相机朝哪、角色朝哪"）；
/// - 速度：瞄准锁定行走档（不接受奔跑，Shift 无效：跑 + 瞄准表现不可用）；档位速度平滑后，
///   投影到相机前/右轴得到前后/左右目标，两轴各自 MoveTowards 平滑（aimAcceleration），供 2D 混合树（horizontal/vertical speed）；
/// - 垂直：着地保持向下压速度；瞄准中走下边沿 → 累积重力，过死区后【提议】
///   （Grenade, Normal, Jumping）（请求边裁决——瞄准状态没有滞空组合，切过去即取消瞄准）；
/// - 位移：OnAnimatorMove 沿用动画根运动（2D 混合树）+ 手写垂直分量（早期原型 同款）；
/// - 动画参数：本类只写水平/垂直速度分量；body/hand/handing 由主体类按状态组合同步
///   （本状态 Hand=Aiming → hand posture=1 驱动瞄准动画分支）。
///
/// 程序化 IK 接管（复刻 rifle/pistol 的轴点体系，轴点改放在手肘附近）：
/// - 轴点（PlayerControllerScript.grenadeAimAxisPoint）由你在场景放置在手肘附近并拖入，
///   是手雷根旋转的俯仰不变点；代码对轴点只读，绝不写它的父级、位置或旋转。
/// - 进入瞄准：手雷根设为轴点的子级（只改手雷根的父级），过渡期内把 local 平滑修正到
///   grenadeAimAxisOffset（offset 即手雷根作为轴点子级时的 localPosition；手雷与步枪到轴点距离不同，独立字段）。
/// - 瞄准全程：每帧写入 手雷根.localPosition = grenadeAimAxisOffset、localRotation = grenadeAimAxisLocalEuler
///   （横握手雷需基于轴点的旋转标定；步枪/手枪瞄准姿态竖直、localRotation 恒等即可），
///   因此 Inspector 调整 grenadeAimAxisOffset / LocalEuler 立即生效；轴点旋转由 TickAxisRotation 程序化推进
///   （PlayerControllerScript.LateUpdate 应用），手雷根作为子级自动跟随，不使用 Multi-Aim 约束。
/// - 双手：进入瞬间捕获"腕-枪"相对位姿，每帧按手雷根当前位姿推算并写入左右手 ChainIK target。
/// - 退出：把手雷根设回进入前父级（优先 RightHandWrist）并恢复 local TRS，交还动画系统。
/// - 弧线预览（真实抛物线 + 地形采样）已接入；抛掷偏航（让真实落点对准视线射线）仍待后续接入。
///   当前稳定期手雷朝向方向 = 角色 yaw × 相机 pitch（GrenadeFrontTarget 占位），只让手肘传递俯仰。
/// </summary>
public class Grenade_Aiming_Ground_State : PlayerStateBase
{
    #region 状态内结构常量（语义见注释；可调参数见 PlayerMotionValuesSO）
    /// <summary>着地（非下落）时 falling speed 分量的常态值。</summary>
    static readonly float NoFallingSpeed = 0f;
    /// <summary>过渡时长下限（s）：防"过渡时长为 0"导致除零/瞬间完成（Inspector 输入 0 或负值时兜底）。</summary>
    static readonly float MinRigTransitionDuration = 0.001f;
    /// <summary>瞄准方向平方长度下限：低于该值视为方向无效（轴点与瞄准点重合时跳过旋转）。</summary>
    static readonly float MinAimDirSqr = 1e-6f;
    /// <summary>无轴点时的回退基准高度（m）：以角色根上方该高度近似轴点（仅枪身前目标回退路径）。</summary>
    static readonly float FallbackAxisHeight = 1.2f;
    /// <summary>相机水平分量下限：相机近垂直（朝天/朝地）时回退纯角色 forward，防 NaN。</summary>
    static readonly float MinCameraHorizLen = 1e-4f;
    /// <summary>弧线折角圆滑顶点数（LineRenderer.numCornerVertices）。</summary>
    static readonly int ArcCornerVertices = 4;
    /// <summary>弧线端点圆帽顶点数（LineRenderer.numCapVertices）。</summary>
    static readonly int ArcCapVertices = 4;
    /// <summary>弧线显示采样点数下限（防 positionCount < 2）。</summary>
    static readonly int MinArcSampleCount = 2;
    /// <summary>落点粗采样步长（s）：先稀疏探测"接近地面"区间，再做细步精化，减少每帧垂直射线数。</summary>
    static readonly float ArcCoarseStep = 0.1f;
    /// <summary>高空跳过探测的保守余量（m）：采样点高于已知地面超过"探测范围 + 余量"时跳过垂直射线。</summary>
    static readonly float ArcProbeSkipMargin = 1f;
    /// <summary>探测最大步数下限（防 Inspector 误配 0/负值）。</summary>
    static readonly int MinDetectSteps = 1;
    /// <summary>显示采样点数上限（防 Inspector 误填超大值撑爆缓存）。</summary>
    static readonly int ArcDisplayMaxSamples = 256;
    #endregion

    float currentSpeed;         // 档位速度（走/跑 × 输入模长，跨帧平滑；瞄准轴投影的基准）
    float aimVerticalSpeed;     // 瞄准轴：相机前后分量（平滑状态跨帧保持）
    float aimHorizontalSpeed;   // 瞄准轴：相机左右分量（平滑状态跨帧保持）
    float aimYaw;               // 瞄准朝向（平滑后的角色 yaw，度；SmoothDampAngle 状态跨帧保持）
    float aimYawVelocity;       // 瞄准朝向的当前角速度（°/s；SmoothDampAngle 内部状态）
    float aimAlignTimer;        // 进入对齐阶段剩余时长（>0 时用高响应平滑时间快速到位）

    // —— 程序化 IK 接管（轴点位置只读；旋转由 PlayerControllerScript.LateUpdate 程序化覆盖；手雷根为轴点子级；双手由手雷根反推）——
    Transform rigRoot;                  // 手雷根（ctx.GrenadeRoot）
    Transform rigPivot;                 // 轴点（ctx.GrenadeAxisPoint；本类不写它的 transform，旋转覆盖在主体类 LateUpdate）
    Vector3 rigOffset;                  // 手雷根作为轴点子级时的 localPosition（ctx.GrenadeAxisOffset）
    bool rigHeld;                       // true = 瞄准 IK 接管中（手雷根已切到轴点下）
    Transform rigSavedParent;           // 进入前父级（优先 ctx.RightHandWrist）
    Vector3 rigSavedLocalPosition;      // 进入前 local TRS（退出时恢复，交还动画系统）
    Quaternion rigSavedLocalRotation;
    Vector3 rigSavedLocalScale;
    Vector3 rigStartLocalPosition;      // 过渡起点 local（切父后保持进入瞬间世界位姿）
    Quaternion rigStartLocalRotation;
    float rigTransitionRemaining;       // 过渡剩余时间；>0 = 仍在进瞄准插值阶段
    float rigTransitionDuration;        // 本次过渡总时长（进入时锁定）

    #region 弧线预览（状态字段）
    LineRenderer arcRenderer;           // 弧线预览渲染器（手雷根子物体，场景装配；状态内 Get）
    Vector3[] arcSamplePoints = new Vector3[ArcDisplayMaxSamples];   // 显示采样缓存（防每帧 GC）
    BulletManage bulletManage;          // 投掷初速数据源（手雷上的 BulletManage；bulletData.bulletInitialSpeed）
    bool bulletDataWarned;              // 初速数据缺失时只告警一次
    float arcShowTimer;                 // 显示延迟计时（进入瞄准后开始倒计时）
    float arcFadeAlpha;                 // 当前弧线 alpha（淡入推进 0..1）
    Gradient arcBaseGradient;           // 进入时缓存的用户配色（淡入只缩 alpha，不动色相）
    Gradient arcFadeGradient = new Gradient();   // 复用实例，避免每帧 alloc
    #endregion

    // 头/胸引导沿用瞄准约定：目标 = 视线远点（相机 forward × aimMissPointDistance）；
    // 手雷朝向（轴点）方向见 UpdateAimTargets / GrenadeFrontTarget：稳定期占位 = 角色 yaw × 相机 pitch，
    // 抛掷偏航待弧线落点模块给出（当前弧线预览已接入真实抛物线 + 地形采样）。

    // 进入瞬间捕获的"腕-枪"常量位姿（手雷根空间 local）；瞄准全程据此推算 ChainIK target 的世界位姿
    bool rightWristCaptured;
    Vector3 rightWristLocalPos;
    Quaternion rightWristLocalRot;
    bool leftWristCaptured;
    Vector3 leftWristLocalPos;
    Quaternion leftWristLocalRot;

    // 轴点当前世界旋转（状态类确定性推进：RotateTowards 限速后写入 ctx.AxisPointRotation，
    // LateUpdate 应用它、双手 target 也用它推算——三者同帧同值，消除"枪先转、手后算"错位穿模）
    Quaternion rigAxisRotation;

    // 手-枪锁定策略：
    // 进入瞬间一次性捕获（手立即贴枪，连续无跳，握位=进入时刻动画混合位）；
    // 等 aimHandCaptureTime（> 动画 crossfade）后【一次性】重捕获"动画稳态握位"，
    // 并在 aimHandRelockBlendTime 内平滑过渡（手在枪上从混合握位滑到稳态握位，无跳无脱手）。
    // 注意：不可在 crossfade 期间每帧重捕获——每帧重捕获会让 target=动画腕位，
    // 手被钉在动画位置（脱离程序化枪位）→ 手/枪分离观感（上一版已踩坑）。
    bool rigHandLocked;
    float rigRelockRemaining;
    float rigRelockBlendRemaining;
    float rigRelockBlendDuration;
    Vector3 rightWristPrevPos;        // 重捕获前的旧握位快照（平滑过渡起点）
    Quaternion rightWristPrevRot;
    Vector3 leftWristPrevPos;
    Quaternion leftWristPrevRot;

    #region 状态生命周期与瞄准移动（Enter/Exit/Tick）
    public override void Enter(PlayerContext ctx)
    {
        // 接手上一状态的水平速度作为当前档位速度基准（接地气不突兀）
        currentSpeed = ctx.Motion.HorizontalSpeed;
        aimVerticalSpeed = 0f;
        aimHorizontalSpeed = 0f;

        // 瞄准朝向初值 = 当前角色朝向（yaw），避免从 0° 起算；进入对齐阶段计时启动。
        // 进入瞄准不做瞬时写入旋转——改为"两段式插值"：前 aimAlignTime 用高响应平滑时间
        // （aimAlignSmoothTime，快速但平滑地转向相机方向），之后恢复常规瞄准跟随参数。
        // 兼顾：① 无瞬转/无卡顿；② 肩位枢轴扫动被压缩在对齐窗口内，混合端点尽早稳定。
        Vector3 fwd = ctx.Transform.forward;
        aimYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        aimYawVelocity = 0f;
        aimAlignTimer = ctx.Values.aimAlignTime;

        // 程序化 IK 接管：捕获腕-枪相对位姿 → 手雷根切到轴点下 → 启动过渡
        BeginAimRig(ctx);

        // 进入即设置瞄准引导目标（首帧目标位置正确，约束无"从旧位置追过来"的过程；
        // 瞄准方向=视线远点，无跳变平滑字段）
        UpdateAimTargets(ctx);

        // 弧线预览渲染器（手雷根子物体）：进入瞄准时取引用并准备世界空间采样写入
        arcRenderer = ctx.GrenadeRoot != null ? ctx.GrenadeRoot.GetComponentInChildren<LineRenderer>(true) : null;
        // 投掷初速数据源：手雷上的 BulletManage（不把初速放进运动数值资产）
        bulletManage = ctx.GrenadeRoot != null ? ctx.GrenadeRoot.GetComponentInChildren<BulletManage>(true) : null;
        if (arcRenderer != null)
        {
            arcRenderer.useWorldSpace = true;
            arcRenderer.numCornerVertices = ArcCornerVertices;   // 折角圆滑（部分版本不在 Inspector 暴露，脚本统一设置）
            arcRenderer.numCapVertices = ArcCapVertices;         // 端点圆帽
            arcRenderer.positionCount = 0;
            arcRenderer.enabled = false;   // 过渡/延迟期间先不显示
            arcBaseGradient = arcRenderer.colorGradient;
            arcShowTimer = ctx.Values.grenadeArcShowDelay;
            arcFadeAlpha = 0f;
        }
    }

    public override void Exit(PlayerContext ctx)
    {
        // 退出瞄准：关闭轴点旋转程序化覆盖，手雷根设回进入前父级并恢复 local TRS；轴点全程不动，混合交给动画系统
        ctx.AimRigActive = false;
        RestoreAimRig(ctx);

        // 退出瞄准：清空弧线预览
        if (arcRenderer != null)
        {
            arcRenderer.positionCount = 0;
            arcRenderer.enabled = false;
            arcRenderer = null;
            arcBaseGradient = null;
            arcFadeAlpha = 0f;
        }
    }

    public override void Tick(PlayerContext ctx)
    {
        float dt = Time.deltaTime;

        // 过渡期内把手雷根 local 平滑修正到 offset；过渡结束后每帧持续应用 offset（改值即时生效）
        TickAimRigTransition(ctx);

        // 每帧把头/胸约束的目标移到相机瞄准点（手雷朝向方向由 GunAimPoint 驱动轴点旋转，见 TickAxisRotation）
        UpdateAimTargets(ctx);

        // 抛物线解析 + 地形采样 → 写入弧线预览
        UpdateArcPreview(ctx);

        RotateTowardCameraForward(ctx);

        // 档位速度平滑（瞄准锁定行走档：不接受奔跑输入，Shift 无效）
        currentSpeed = ctx.Values.MoveAimSpeed(currentSpeed, ctx.Input.Move.magnitude, dt);

        // WASD 投影到相机前/右轴（越肩视角：不转向，动画 2D 混合树表现方向）
        Vector3 camForward = CameraForward(ctx);
        Vector3 camRight = CameraRight(ctx);
        Vector3 moveDir = camForward * ctx.Input.Move.y + camRight * ctx.Input.Move.x;
        if (moveDir.sqrMagnitude > ctx.Values.minMoveSqrMagnitude)
        {
            moveDir.Normalize();
        }

        float targetVertical = Vector3.Dot(moveDir, camForward) * currentSpeed;
        float targetHorizontal = Vector3.Dot(moveDir, camRight) * currentSpeed;
        aimVerticalSpeed = Mathf.MoveTowards(aimVerticalSpeed, targetVertical, ctx.Values.aimAcceleration * dt);
        aimHorizontalSpeed = Mathf.MoveTowards(aimHorizontalSpeed, targetHorizontal, ctx.Values.aimAcceleration * dt);

        // 垂直：着地贴地 / 离地累积重力（瞄准中走下边沿）
        float vertical = ctx.Motion.VerticalVelocity;
        vertical = ctx.IsGrounded
            ? ctx.Values.groundStickSpeed
            : ctx.Values.ApplyGravity(vertical, dt);

        // 写回共享运动槽（水平 = 两瞄准轴合成的相机空间速度；供滞空继承）
        var velocity = ctx.Motion.Velocity;
        velocity = camForward * aimVerticalSpeed + camRight * aimHorizontalSpeed;
        velocity.y = vertical;
        ctx.Motion.Velocity = velocity;

        // 离地且垂直速度越过死区 → 提议（手雷正常手部滞空，请求边裁决；瞄准无滞空组合，切换即取消瞄准）
        if (!ctx.IsGrounded
            && (vertical < ctx.Values.airborneFallThreshold || vertical > ctx.Values.airborneRiseThreshold))
        {
            ctx.RequestTransition(PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping);
            return;
        }

        WriteAnimatorParams(ctx);
    }

    #endregion

    #region 弧线预览（弹道解析与渲染管理）
    /// <summary>
    /// 抛物线解析 + 地形采样（弧线预览）：
    /// 起点 = 轴点 + rigAxisRotation × local offset（暂不考虑手雷 localRotation）；
    /// 落点探测 = 粗采样（ArcCoarseStep）定位落地区间 → Values.grenadeArcDetectTimeStep 精化截断；
    /// 找到真实飞行终点后，再按
    /// PlayerMotionValuesSO.grenadeArcSampleCount 在 0..落点时间 间均匀重采样，点数只影响平滑度。
    /// 初速 = 手雷 BulletManage.bulletData.bulletInitialSpeed，重力沿用 Values.gravity。
    /// </summary>
    void UpdateArcPreview(PlayerContext ctx)
    {
        if (arcRenderer == null || !rigHeld || rigPivot == null) return;

        // 显示节奏：手雷根过渡期与短暂延迟内不绘制（避免手臂尚未到位时弧线乱摆/突兀出现）
        if (rigTransitionRemaining > 0f)
        {
            arcRenderer.enabled = false;
            return;
        }
        if (arcShowTimer > 0f)
        {
            arcShowTimer -= Time.deltaTime;
            arcRenderer.enabled = false;
            return;
        }

        // 起点：轴点位置 + 当前确定性旋转 × local offset
        Vector3 origin = rigPivot.position + rigAxisRotation * rigOffset;

        // 初速方向：轴点瞄准轴（aimAxisNegZ=true → 轴点 -Z 指向瞄准点）
        Vector3 dir = ctx.Values.aimAxisNegZ
            ? -(rigAxisRotation * Vector3.forward)
            : rigAxisRotation * Vector3.forward;
        float speed = 0f;
        if (bulletManage != null && bulletManage.bulletData != null)
        {
            speed = bulletManage.bulletData.bulletInitialSpeed;
        }
        float g = -ctx.Values.gravity;   // gravity 为负（向下），此处取正数幅度
        if (speed <= 0f && !bulletDataWarned)
        {
            bulletDataWarned = true;
            Debug.LogWarning(
                "[GrenadeArc] 未取得投掷初速：请在手雷上挂 BulletManage 并指派 bulletData（bulletInitialSpeed > 0）",
                ctx.Transform);
        }
        if (speed <= 0f || dir.sqrMagnitude < MinAimDirSqr)
        {
            arcRenderer.positionCount = 0;
            arcRenderer.enabled = false;
            return;
        }
        dir.Normalize();

        // 1) 落点探测（粗采样定位落地区间 → 细步精化截断）：
        //    相比全程细步采样，显著减少每帧垂直射线数量，见 TryFindArcLanding
        bool landed = false;
        Vector3 landPoint = default;
        float endTime = ArcCoarseStep * Mathf.Max(MinDetectSteps, ctx.Values.grenadeArcDetectMaxSteps);
        if (TryFindArcLanding(ctx, origin, dir, speed, g, out float landTime, out landPoint))
        {
            landed = true;
            endTime = landTime;
        }

        // 2) 显示采样：按调试参数在 0..endTime 间均匀取点（数量 = 平滑度）
        int count = Mathf.Clamp(ctx.Values.grenadeArcSampleCount, MinArcSampleCount, ArcDisplayMaxSamples);
        arcSamplePoints[0] = origin;
        for (int i = 1; i < count; ++i)
        {
            float tt = endTime * (i / (float)(count - 1));
            arcSamplePoints[i] = SampleArcPoint(origin, dir, speed, g, tt);
        }
        if (landed)
        {
            arcSamplePoints[count - 1] = landPoint;   // 终点钉在地面命中点
        }

        arcRenderer.positionCount = count;
        arcRenderer.SetPositions(arcSamplePoints);

        // 淡入：alpha 从 0 平滑升到 1（时长 = Values.grenadeArcFadeInTime），只缩透明度不改用户配色
        arcRenderer.enabled = true;
        if (arcFadeAlpha < 1f)
        {
            float fadeTime = Mathf.Max(MinRigTransitionDuration, ctx.Values.grenadeArcFadeInTime);
            arcFadeAlpha = Mathf.Min(1f, arcFadeAlpha + Time.deltaTime / fadeTime);
            ApplyArcAlpha(arcFadeAlpha);
        }
    }

    /// <summary>解析抛物线采样点：p(t) = origin + dir·v·t + ½g·t²（向下）。探测与显示共用。</summary>
    static Vector3 SampleArcPoint(Vector3 origin, Vector3 dir, float speed, float g, float t)
        => origin + dir * (speed * t) + Vector3.down * (0.5f * g * t * t);

    /// <summary>从采样点上方下投垂直射线，返回地面命中点；无命中返回 false。</summary>
    static bool ProbeGroundBelow(PlayerContext ctx, Vector3 p,
                                 float probeUp, float probeDist, out Vector3 ground)
    {
        ground = default;
        Vector3 probeOrigin = p + Vector3.up * probeUp;
        if (Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit,
                            probeDist, ctx.Values.aimRayMask))
        {
            ground = hit.point;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 落点探测（优化版）：
    /// 1) 起点下方探测一次，作为"已知地面高度"基准；
    /// 2) 按 ArcCoarseStep 粗采样；采样点远高于已知地面时直接跳过垂直射线（高抛中段零查询）；
    /// 3) 粗采样命中"低于地面 + 容差"后，用 Values.grenadeArcDetectTimeStep 在上一个粗采样点
    ///    与命中点之间精化出首个截断点。
    /// </summary>
    bool TryFindArcLanding(PlayerContext ctx, Vector3 origin, Vector3 dir, float speed, float g,
                           out float landTime, out Vector3 landPoint)
    {
        landTime = 0f;
        landPoint = default;

        float probeUp = ctx.Values.grenadeArcGroundProbeUp;
        float probeDist = ctx.Values.grenadeArcGroundProbeDistance;
        float tolerance = ctx.Values.grenadeArcLandingTolerance;
        int maxCoarse = Mathf.Max(MinDetectSteps, ctx.Values.grenadeArcDetectMaxSteps);
        float skipThreshold = probeUp + probeDist + ArcProbeSkipMargin;

        // 基准地面：起点正下方探测一次
        bool groundKnown = false;
        float lastGroundY = origin.y;
        if (ProbeGroundBelow(ctx, origin, probeUp, probeDist, out Vector3 originGround))
        {
            groundKnown = true;
            lastGroundY = originGround.y;
        }

        for (int s = 1; s <= maxCoarse; ++s)
        {
            float t = s * ArcCoarseStep;
            Vector3 p = SampleArcPoint(origin, dir, speed, g, t);

            // 高空跳过：当前点远高于已知地面，垂直射线必然够不到地面
            if (groundKnown && p.y - lastGroundY > skipThreshold) continue;

            if (!ProbeGroundBelow(ctx, p, probeUp, probeDist, out Vector3 ground)) continue;
            groundKnown = true;
            lastGroundY = ground.y;

            if (p.y > ground.y + tolerance) continue;

            // 精化：在上一个粗采样点与当前命中点之间按细步长找首个低于地面的点
            float fineStart = Mathf.Max(0f, t - ArcCoarseStep);
            float fineStep = Mathf.Max(MinRigTransitionDuration, ctx.Values.grenadeArcDetectTimeStep);
            for (float tt = fineStart; tt <= t + fineStep; tt += fineStep)
            {
                Vector3 ps = SampleArcPoint(origin, dir, speed, g, tt);
                if (ProbeGroundBelow(ctx, ps, probeUp, probeDist, out Vector3 gs)
                    && ps.y <= gs.y + tolerance)
                {
                    landTime = tt;
                    landPoint = gs;
                    return true;
                }
            }

            // 兜底：精化未命中时用粗采样命中点本身
            landTime = t;
            landPoint = ground;
            return true;
        }
        return false;
    }

    /// <summary>把用户配色按当前 alpha 写入渲染器：淡入只缩 alpha 通道，不动色相/宽度曲线。</summary>
    void ApplyArcAlpha(float alpha)
    {
        if (arcBaseGradient == null || arcRenderer == null) return;

        arcFadeGradient.mode = arcBaseGradient.mode;
        GradientColorKey[] colors = arcBaseGradient.colorKeys;
        GradientAlphaKey[] alphas = arcBaseGradient.alphaKeys;
        for (int i = 0; i < alphas.Length; ++i)
        {
            alphas[i].alpha *= alpha;
        }
        arcFadeGradient.SetKeys(colors, alphas);
        arcRenderer.colorGradient = arcFadeGradient;
    }
    #endregion

    #region 瞄准目标与轴点旋转（约束目标位置、确定性旋转推进）
    /// <summary>
    /// 瞄准引导目标更新：
    /// 头/胸的瞄准方向【统一 = 视线远点】（相机中心 forward × aimMissPointDistance 处的点）；
    /// 手雷朝向目标（GunAimPoint）：过渡与稳定期统一 = 角色 yaw + 相机 pitch 的角色身前远点
    /// （GrenadeFrontTarget）——手雷只把手肘轴点的俯仰作为输入，偏航由后续弧线/落点模块给出；
    /// 弧线接入前该占位保证轴点俯仰连续，不追视线 yaw。
    /// 头/胸的瞄准方向仍指向视线远点；命中检测保留作准星/调试（LastAimHit/LastAimPoint）。
    /// 只移动目标对象 position，不改约束引用（约束装配即定，与本项目程序化 IK 目标约定一致）；
    /// 约束 weight 由动画参数经 SetIKweight 回写，本方法不参与。
    /// </summary>
    void UpdateAimTargets(PlayerContext ctx)
    {
        Camera cam = ctx.MainCamera;
        if (cam == null) return;

        // 准星判定射线：仅记录命中信息（调试/未来子弹系统），不再决定瞄准方向
        var ray = new Ray(cam.transform.position, cam.transform.forward);
        bool hit = Physics.Raycast(ray, out RaycastHit hitInfo,
                                   ctx.Values.aimRayDistance, ctx.Values.aimRayMask);

        // 瞄准方向目标 = 视线远点（统一、连续；近物命中时与准星的微小视差偏移由后续弹道特效掩盖）
        Vector3 lookPoint = cam.transform.position + cam.transform.forward * ctx.Values.aimMissPointDistance;

        // 记录本帧瞄准信息（Scene 视图 Gizmos 调试：绿=命中 / 黄=未命中远点）
        ctx.LastAimPoint = hit ? hitInfo.point : lookPoint;
        ctx.LastAimValid = true;
        ctx.LastAimHit = hit;

        // 手雷朝向专用瞄准点（弧线/偏航求解接入前的占位）：统一用 GrenadeFrontTarget
        // （角色 yaw × 相机 pitch），轴点只做俯仰传递；PlayerControllerScript.LateUpdate 据此覆盖轴点旋转。
        Vector3 gunTarget = GrenadeFrontTarget(ctx);
        ctx.GunAimPoint = gunTarget;

        // 轴点旋转确定性推进：必须【先】于双手推算——手部 target 与 LateUpdate 写入轴点的
        // 旋转使用同一值（ctx.AxisPointRotation），消除"枪先转、手后算"的同帧错位穿模。
        TickAxisRotation(ctx);

        SetAimTargetPosition(ctx.HeadAimConstraint, lookPoint);
        SetAimTargetPosition(ctx.BodyAimConstraint, lookPoint);

        // 瞄准全程：双手 ChainIK target 由"进入时捕获的腕-枪相对位姿 × 当前手雷根位姿"推算
        UpdateHandTargets(ctx);
    }

    /// <summary>把约束的第一个源对象（目标）移动到指定位置；约束/源为空时静默跳过。</summary>
    static void SetAimTargetPosition(MultiAimConstraint constraint, Vector3 position)
    {
        if (constraint == null) return;
        var sources = constraint.data.sourceObjects;   // 只读引用，不改数组
        if (sources.Count == 0) return;
        Transform target = sources[0].transform;
        if (target != null) target.position = position;
    }
    #endregion

    #region 程序化 IK 接管（手雷根切父/过渡、双手 ChainIK 推算、退出还原）
    /// <summary>
    /// 进入瞄准：① 实时捕获双手腕相对手雷根的位姿；② 记录手雷根原父级与 local TRS；
    /// ③ 把手雷根设为轴点的子级（只改枪，不改轴点）；④ 启动过渡计时。
    /// </summary>
    void BeginAimRig(PlayerContext ctx)
    {
        rigRoot = ctx.GrenadeRoot;
        rigPivot = ctx.GrenadeAxisPoint;
        rigOffset = ctx.GrenadeAxisOffset;
        rigHeld = false;
        rigSavedParent = null;
        rigTransitionRemaining = 0f;
        rightWristCaptured = false;
        leftWristCaptured = false;

        // 未装配（缺手雷根/轴点）→ 跳过手雷根接管：只剩头/胸引导与 GunAimPoint 记录（无枪身可转）
        if (rigRoot == null || rigPivot == null) return;
        if (rigPivot.IsChildOf(rigRoot))
        {
            // 轴点在枪内时无法把枪设为它的子级（会成环）；轴点应是枪外的独立节点
            Debug.LogWarning("[AimIK] 轴点（aimAxisPoint）是枪的子物体，无法把枪设为它的子级（会成环），已退回旧行为。请把轴点放到枪外（例如角色挂点下的独立空物体）。");
            return;
        }

        // 进入瞬间双手仍握在枪上：以当前实时相对位姿为常量基准
        CaptureHandOffsets(ctx);

        // 记录"回到原父级"的基准 local TRS（退出时原样恢复，动画才能无缝接管）
        rigSavedParent = ctx.RightHandWrist != null ? ctx.RightHandWrist : rigRoot.parent;
        rigSavedLocalPosition = rigRoot.localPosition;
        rigSavedLocalRotation = rigRoot.localRotation;
        rigSavedLocalScale = rigRoot.localScale;

        // 手雷根设为轴点子级（世界位姿不跳）；轴点旋转由脚本覆盖时枪自动跟随
        rigRoot.SetParent(rigPivot, true);
        rigStartLocalPosition = rigRoot.localPosition;
        rigStartLocalRotation = rigRoot.localRotation;

        rigTransitionDuration = Mathf.Max(MinRigTransitionDuration, ctx.Values.aimRigTransitionTime);
        rigTransitionRemaining = rigTransitionDuration;
        rigHeld = true;

        // 轴点旋转推进的确定性起点 = 进入瞬间轴点当前世界旋转（RotateTowards 限速插值从此开始）
        rigAxisRotation = rigPivot.rotation;

        // 手-枪锁定：进入瞬间值先贴枪；等 aimHandCaptureTime（动画 crossfade 完成后）
        // 一次性重捕获稳态握位并平滑过渡（见 UpdateHandTargets 注释）
        rigHandLocked = false;
        rigRelockRemaining = Mathf.Max(0f, ctx.Values.aimHandCaptureTime);
        rigRelockBlendRemaining = 0f;

        // 激活轴点旋转程序化覆盖（PlayerControllerScript.LateUpdate 据此接管轴点 worldRotation）
        ctx.AimRigActive = true;
    }

    /// <summary>进入瞬间把左右手 ChainIK 的 tip（腕骨）位姿换算进手雷根 local 空间保存。</summary>
    void CaptureHandOffsets(PlayerContext ctx)
    {
        if (ctx.RightArmChainConstraint != null)
        {
            Transform tip = ctx.RightArmChainConstraint.data.tip;
            if (tip != null)
            {
                rightWristLocalPos = rigRoot.InverseTransformPoint(tip.position);
                rightWristLocalRot = Quaternion.Inverse(rigRoot.rotation) * tip.rotation;
                rightWristCaptured = true;
            }
        }
        if (ctx.LeftArmChainConstraint != null)
        {
            Transform tip = ctx.LeftArmChainConstraint.data.tip;
            if (tip != null)
            {
                leftWristLocalPos = rigRoot.InverseTransformPoint(tip.position);
                leftWristLocalRot = Quaternion.Inverse(rigRoot.rotation) * tip.rotation;
                leftWristCaptured = true;
            }
        }
    }

    /// <summary>
    /// 过渡期：手雷根 local 从切父起点平滑过渡到（ctx.GrenadeAxisOffset, ctx.GrenadeAxisLocalRotation）。
    /// 过渡结束后每帧持续写入：localPosition = grenadeAimAxisOffset、localRotation = grenadeAimAxisLocalEuler
    /// （横握手雷需要基于轴点的旋转标定；步枪/手枪瞄准姿态竖直、localRotation 恒等，此处不同）。
    /// 使 Inspector 调整 offset/Euler 立即生效；轴点本身始终不被写入。
    /// </summary>
    void TickAimRigTransition(PlayerContext ctx)
    {
        if (!rigHeld || rigRoot == null) return;

        Quaternion targetLocalRotation = ctx.GrenadeAxisLocalRotation;
        if (rigTransitionRemaining > 0f)
        {
            rigTransitionRemaining = Mathf.Max(0f, rigTransitionRemaining - Time.deltaTime);
            float t = Mathf.Clamp01(1f - rigTransitionRemaining / rigTransitionDuration);
            float smooth = t * t * (3f - 2f * t);
            rigRoot.localPosition = Vector3.Lerp(rigStartLocalPosition, rigOffset, smooth);
            rigRoot.localRotation = Quaternion.Slerp(rigStartLocalRotation, targetLocalRotation, smooth);
            if (rigTransitionRemaining <= 0f)
            {
                rigRoot.localPosition = rigOffset;
                rigRoot.localRotation = targetLocalRotation;
            }
            return;
        }

        // 稳定期：持续应用 offset（轴点坐标系下的 local），改 grenadeAimAxisOffset / LocalEuler 即时生效
        rigRoot.localPosition = rigOffset;
        rigRoot.localRotation = targetLocalRotation;
    }

    /// <summary>
    /// 过渡期手雷朝向 aim 目标：角色身前远点，方向 = 角色 yaw（水平 forward）+ 相机 pitch。
    /// 水平原点取轴点；角色仍在转身对齐相机 yaw 期间手雷朝向不追相机 yaw，但俯仰直接取相机，
    /// 使过渡结束切到视线远点时俯仰连续（偏航残余由转身插值吸收）。
    /// </summary>
    Vector3 GrenadeFrontTarget(PlayerContext ctx)
    {
        Vector3 origin = rigPivot != null
            ? rigPivot.position
            : ctx.Transform.position + Vector3.up * FallbackAxisHeight;

        // 默认 = 角色正前方（无相机时回退）
        Vector3 dir = ctx.Transform.forward;
        Camera cam = ctx.MainCamera;
        if (cam != null)
        {
            Vector3 camF = cam.transform.forward;
            float camHoriz = Mathf.Sqrt(camF.x * camF.x + camF.z * camF.z);
            if (camHoriz > MinCameraHorizLen)
            {
                // 水平分量保留角色 yaw（dir），竖直分量取相机俯仰（camF.y，上正下负）
                dir = new Vector3(dir.x * camHoriz, camF.y, dir.z * camHoriz);
                if (dir.sqrMagnitude > MinAimDirSqr) dir.Normalize();
            }
        }
        return origin + dir * ctx.Values.aimRayDistance;
    }

    /// <summary>
    /// 轴点旋转的确定性推进（状态 Tick 内执行，LateUpdate 只做应用）：
    /// RotateTowards 以 aimAxisMaxRotSpeed 限速把 rigAxisRotation 插向"本帧手雷朝向瞄准方向"，
    /// 结果写入 ctx.AxisPointRotation——该值同时被 LateUpdate（设置轴点 worldRotation）与
    /// UpdateHandTargets（推算双手 target）使用，保证手/枪同帧同旋转。
    /// 轴向语义：沿用原手雷朝向约定（aimAxis=Z_NEG）——aimAxisNegZ=true 时让轴点 -Z 指向瞄准点。
    /// </summary>
    void TickAxisRotation(PlayerContext ctx)
    {
        if (!rigHeld || rigPivot == null) return;

        Vector3 dir = ctx.GunAimPoint - rigPivot.position;
        if (dir.sqrMagnitude < MinAimDirSqr) return;

        Quaternion target = ctx.Values.aimAxisNegZ
            ? Quaternion.LookRotation(-dir, Vector3.up)
            : Quaternion.LookRotation(dir, Vector3.up);
        rigAxisRotation = Quaternion.RotateTowards(rigAxisRotation, target,
            ctx.Values.aimAxisMaxRotSpeed * Time.deltaTime);
        ctx.AxisPointRotation = rigAxisRotation;
    }

    /// <summary>
    /// 双手 ChainIK target 推算：用进入瞬间捕获的"腕-枪"常量位姿 × 本帧手雷根应处位姿
    /// （轴点位置 + rigAxisRotation × 手雷根 local，与 LateUpdate 写入轴点的旋转同值），
    /// 得到本帧手腕应在的世界位姿并写入 target。只写 target.transform，不改约束装配。
    /// 注意：正常↔瞄准的动画 crossfade 期间右手 TwoBoneIK 与 ChainIK 权重同时 > 0，
    /// 右手 TwoBoneIK target 的对齐由 PlayerControllerScript.UpdateRightHandAimBlendTargets
    /// 在本状态之后统一处理（把两 target 写到同一插值位姿，消除争抢扭转），本方法不重复写。
    /// </summary>
    void UpdateHandTargets(PlayerContext ctx)
    {
        if (!rigHeld || rigRoot == null || rigPivot == null) return;

        // 重捕获时机：crossfade 完成后一次性重捕获稳态握位（期间手保持贴枪，见字段注释），
        // 随后在 aimHandRelockBlendTime 内从旧握位平滑过渡到稳态握位
        if (!rigHandLocked)
        {
            rigRelockRemaining -= Time.deltaTime;
            if (rigRelockRemaining <= 0f)
            {
                rightWristPrevPos = rightWristLocalPos;
                rightWristPrevRot = rightWristLocalRot;
                leftWristPrevPos = leftWristLocalPos;
                leftWristPrevRot = leftWristLocalRot;
                CaptureHandOffsets(ctx);
                rigHandLocked = true;
                rigRelockBlendDuration = Mathf.Max(MinRigTransitionDuration, ctx.Values.aimHandRelockBlendTime);
                rigRelockBlendRemaining = rigRelockBlendDuration;
            }
        }

        // 平滑过渡（旧握位 → 稳态握位）；无过渡时直接用当前捕获值
        Vector3 rPos = rightWristLocalPos;
        Quaternion rRot = rightWristLocalRot;
        Vector3 lPos = leftWristLocalPos;
        Quaternion lRot = leftWristLocalRot;
        if (rigRelockBlendRemaining > 0f)
        {
            rigRelockBlendRemaining = Mathf.Max(0f, rigRelockBlendRemaining - Time.deltaTime);
            float t = 1f - rigRelockBlendRemaining / rigRelockBlendDuration;
            float s = t * t * (3f - 2f * t);
            rPos = Vector3.Lerp(rightWristPrevPos, rightWristLocalPos, s);
            rRot = Quaternion.Slerp(rightWristPrevRot, rightWristLocalRot, s);
            lPos = Vector3.Lerp(leftWristPrevPos, leftWristLocalPos, s);
            lRot = Quaternion.Slerp(leftWristPrevRot, leftWristLocalRot, s);
        }

        // 手雷根本帧应处位姿（确定性：旋转 = rigAxisRotation，与轴点即将写入的 worldRotation 一致）
        Vector3 gunPos = rigPivot.position + rigAxisRotation * rigRoot.localPosition;
        Quaternion gunRot = rigAxisRotation * rigRoot.localRotation;
        if (rightWristCaptured)
        {
            WriteChainTarget(ctx.RightArmChainConstraint,
                gunPos + gunRot * rPos, gunRot * rRot);
        }
        if (leftWristCaptured)
        {
            WriteChainTarget(ctx.LeftArmChainConstraint,
                gunPos + gunRot * lPos, gunRot * lRot);
        }
    }

    /// <summary>把推算出的手腕世界位姿写入 ChainIK 的 target。</summary>
    static void WriteChainTarget(ChainIKConstraint chain, Vector3 position, Quaternion rotation)
    {
        if (chain == null) return;
        Transform target = chain.data.target;
        if (target == null) return;
        target.SetPositionAndRotation(position, rotation);
    }

    /// <summary>退出瞄准：手雷根设回进入前父级并恢复 local TRS；轴点全程不动。</summary>
    void RestoreAimRig(PlayerContext ctx)
    {
        if (!rigHeld)
        {
            rigRoot = null;
            rigPivot = null;
            return;
        }

        rigHeld = false;
        rigTransitionRemaining = 0f;
        if (rigRoot != null)
        {
            if (rigSavedParent != null)
            {
                rigRoot.SetParent(rigSavedParent, false);
            }
            else
            {
                rigRoot.SetParent(null, false);
            }
            rigRoot.localPosition = rigSavedLocalPosition;
            rigRoot.localRotation = rigSavedLocalRotation;
            rigRoot.localScale = rigSavedLocalScale;
        }
        rigSavedParent = null;
        rigRoot = null;
        rigPivot = null;
    }
    #endregion

    #region 转向、位移与动画参数
    /// <summary>
    /// 瞄准转向：角色始终朝向相机水平前方（相机 forward 投影 XZ）。
    /// 与地面移动转向（RotateTowardMoveDirection）的差异：
    /// - 不随输入门控——无移动输入时也持续追踪（瞄准语义 = "相机即瞄准轴线"，角色必须朝向视线方向，
    ///   避免"站定不动时相机已转、角色朝向滞后"）；有输入时移动方向仍由 2D 混合树表现（横向 strafe）。
    /// 转向以 SmoothDampAngle 做 yaw 单轴阻尼追角：进入瞄准后的 aimAlignTime 内使用高响应平滑时间
    /// （aimAlignSmoothTime）快速到位，随后恢复常规 aimRotateSmoothTime——无瞬转、无恒速阶跃；
    /// aimRotateMaxSpeed 限速钳制防止大误差首帧跳变过陡。仅输出 yaw（Euler(0, yaw, 0)）。
    /// </summary>
    void RotateTowardCameraForward(PlayerContext ctx)
    {
        Vector3 camForward = CameraForward(ctx);   // 已投影 XZ 并归一化；缺主相机时回退世界 forward
        if (camForward.sqrMagnitude <= ctx.Values.minMoveSqrMagnitude) return;

        float targetYaw = Mathf.Atan2(camForward.x, camForward.z) * Mathf.Rad2Deg;

        // 两段式：进入对齐阶段用高响应平滑时间（快速平滑转身），计时结束后恢复常规跟随
        float activeSmoothTime = aimAlignTimer > 0f ? ctx.Values.aimAlignSmoothTime
                                                    : ctx.Values.aimRotateSmoothTime;
        aimYaw = Mathf.SmoothDampAngle(aimYaw, targetYaw, ref aimYawVelocity,
                                       activeSmoothTime, ctx.Values.aimRotateMaxSpeed);
        aimAlignTimer -= Time.deltaTime;

        ctx.Transform.rotation = Quaternion.Euler(0f, aimYaw, 0f);
    }

    public override void OnAnimatorMove(PlayerContext ctx)
    {
        // 瞄准地面：沿用动画根运动（2D 混合树）+ 手写垂直分量（早期原型 同款）
        Vector3 delta = ctx.Animator.deltaPosition;
        delta.y = ctx.Motion.VerticalVelocity * Time.deltaTime;
        ctx.CharacterController.Move(delta);
    }

    void WriteAnimatorParams(PlayerContext ctx)
    {
        var anim = ctx.AnimParams;   // 值缓存写入器：同值跳过 SetFloat
        // 瞄准：水平面内前后/左右轴分别进 2D 混合树两个轴
        anim.SetFloat(PlayerControllerScript.AnimVerticalSpeed, aimVerticalSpeed);
        anim.SetFloat(PlayerControllerScript.AnimHorizontalSpeed, aimHorizontalSpeed);
        // 着地（非下落）时垂直方向速度为 0
        anim.SetFloat(PlayerControllerScript.AnimFallingSpeed, NoFallingSpeed);
    }
    #endregion
}

