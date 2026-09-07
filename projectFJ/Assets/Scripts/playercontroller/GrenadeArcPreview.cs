using UnityEngine;

/// <summary>
/// 手雷弧线预览（独立工具类，供 Grenade_Aiming_Ground_State 组合调用）：
/// 职责：接管"手雷瞄准期间的抛物线弧线预览"这一整套逻辑——
/// - 渲染器（LineRenderer）引用的查找与初始参数（世界空间采样、折角/端帽圆滑）；
/// - 落点探测：真实抛物线解析 + 粗/细两段地形采样（TryFindArcLanding）；
/// - 显示节奏：进瞄准过渡期与短暂延迟内不显示，之后按 grenadeArcSampleCount 重采样写入；
/// - 淡入：只缩 alpha 通道、不动用户配色（渐变键数组在 Enter 一次性缓存，避免淡入期帧级托管分配）。
///
/// 与状态类的关系：Grenade_Aiming_Ground_State 只负责"何时调用"——
/// 状态 Enter 时调 Enter(ctx)、每帧 Tick 调 Tick(ctx, rigHeld, rigTransitioning)、退出时调 Exit()；
/// 本类只经 PlayerContext 读取组件与数值（ctx.GrenadeRoot / ctx.GrenadeAxisPoint /
/// ctx.AxisPointRotation / ctx.Values），不依赖状态类内部字段，也不参与状态切换裁决。
/// 数值参数见 PlayerMotionValuesSO（grenadeArc* 区）；初速读取手雷上 BulletManage.bulletData。
/// </summary>
public class GrenadeArcPreview
{
    #region 结构常量（语义见注释；可调参数见 PlayerMotionValuesSO）
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
    /// <summary>时值下限（s）：防淡入时长/精化步长为 0 或负值导致除零/瞬间完成。</summary>
    static readonly float MinDuration = 0.001f;
    /// <summary>初速方向平方长度下限：低于该值视为方向无效（旋转异常时的防御）。</summary>
    static readonly float MinAimDirSqr = 1e-6f;
    #endregion

    #region 弧线预览状态（渲染器引用与采样/淡入缓存）
    LineRenderer arcRenderer;           // 弧线预览渲染器（手雷根子物体，场景装配；Enter 时查找）
    Vector3[] arcSamplePoints = new Vector3[ArcDisplayMaxSamples];   // 显示采样缓存（防每帧 GC）
    BulletManage bulletManage;          // 投掷初速数据源（手雷上的 BulletManage；bulletData.bulletInitialSpeed）
    bool bulletDataWarned;              // 初速数据缺失时只告警一次
    float arcShowTimer;                 // 显示延迟计时（进入瞄准后开始倒计时）
    float arcFadeAlpha;                 // 当前弧线 alpha（淡入推进 0..1）
    Gradient arcBaseGradient;           // 进入时缓存的用户配色（淡入只缩 alpha，不动色相）
    Gradient arcFadeGradient = new Gradient();          // 复用的输出渐变（避免每帧新建 Gradient）
    GradientColorKey[] baseColorKeys;   // 用户配色键快照（Enter 一次性缓存）
    GradientAlphaKey[] baseAlphaKeys;   // 用户透明度键快照（Enter 一次性缓存）
    GradientAlphaKey[] fadeAlphaKeys;   // 淡入用可变副本：每帧原地缩放 alpha 后 SetKeys
    #endregion

    #region 生命周期（Enter/Tick/Exit：由 Grenade_Aiming_Ground_State 按状态生命周期调用）
    /// <summary>
    /// 进入瞄准：查找弧线渲染器与投掷初速数据源，准备世界空间采样与淡入状态。
    /// 只做一次性准备；未装配 LineRenderer（手雷根无子物体渲染器）时静默跳过（弧线不显示）。
    /// </summary>
    public void Enter(PlayerContext ctx)
    {
        arcRenderer = ctx.GrenadeRoot != null ? ctx.GrenadeRoot.GetComponentInChildren<LineRenderer>(true) : null;
        // 投掷初速数据源：手雷上的 BulletManage（不把初速放进运动数值资产）
        bulletManage = ctx.GrenadeRoot != null ? ctx.GrenadeRoot.GetComponentInChildren<BulletManage>(true) : null;
        if (arcRenderer == null) return;

        arcRenderer.useWorldSpace = true;
        arcRenderer.numCornerVertices = ArcCornerVertices;   // 折角圆滑（部分版本不在 Inspector 暴露，脚本统一设置）
        arcRenderer.numCapVertices = ArcCapVertices;         // 端点圆帽
        arcRenderer.positionCount = 0;
        arcRenderer.enabled = false;   // 过渡/延迟期间先不显示

        // 用户配色快照 + 键数组一次性缓存：colorKeys/alphaKeys 每次访问都返回新托管数组，
        // 淡入期每帧经 ApplyFadeAlpha 重写渐变，若逐帧重取会制造帧级 GC（见 ApplyFadeAlpha）。
        arcBaseGradient = arcRenderer.colorGradient;
        baseColorKeys = arcBaseGradient.colorKeys;
        baseAlphaKeys = arcBaseGradient.alphaKeys;
        fadeAlphaKeys = new GradientAlphaKey[baseAlphaKeys.Length];
        for (int i = 0; i < baseAlphaKeys.Length; ++i)
        {
            fadeAlphaKeys[i] = baseAlphaKeys[i];
        }

        arcShowTimer = ctx.Values.grenadeArcShowDelay;
        arcFadeAlpha = 0f;
    }

