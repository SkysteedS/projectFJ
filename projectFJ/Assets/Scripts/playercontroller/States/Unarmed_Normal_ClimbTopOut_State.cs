using UnityEngine;

/// <summary>
/// 具体状态：空手（Unarmed）× 正常手部（Normal）× 登顶（ClimbTopOut）。
///
/// 职责（入边已补全：攀爬状态到顶探测命中 → 请求本状态）：
/// - 机器切到本组合后，主体类 SyncAnimatorPostureParams 每帧写 body posture = 3，
///   Animator 由 climb 状态进入 Tag=ClimbToEnd 的 climb to end 动画（一次性，约 3.83s）；
/// - 悬挂手固定（MatchTarget）：“被固定的那只手”（ctx.TopOutGrabLeftHand，由攀爬状态按到顶探测
///   选择的较高手写入）的抓取点 = 进入登顶瞬间从该手骨上方开始、垂直向下每隔 1cm 扫描墙顶面
///   （见 BeginGrabLock / TryScanGrabPoint），而不是 IK target 快照（IK target 贴墙面、低于墙顶缘，
///   直接快照会让手抓空）；待 climb to end 真正开始播放后对该手下达一次 MatchTarget，
///   让动画“手按在缘上、身体悬挂→上撑”的位移与场景几何对齐；
/// - 放手：一次性 MatchTarget 在 topOutMatchEnd（默认 0.1 = 动画 10%）处把被抓手精确匹配到
///   墙顶抓取点（+ 可调偏移）；HandGrabEdgeEnd 事件（climb to end.anim 1.0s ≈ normalizedTime 0.261）
///   触发后解除固定（后续不再匹配），由动画根运动接管把身体带上平台；
/// - 碰撞体：进入本状态即禁用 CharacterController——登顶的 MatchTarget/根运动位移需要【精确】落到
///   墙顶抓取点，若仍走 CC.Move，角色胶囊与墙顶/平台边缘的碰撞会顶掉/滑走部分位移，手就抓不到点；
///   禁用期间 OnAnimatorMove 直接把 deltaPosition 加到 transform，离开本状态时恢复 CC。
/// - 位移：OnAnimatorMove 应用动画根运动——匹配期间 animator.deltaPosition 已含 MatchTarget 修正，
///   无需脚本叠加位移；
/// - 结束：检测 Tag=ClimbToEnd 动画是否播放到 Values.topOutExitNormalizedTime（默认 0.9），
///   到达即请求退出到 Unarmed Normal Ground（提前于动画末段，让收尾姿态由地面过渡衔接）。
///
/// 数值参数见 PlayerMotionValuesSO【登顶】区（探测/匹配窗口全部可 Inspector 调参）。
/// </summary>
public class Unarmed_Normal_ClimbTopOut_State : PlayerStateBase
{
    /// <summary>
    /// 放手动画事件名（与 climb to end.anim 的 Animation Event functionName 一致，勿改；
    /// PlayerControllerScript.HandGrabEdgeEnd 收到事件后经状态机转发，本状态据此中断匹配）。
    /// </summary>
    public const string AnimEventHandGrabEdgeEnd = "HandGrabEdgeEnd";

    /// <summary>登顶动画 Tag（Animator 中 climb to end 状态的 m_Tag，见 Animator 控制器）。</summary>
    static readonly int ClimbToEndTagHash = Animator.StringToHash("ClimbToEnd");
    /// <summary>墙面方向向量平方长度下限（零向量防护）。</summary>
    const float MinDirectionSqr = 0.0001f;
    /// <summary>扫描命中点允许低于被抓取手骨的最大值（m）：低于过多说明垂线打到了墙后地面等错误表面。</summary>
    const float GrabPointMaxBelowHand = -0.3f;

    bool grabLeftHand;          // 被固定的手：true = 左手（攀爬状态写入 ctx.TopOutGrabLeftHand，Enter 读取）
    bool grabLockActive;        // 固定是否仍激活（等待 HandGrabEdgeEnd 放手前为 true）
    bool grabMatchIssued;       // 是否已下达过一次性 MatchTarget（窗口终点 = 落位时间，事件后不再下达）
    Vector3 grabWorldPosition;  // MatchTarget 目标位：墙顶扫描抓取点（扫描失败时兜底 IK 快照）+ topOutMatchPositionOffset 可调偏移
    Quaternion grabWorldRotation; // 旋转快照（旋转权重 > 0 时使用；默认 0 时不影响）
    bool controllerWasEnabled;  // 进入前 CharacterController 的 enabled 值（Exit 恢复）
    bool controllerDisabled;    // 本状态是否禁用了 CharacterController