    /// <summary>
    /// 每帧更新（在 Grenade_Aiming_Ground_State.Tick 中、瞄准方向/轴点旋转确定后调用）：
    /// 抛物线解析 + 地形采样 → 写入弧线预览；显示节奏（手雷根过渡期/延迟）与淡入在此统一管理。
    /// </summary>
    /// <param name="ctx">状态上下文（经 ctx.GrenadeAxisPoint / ctx.AxisPointRotation 取本帧确定性旋转）</param>
    /// <param name="aimRigHeld">瞄准 IK 接管是否生效（手雷根已挂到轴点下；false = 装配缺失，不绘制）</param>
    /// <param name="aimRigTransitioning">手雷根进瞄准过渡是否进行中（true = 暂不显示弧线）</param>
    public void Tick(PlayerContext ctx, bool aimRigHeld, bool aimRigTransitioning)
    {
        if (arcRenderer == null || !aimRigHeld) return;
        Transform pivot = ctx.GrenadeAxisPoint;
        if (pivot == null) return;

        // 显示节奏：手雷根过渡期与短暂延迟内不绘制（避免手臂尚未到位时弧线乱摆/突兀出现）
        if (aimRigTransitioning)
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

        // 起点 = 轴点当前位姿 + 本帧确定性旋转 × local offset（与手雷根/双手 ChainIK 推算同值）
        Vector3 origin = pivot.position + ctx.AxisPointRotation * ctx.GrenadeAxisOffset;

        // 初速方向：轴点瞄准轴（aimAxisNegZ=true → 轴点 -Z 指向瞄准点）
        Vector3 dir = ctx.Values.aimAxisNegZ
            ? -(ctx.AxisPointRotation * Vector3.forward)
            : ctx.AxisPointRotation * Vector3.forward;
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
            float fadeTime = Mathf.Max(MinDuration, ctx.Values.grenadeArcFadeInTime);
            arcFadeAlpha = Mathf.Min(1f, arcFadeAlpha + Time.deltaTime / fadeTime);
            ApplyFadeAlpha(arcFadeAlpha);
        }
    }

    /// <summary>退出瞄准：清空弧线预览（隐藏并释放引用；下次 Enter 重新查找）。</summary>
    public void Exit()
    {
        if (arcRenderer != null)
        {
            arcRenderer.positionCount = 0;
            arcRenderer.enabled = false;
        }
        arcRenderer = null;
        arcBaseGradient = null;
        baseColorKeys = null;
        baseAlphaKeys = null;
        fadeAlphaKeys = null;
        arcFadeAlpha = 0f;
    }
    #endregion

    #region 弹道解析与地形采样（探测/显示共用）
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
            float fineStep = Mathf.Max(MinDuration, ctx.Values.grenadeArcDetectTimeStep);
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
    #endregion

    #region 淡入写入（渐变键缓存版：只缩 alpha，不动色相；帧内零托管分配）
    /// <summary>
    /// 把用户配色按当前 alpha 写入渲染器：淡入只缩 alpha 通道，不动色相/宽度曲线。
    /// 键数组（baseColorKeys/baseAlphaKeys）在 Enter 一次性快照；本方法只原地缩放
    /// fadeAlphaKeys 副本后 SetKeys——不再每帧调用 colorKeys/alphaKeys getter
    /// （两者每次调用都返回新托管数组，会造成淡入期的帧级 GC）。
    /// </summary>
    void ApplyFadeAlpha(float alpha)
    {
        if (arcRenderer == null || arcBaseGradient == null
            || baseColorKeys == null || fadeAlphaKeys == null) return;

        for (int i = 0; i < fadeAlphaKeys.Length; ++i)
        {
            fadeAlphaKeys[i].alpha = baseAlphaKeys[i].alpha * alpha;
        }
        arcFadeGradient.mode = arcBaseGradient.mode;
        arcFadeGradient.SetKeys(baseColorKeys, fadeAlphaKeys);
        arcRenderer.colorGradient = arcFadeGradient;
    }
    #endregion
}