    public override void Enter(PlayerContext ctx)
    {
        grabLeftHand = ctx.TopOutGrabLeftHand;
        BeginGrabLock(ctx);
        DisableControllerForRootMotion(ctx);
    }

    public override void Exit(PlayerContext ctx)
    {
        // 防御：被中途切走（如动画事件未配置/状态被外力打断）时不残留匹配与固定标志
        if (grabLockActive && ctx.Animator.isMatchingTarget)
        {
            ctx.Animator.InterruptMatchTarget(false);
        }
        grabLockActive = false;
        grabMatchIssued = false;
        RestoreController(ctx);
        ctx.TopOutGrabLeftHand = false;
        ctx.TopOutProbeActive = false;
        ctx.TopOutGrabPointValid = false;
    }

    public override void Tick(PlayerContext ctx)
    {
        TryIssueGrabMatch(ctx);

        if (IsClimbToEndFinished(ctx))
        {
            ctx.RequestTransition(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground);
        }
    }

    /// <summary>
    /// 动画事件回调（经 PlayerStateMachine.NotifyAnimEvent 由主体类入口转发）：
    /// HandGrabEdgeEnd = 悬挂结束，解除固定（匹配窗口应在事件前已完成，此处只防御性中断
    /// 尚未完成的匹配；之后动画由根运动自由驱动）。
    /// </summary>
    public override void OnAnimEvent(PlayerContext ctx, string message)
    {
        if (message != AnimEventHandGrabEdgeEnd || !grabLockActive) return;

        if (ctx.Values.topOutDebugLog)
        {
            Debug.Log($"[ClimbTopOut] HandGrabEdgeEnd @nt={ClimbToEndNormalizedTime(ctx):F3} " +
                      $"wasMatching={ctx.Animator.isMatchingTarget} target={grabWorldPosition:F3}");
        }
        if (ctx.Animator.isMatchingTarget) ctx.Animator.InterruptMatchTarget(false);
        grabLockActive = false;
        grabMatchIssued = false;
    }

    public override void OnAnimatorMove(PlayerContext ctx)
    {
        // 登顶整体位移由动画根运动驱动（撑上台阶），不叠加任何脚本速度，也不做贴墙/重力修正。
        // 本状态期间 CharacterController 已禁用（见 DisableControllerForRootMotion），
        // MatchTarget 的修正体现在 animator.deltaPosition 内并直接加到 transform，避免碰撞顶掉位移。
        if (ctx.CharacterController != null && ctx.CharacterController.enabled)
        {
            ctx.CharacterController.Move(ctx.Animator.deltaPosition);
        }
        else if (ctx.Transform != null)
        {
            ctx.Transform.position += ctx.Animator.deltaPosition;
        }
    }

    /// <summary>
    /// 禁用 CharacterController：登顶动画（尤其 MatchTarget 落位段）要求根位移精确生效，
    /// CC.Move 会因胶囊与墙顶边缘碰撞而削减/扭曲位移。GroundProbe 只读胶囊参数，不受禁用影响。
    /// </summary>
    void DisableControllerForRootMotion(PlayerContext ctx)
    {
        controllerDisabled = false;
        if (ctx.CharacterController == null) return;

        controllerWasEnabled = ctx.CharacterController.enabled;
        if (!controllerWasEnabled) return;   // 本来就没启用：无需本状态负责恢复

        ctx.CharacterController.enabled = false;
        controllerDisabled = true;
    }

    /// <summary>离开登顶时恢复 CharacterController（供地面/跳跃状态继续用 CC.Move）。</summary>
    void RestoreController(PlayerContext ctx)
    {
        if (!controllerDisabled || ctx.CharacterController == null) return;

        ctx.CharacterController.enabled = controllerWasEnabled;
        controllerDisabled = false;
    }

    /// <summary>
    /// 悬挂手固定：进入时确定被抓手的世界抓取点（MatchTarget 目标），此后每帧不再改变。
    /// 抓取点优先来自“墙顶扫描”：从该手手骨上方沿角色前向偏移 topOutGrabScanForward 处
    /// （让垂线落在墙顶面正上方）逐厘米向下扫描，首个命中的水平顶面即抓取位置；
    /// 扫描失败（配置/几何不满足）时回退旧逻辑 = 该手 IK target 的世界位置快照并告警；
    /// 最终目标再叠加 topOutMatchPositionOffset（角色右/上/前向局部系）供手动调参。
    /// </summary>
    void BeginGrabLock(PlayerContext ctx)
    {
        ctx.TopOutGrabPointValid = false;
        ctx.TopOutGrabUsedScan = false;

        Transform grabTarget = grabLeftHand
            ? (ctx.LeftHandConstraint != null ? ctx.LeftHandConstraint.data.target : null)
            : (ctx.RightHandConstraint != null ? ctx.RightHandConstraint.data.target : null);
        Transform handBone = ctx.Animator.GetBoneTransform(
            grabLeftHand ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);

        Transform fallback = grabTarget != null ? grabTarget : handBone;
        if (fallback == null)
        {
            grabLockActive = false;
            grabMatchIssued = false;
            Debug.LogWarning("[ClimbTopOut] 找不到被固定的手（IK target 与手骨均为空），已跳过 MatchTarget 固定（登顶动画仍会播放）。");
            return;
        }

        bool scanned = false;
        ctx.TopOutGrabScanOrigin = handBone != null ? handBone.position : fallback.position;
        if (ctx.Values.topOutGrabUseLedgeScan && handBone != null)
        {
            scanned = TryScanGrabPoint(ctx, handBone, out grabWorldPosition);
        }
        if (!scanned)
        {
            grabWorldPosition = fallback.position;
            if (ctx.Values.topOutGrabUseLedgeScan && handBone != null)
            {
                Debug.LogWarning("[ClimbTopOut] 墙顶扫描未找到可抓取面，已回退 IK target 快照作为抓取点。" +
                                 "请检查 topOutGrabScanForward 是否小于墙体厚度、扫描起点是否高于墙顶。");
            }
        }

        // 手动调参偏移（topOutMatchPositionOffset，局部系）：X = 角色右轴（沿墙横向）、
        // Y = 世界向上、Z = 角色前向（指向墙面）。扫描点/兜底点基础上叠加，便于 Play Mode 微调匹配落点。
        Vector3 forward = Vector3.ProjectOnPlane(ctx.Transform.forward, Vector3.up);
        if (forward.sqrMagnitude < MinDirectionSqr) forward = Vector3.forward;
        forward.Normalize();
        Vector3 offset = ctx.Values.topOutMatchPositionOffset;
        grabWorldPosition += ctx.Transform.right * offset.x + Vector3.up * offset.y + forward * offset.z;

        ctx.TopOutGrabUsedScan = scanned;
        ctx.TopOutGrabPoint = grabWorldPosition;
        ctx.TopOutGrabPointValid = true;
        grabWorldRotation = fallback.rotation;
        grabLockActive = true;
        grabMatchIssued = false;
    }

    /// <summary>
    /// 墙顶抓取点扫描（进入登顶瞬间执行一次）：被抓取手的手骨为水平参考，沿角色前向偏移
    /// topOutGrabScanForward 使垂线位于墙顶面正上方；从手骨上方 topOutGrabScanStartAbove 开始，
    /// 每隔 topOutGrabScanStep（1cm）向下发射一根短射线，第一个命中“法线朝上”的水平面
    /// （且高度不超出抓握范围）即抓取点，再按 topOutGrabPointUpOffset 上抬补偿腕骨高度。
    /// </summary>
    bool TryScanGrabPoint(PlayerContext ctx, Transform handBone, out Vector3 grabPoint)
    {
        grabPoint = default;
        if (handBone == null) return false;

        Vector3 forward = Vector3.ProjectOnPlane(ctx.Transform.forward, Vector3.up);
        if (forward.sqrMagnitude < MinDirectionSqr) return false;
        forward.Normalize();

        float step = Mathf.Max(0.001f, ctx.Values.topOutGrabScanStep);
        int steps = Mathf.Max(0, ctx.Values.topOutGrabScanSteps);
        Vector3 horizontal = handBone.position + forward * ctx.Values.topOutGrabScanForward;
        float topY = handBone.position.y + ctx.Values.topOutGrabScanStartAbove;
        ctx.TopOutGrabScanOrigin = new Vector3(horizontal.x, topY, horizontal.z);
        float rayLength = step + 0.005f;   // 略长于采样间隔，避免漏过两采样点间的小台阶/边缘倒角
        LayerMask layer = ctx.WallProbe.climbableLayer;

        for (int i = 0; i < steps; ++i)
        {
            Vector3 origin = new Vector3(horizontal.x, topY - i * step, horizontal.z);
            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, rayLength, layer)) continue;
            if (hit.normal.y <= 0.7f) continue;   // 只认水平朝上的顶面（墙顶/平台顶），跳过竖直墙面侧面

            float aboveHand = hit.point.y - handBone.position.y;
            if (aboveHand < GrabPointMaxBelowHand || aboveHand > ctx.Values.topOutGrabMaxLedgeHeight)
            {
                return false;   // 打到远低于手的墙后地面 / 远高于手的其它平台：视为扫错面，交回兜底
            }

            grabPoint = hit.point + Vector3.up * ctx.Values.topOutGrabPointUpOffset;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 一次性下达悬挂手 MatchTarget：在 climb to end 真正开播（crossfade 结束）、且动画进度进入
    /// [topOutMatchStart, topOutMatchEnd] 窗口时只调用一次。引擎从 start 起把修正权重混合到 1，
    /// 使被抓手在 topOutMatchEnd（默认 0.1 = 动画 10%）时精确落在墙顶抓取点（+ 可调偏移）。
    /// 注意不能每帧重复下达：重复调用会反复重置匹配混合进度，修正权重永远上不去，
    /// 手会停在起始位置与抓取点之间的半空（表现为“抓到空中某处”）。
    /// 位置权重默认全 1；旋转权重默认 0（手型旋转仍由动画控制，避免攀爬 IK 贴墙手型与悬挂抓缘手型打架）。
    /// </summary>
    void TryIssueGrabMatch(PlayerContext ctx)
    {
        if (!grabLockActive || grabMatchIssued) return;

        // 等待登顶动画真正成为当前状态（crossfade 中下达会作用在混合中的爬墙动画上）
        if (ctx.Animator.IsInTransition(0)) return;
        if (!TryGetClimbToEndProgress(ctx, out float normalizedTime)) return;

        PlayerMotionValuesSO v = ctx.Values;
        float start = Mathf.Clamp01(v.topOutMatchStartNormalizedTime);
        float end = Mathf.Clamp01(v.topOutMatchEndNormalizedTime);
        if (end <= start) return;
        if (normalizedTime < start || normalizedTime > end) return;

        var mask = new MatchTargetWeightMask(v.topOutMatchPositionWeight, v.topOutMatchRotationWeight);
        AvatarTarget bodyPart = grabLeftHand ? AvatarTarget.LeftHand : AvatarTarget.RightHand;
        ctx.Animator.MatchTarget(grabWorldPosition, grabWorldRotation, bodyPart, mask, start, end);
        grabMatchIssued = true;

        if (v.topOutDebugLog)
        {
            Debug.Log($"[ClimbTopOut] MatchTarget issued @nt={normalizedTime:F3} part={bodyPart} " +
                      $"window={start:F3}..{end:F3} target={grabWorldPosition:F3} isMatching={ctx.Animator.isMatchingTarget}");
        }
    }

    /// <summary>当前 ClimbToEnd 动画进度（调试日志用；未播放返回 -1）。</summary>
    static float ClimbToEndNormalizedTime(PlayerContext ctx)
        => TryGetClimbToEndProgress(ctx, out float normalizedTime) ? normalizedTime : -1f;

    /// <summary>
    /// Tag=ClimbToEnd 动画是否到达退出阈值：扫描所有动画层（跨层保险），
    /// 当前状态带该 Tag 且 normalizedTime ≥ Values.topOutExitNormalizedTime（默认 0.9）。
    /// </summary>
    static bool IsClimbToEndFinished(PlayerContext ctx)
        => TryGetClimbToEndProgress(ctx, out float normalizedTime)
           && normalizedTime >= ctx.Values.topOutExitNormalizedTime;

    /// <summary>
    /// 取 Tag=ClimbToEnd 动画的进度（normalizedTime）：当前/过渡中下一状态带该 Tag 即返回；
    /// 未播放返回 false。跨层扫描保险（实际该动画只在 Base Layer 的 climb 子状态机内）。
    /// </summary>
    static bool TryGetClimbToEndProgress(PlayerContext ctx, out float normalizedTime)
    {
        int layers = ctx.Animator.layerCount;
        for (int i = 0; i < layers; ++i)
        {
            var current = ctx.Animator.GetCurrentAnimatorStateInfo(i);
            if (current.tagHash == ClimbToEndTagHash)
            {
                normalizedTime = current.normalizedTime;
                return true;
            }
            if (ctx.Animator.IsInTransition(i))
            {
                var next = ctx.Animator.GetNextAnimatorStateInfo(i);
                if (next.tagHash == ClimbToEndTagHash)
                {
                    normalizedTime = next.normalizedTime;
                    return true;
                }
            }
        }
        normalizedTime = 0f;
        return false;
    }
}
