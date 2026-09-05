using UnityEngine;

/// <summary>
/// 具体状态：空手（Unarmed）× 正常手部（Normal）× 攀爬（Climbing）。
/// 行为取自 早期原型 攀爬段（保留项）：
/// - 进入：地面 Jump 信号边（跳跃键"攀爬优先"判定）或滞空持久边（空中抓墙，上升/下降均可）——
///   两者共用同一套"墙面命中 + 水平速度朝墙"条件，见 PlayerControllerScript 攀爬进入检测区；
/// - 墙面复测（覆盖式）：全部网格射线发射统计，缺失比例 ≥ Values.climbWallExitMissRatio（默认 50%）
///   且连续 climbExitMissFrames 帧 → 视为脱墙，提议回地面（物理驱逐）——容忍头顶射线越过墙顶，
///   避免与后续登顶检测冲突导致无法登顶；
/// - 退出输入：QuitClimb 信号边（按 X 退回默认姿态）；
/// - 向下爬至着陆：持续按 S（下）且下方持续探测到可着陆（GroundProbe 去抖着地，ctx.IsGrounded）
///   连续达 Values.climbDownExitFrames 帧 → 自动请求回 Normal 地面姿态（爬到底部直接落地转站立）；
/// - 头部注视：head Multi-Aim 的目标由本状态内写入（无外部写入）——有输入时方向 = 墙上移动方向、
///   最大转角 Values.climbHeadLookAngle（可调）；无输入保持上一注视角度（不回正）；
/// - 到顶探测（本版实现）：从较高手的手腕上方（topOutProbeUpOffset + topOutProbePalmOffset 偏差）沿墙面方向
///   射一根射线，连续 topOutProbeMissFrames 帧未命中墙面 = 该手已越过墙顶 → 请求 ClimbTopOut；
///   并把该手写入 ctx.TopOutGrabLeftHand（登顶状态据此对同一只手 MatchTarget 固定，见
///   Unarmed_Normal_ClimbTopOut_State）。到顶请求优先于脱墙退出（墙顶缺失与到顶同帧竞争时以登顶为准）。
/// - 脚部 IK：与手共用步进节奏（climbStepInterval / climbSwitchTime）——上下爬与手同拍反相
///   （同侧手在上则脚在下），左右爬双脚与手同相开合（打开/收拢均停在基准线上，不上不下）；
/// - 朝墙：身体平面垂直于墙面法线（面向墙），RotateTowards 插值；
/// - 位移：输入映射到墙面切平面（W=角色上方、S=下方、D=右、A=左）× climbSpeed，
///   沿法线贴墙收敛（保持 climbWallHugDistance），无重力——全部脚本接管（OnAnimatorMove）；
/// - 动画参数：本类只写速度分量（攀爬树不用，写 0）；body/hand/handing 由主体类同步（Body=Climbing）。
///
/// 攀爬手部 IK（本版实现，类原神姿态；参数见 PlayerMotionValuesSO【攀爬手部 IK】区，全部可 Inspector 调参）：
/// 程序化 TwoBoneIK 目标 = "跟随头部的水平线"为基准的墙面坐标系姿态——
/// - 上下攀爬：双手沿中线（距中线 climbHandCenterDist）上下交替，一手在水平线上方
///   climbHandAlternateLength、另一手下方同值（交替长度一致、上下对称），每步进间隔交替换手；
/// - 左右攀爬：对应移动方向侧的手在水平线下方 climbHandPlaceDist、相反侧手在上方同值
///   （放置距离一致）；横向按"收拢（中线距离 − 开合幅度）/ 打开（中线距离 + 开合幅度）"
///   两种状态随步进交替（收开幅度一致），每方向类别切换时立即交替；
/// - 无输入：保持当前姿态；方向类别改变：立即交替相位；持续同向：按步进间隔交替；
/// - 目标每帧合成后投影到当前墙面平面（贴墙），再沿墙面法线外推 climbHandWallNormalOffset
///   （target 定位的是腕骨而非掌心：腕-掌厚度若不做外推，掌心/手会陷入墙内）；
/// - 旋转：每帧写入 target.rotation = 墙面基准旋转（target 的 +Z 朝向墙面、+Y 沿墙向上）× 左右手
///   各自偏移欧拉（climbLeft/RightHandRotationOffset）；climbHandWriteRotation 关闭时保持原旋转（对照调试用）；
/// - 只移动 target 的位置/旋转（TRS），不写 constraint.weight——IK 权重由动画状态机全权管理
///   （"arms and leg ik layer" 的 climb 状态经 climb ik.anim 把 right/left hand ik weight 置 1）。
/// </summary>
public class Unarmed_Normal_Climbing_State : PlayerStateBase
{
    #region 状态内结构常量（语义见注释；可调参数见 PlayerMotionValuesSO）
    /// <summary>墙面法线平方长度下限（无效法线判定）。</summary>
    const float MinNormalSqr = 0.0001f;
    /// <summary>攀爬树不使用水平/垂直速度参数时的分量值。</summary>
    static readonly float IdleSpeedParam = 0f;
    /// <summary>输入向量平方长度下限（低于视为静止；与 早期原型 ClimbDirCategory 一致）。</summary>
    const float InputDeadZoneSqr = 0.01f;
    /// <summary>姿态平滑时间下限（s）：防"切换时间为 0"导致除零（Inspector 输入 0 或负值时兜底）。</summary>
    const float MinSwitchTime = 0.01f;
    /// <summary>头部注视目标点距头部骨骼距离（m）：Multi-Aim 只用目标点位置定方向，足够远即可。</summary>
    const float ClimbHeadLookDistance = 3f;
    /// <summary>头部注视方向平滑时间（s）：防 WASD/方向切换时头部瞬跳。</summary>
    const float ClimbHeadLookSmoothTime = 0.12f;
    /// <summary>身体注视目标点距约束骨骼（Chest）距离（m）。</summary>
    const float ClimbBodyLookDistance = 3f;
    /// <summary>身体注视方向平滑时间（s）。</summary>
    const float ClimbBodyLookSmoothTime = 0.15f;
    /// <summary>注视对齐角度误差下限（°）：两方向夹角小于该值视为已同向，不做插值。</summary>
    const float LookAlignAngleEpsilon = 0.01f;
    #endregion

    Vector3 wallNormal;   // 最近一次检测到的墙面法线（朝向角色）
    Vector3 wallPoint;    // 最近一次检测到的墙面锚点（命中点平均）
    int wallMissFrames;   // 墙面检测连续失败帧数
    int climbDownGroundedFrames;  // 向下爬且持续着地的连续帧数（达 climbDownExitFrames 自动退出）
    int topOutProbeMissCount;     // 到顶探测射线连续未命中帧数（达 topOutProbeMissFrames 触发登顶）
    bool topOutProbeLeftHand;     // 最近一次到顶探测选中的手（取实际较高的那只）：true = 左手

    #region 攀爬手部 IK（程序化目标：跟随头部的水平线 + 中线对称姿态）
    // 骨骼与目标（Enter 缓存；攀爬期间 RigBuilder 每帧读取 target 世界位姿）
    Transform climbHead;            // 头部骨骼（水平线锚点）
    Transform climbLeftTip;         // 左手约束 tip（腕骨；调试对比用）
    Transform climbRightTip;
    Transform climbLeftTarget;      // 左手 TwoBoneIK target
    Transform climbRightTarget;     // 右手 TwoBoneIK target
    bool climbIkReady;              // 装配完整（头部 + 双手 target 均非空）；false 时跳过手部 IK

    bool climbHandPhase;            // 姿态相位：false / true（上下换手 / 收开互换）
    int climbHandLastCategory;      // 上次输入方向类别（0 静止 / ±1 左右 / ±2 上下）
    float climbStepTimer;       // 步进交替计时
    Vector2 climbHandLOffset;       // 当前（平滑后）左手姿态偏移（x=横向沿墙面右轴，y=高度沿墙面上轴）
    Vector2 climbHandROffset;       // 当前（平滑后）右手姿态偏移
    Vector2 climbHandLOffsetTarget; // 姿态目标偏移（方向类别/步进交替时更新）
    Vector2 climbHandROffsetTarget; // 姿态目标偏移
    Quaternion climbHandLRotation;  // 当前（平滑后）左手 target 旋转（墙面基准 × 左手偏移）
    Quaternion climbHandRRotation;  // 当前（平滑后）右手 target 旋转
    #endregion

    // 攀爬头部 IK（head Multi-Aim 的 source[0] = "head look target"；无外部写入，本状态内写）
    Transform climbHeadLookTarget;  // head Multi-Aim 的目标 transform
    bool climbHeadLookReady;        // 头部骨骼 + 目标均非空
    Vector3 climbHeadLookDir;       // 当前头部注视方向（逐帧平滑）

    // 攀爬脚部 IK（TwoBoneIK tip = 脚踝；target 场景装配为 left/right leg ik target）
    Transform climbHips;             // 臀部骨骼（脚部基准线锚点）
    Transform climbLeftLegTarget;    // 左脚 TwoBoneIK target
    Transform climbRightLegTarget;   // 右脚 TwoBoneIK target
    Transform climbLeftFootTip;      // 左脚踝（调试）
    Transform climbRightFootTip;     // 右脚踝（调试）
    bool climbFootIkReady;           // 臀部 + 双脚 target 均非空
    Vector2 climbFootLOffset;        // 当前（平滑后）左脚偏移（x=横向，y=高度，相对臀部基准线）
    Vector2 climbFootROffset;
    Vector2 climbFootLOffsetTarget;  // 左脚目标偏移
    Vector2 climbFootROffsetTarget;
    Quaternion climbFootLRotation;   // 当前左脚 target 旋转（墙面基准 × 左脚偏移）
    Quaternion climbFootRRotation;

    // 攀爬身体 IK（body Multi-Aim：约束 Chest；source[0] = "body look target"）
    Transform climbBodyLookTarget;   // body Multi-Aim 的目标 transform
    Transform climbBodyLookOrigin;   // 被约束骨骼（Chest；目标点以此为原点）
    bool climbBodyLookReady;
    Vector3 climbBodyLookDir;        // 当前身体注视方向（逐帧平滑）

    public override void Enter(PlayerContext ctx)
    {
        // 进入即覆盖式复测一次（信号边刚用严格条件探测通过，一般直接命中）；失败按"立即计入退出"处理（防御）
        if (TryRefreshWall(ctx, out wallNormal, out wallPoint))
        {
            wallMissFrames = 0;
        }
        else
        {
            wallMissFrames = ctx.Values.climbExitMissFrames;
        }

        // 攀爬不应用重力：垂直速度清 0（水平速度语义在攀爬期间无效，一并清 0，退出后由地面状态接管）
        var velocity = ctx.Motion.Velocity;
        velocity.x = 0f;
        velocity.z = 0f;
        velocity.y = 0f;
        ctx.Motion.Velocity = velocity;

        climbDownGroundedFrames = 0;
        ctx.ClimbDownReachedGround = false;
        ctx.ClimbWallLost = false;
        topOutProbeMissCount = 0;
        topOutProbeLeftHand = false;
        ctx.TopOutGrabLeftHand = false;   // 防御默认右手；登顶请求前会按探测手重写
        ctx.TopOutProbeActive = false;

        BeginHandIk(ctx);
        BeginFootIk(ctx);
        BeginBodyLook(ctx);
    }

    public override void Exit(PlayerContext ctx)
    {
        // IK 权重归零由动画层负责（ik 层退出 climb 时的过渡动画/曲线），脚本不写 IK 权重参数
        // 手部 IK 无其他需要还原的层权重；只关掉调试可视化
        ctx.ClimbIkDebugActive = false;
        ctx.TopOutProbeActive = false;
    }

    public override void Tick(PlayerContext ctx)
    {
        RotateTowardWall(ctx);

        // 墙面覆盖式复测：成功则更新墙面平面与去抖计数；失败保留最近有效墙面（瞬时断测不抖动），
        // 连续失败 climbExitMissFrames 帧后置 ClimbWallLost 并提议回地面（请求边裁决）
        if (TryRefreshWall(ctx, out Vector3 normal, out Vector3 point))
        {
            wallNormal = normal;
            wallPoint = point;
            wallMissFrames = 0;
        }
        else
        {
            wallMissFrames++;
        }

        // 到顶探测优先于脱墙退出：墙顶附近“顶部射线缺失”通常与“手腕越过墙顶”同帧出现，
        // 若先判脱墙会把角色驱逐回地面而不是登顶
        if (TryRequestTopOut(ctx)) return;

        // 脱墙退出（去抖达标才驱逐）
        if (wallMissFrames >= ctx.Values.climbExitMissFrames)
        {
            ctx.ClimbWallLost = true;
            ctx.RequestTransition(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground);
            return;
        }

        // 向下爬至可着陆：达标即请求回 Normal 地面姿态（切换已发生时本帧不再推进攀爬逻辑）
        if (TryRequestDownClimbExit(ctx)) return;

        // 头部注视：方向 = 移动方向（最大转角 Values.climbHeadLookAngle 可调）；无输入保持上一角度
        UpdateHeadLook(ctx);
        // 身体注视：方向 = 移动方向（最大转角 Values.climbBodyLookAngle，比头小）；无输入保持上一角度
        UpdateBodyLook(ctx);

        UpdateClimbIk(ctx);
        WriteAnimatorParams(ctx);
    }

    /// <summary>
    /// 向下爬至可着陆的自动退出：持续按“下”（S，方向类别 -2）且 GroundProbe 去抖后的着地标志
    /// （ctx.IsGrounded）连续达 Values.climbDownExitFrames 帧 → 置 ctx.ClimbDownReachedGround 并请求
    /// 回 Normal 地面姿态（“退出攀爬”请求边 ③c 的条件并入该标志）。帧数 ≤ 0 = 关闭；条件任一
    /// 不满足即清零计数。返回 true 表示本帧已请求切换（调用方应停止推进本帧攀爬逻辑）。
    /// </summary>
    bool TryRequestDownClimbExit(PlayerContext ctx)
    {
        int required = ctx.Values.climbDownExitFrames;
        if (required <= 0)
        {
            ctx.ClimbDownReachedGround = false;
            return false;
        }

        bool down = ClimbDirCategory(ctx.Input.Move) == -2;
        if (down && ctx.IsGrounded)
        {
            climbDownGroundedFrames++;
            if (climbDownGroundedFrames >= required)
            {
                ctx.ClimbDownReachedGround = true;
                ctx.RequestTransition(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground);
                return true;
            }
        }
        else
        {
            climbDownGroundedFrames = 0;
            ctx.ClimbDownReachedGround = false;
        }
        return false;
    }

    #region 登顶到顶探测（手部射线 → 进入 ClimbTopOut）
    /// <summary>
    /// 手部到顶探测并请求进入登顶：从较高手的手腕上方（向上/手掌方向两段偏差）沿墙面方向射一根短射线，
    /// 连续 topOutProbeMissFrames 帧未命中墙面 = 该手已越过墙顶边缘 → 把该手写入 ctx.TopOutGrabLeftHand
    /// （登顶状态据此对同一只手 MatchTarget）并请求 ClimbTopOut。向下输入（S）期间不触发，避免“想下爬却在
    /// 顶缘附近被拉进登顶”。返回 true 表示本帧已请求切换（调用方应停止推进本帧攀爬逻辑）。
    /// </summary>
    bool TryRequestTopOut(PlayerContext ctx)
    {
        if (!ctx.Values.topOutAutoTrigger || ClimbDirCategory(ctx.Input.Move) == -2)
        {
            topOutProbeMissCount = 0;
            ctx.TopOutProbeActive = false;
            return false;
        }

        if (TryDetectReachedTop(ctx))
        {
            topOutProbeMissCount++;
        }
        else
        {
            topOutProbeMissCount = 0;
        }

        int required = ctx.Values.topOutProbeMissFrames;
        if (required <= 0 || topOutProbeMissCount < required) return false;

        ctx.TopOutGrabLeftHand = topOutProbeLeftHand;
        ctx.RequestTransition(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.ClimbTopOut);
        return true;
    }

    /// <summary>
    /// 到顶判定（单帧）：取世界高度较高的那只手对应的 TwoBoneIK target（脚本每帧写入的权威“手掌”位，
    /// 不受 IK 收敛滞后影响；装配缺失时回退手骨），从该 target 上方 topOutProbeUpOffset 处再沿墙面法线
    /// （远离墙面方向）外推 topOutProbePalmOffset 作为起点——保证起点在墙体外侧，避免 Raycast 起点
    /// 处于碰撞体内部时永不命中（正常爬墙被误判到顶的根因）。射线向墙面（-wallNormal）射出：
    /// - 命中【墙面侧面】（命中法线接近水平，排除越过墙顶后打中平台顶面/墙后物体的情形）→ 手在该高度
    ///   仍贴墙，未到顶（返回 false）；
    /// - 未命中侧面 → 手掌上方已越过墙顶，可登顶（返回 true）。
    /// 探测结果（射线/是否命中）写入 ctx.TopOutProbe* 供 Scene Gizmos 调试。
    /// </summary>
    bool TryDetectReachedTop(PlayerContext ctx)
    {
        ctx.TopOutProbeAnchor = default;
        ctx.TopOutProbeOrigin = default;
        ctx.TopOutProbeEnd = default;
        ctx.TopOutProbeHitWall = false;
        ctx.TopOutProbeActive = false;   // 只有本帧真正发射了探测射线才视为数据有效
        if (wallNormal.sqrMagnitude < MinNormalSqr) return false;

        // 锚点优先 TwoBoneIK target（脚本权威“手掌”位，进入攀爬当帧即写入墙面坐标）；
        // 装配缺失（climbIkReady = false）时回退手骨/约束 tip
        Transform leftAnchor = climbLeftTarget != null ? climbLeftTarget
            : (climbLeftTip != null ? climbLeftTip : ctx.Animator.GetBoneTransform(HumanBodyBones.LeftHand));
        Transform rightAnchor = climbRightTarget != null ? climbRightTarget
            : (climbRightTip != null ? climbRightTip : ctx.Animator.GetBoneTransform(HumanBodyBones.RightHand));
        if (leftAnchor == null || rightAnchor == null) return false;

        // 交替攀爬中上侧手 = 世界高度较高的那只（与 climbHandPhase 方向语义一致，但直接用高度更鲁棒）
        Transform probeAnchor = leftAnchor.position.y >= rightAnchor.position.y ? leftAnchor : rightAnchor;
        topOutProbeLeftHand = probeAnchor == leftAnchor;
        ctx.TopOutProbeAnchor = probeAnchor.position;

        Vector3 dir = -wallNormal;
        if (dir.sqrMagnitude < MinNormalSqr) return false;
        dir.Normalize();

        ctx.TopOutProbeActive = true;
        // 起点 = 手掌锚点上方 + 沿墙面法线（远离墙）外拉，保证在墙碰撞体外
        Vector3 origin = probeAnchor.position
                         + Vector3.up * ctx.Values.topOutProbeUpOffset
                         + wallNormal * ctx.Values.topOutProbePalmOffset;
        float length = Mathf.Max(0f, ctx.Values.topOutProbeRayLength);
        bool hitWall = Physics.Raycast(origin, dir, out RaycastHit wallHit, length, ctx.WallProbe.climbableLayer)
                       && Mathf.Abs(wallHit.normal.y) < 0.5f;   // 只认墙面侧面（法线近水平）；顶面/墙后地面不算“还在爬”

        ctx.TopOutProbeOrigin = origin;
        // 命中时只画到墙面表面（不把线画进墙里被遮挡）；未命中才画满射线长度
        ctx.TopOutProbeEnd = hitWall ? wallHit.point : origin + dir * length;
        ctx.TopOutProbeHitWall = hitWall;
        return !hitWall;
    }
    #endregion

    /// <summary>
    /// 墙面覆盖式复测（攀爬中每帧/进入用）：全部网格射线发射统计，允许部分射线缺失——例如头顶
    /// 射线越过墙顶（为后续登顶检测留出触发窗口，不因顶部少数缺失就脱墙）。
    /// 仍贴墙的条件：命中点几何校验通过，且缺失比例 < Values.climbWallExitMissRatio（默认 < 50%）；
    /// 缺失 ≥ 阈值（默认恰好一半起）即视为本帧脱墙（计入 wallMissFrames 去抖）。
    /// </summary>
    bool TryRefreshWall(PlayerContext ctx, out Vector3 normal, out Vector3 point)
    {
        normal = default;
        point = default;
        if (!ctx.WallProbe.EvaluateCoverage(ctx.Transform, ctx.Transform.forward,
                out Vector3 n, out Vector3 p, out int hitCount, out int totalCount))
        {
            return false;
        }

        float missRatio = totalCount > 0 ? 1f - (float)hitCount / totalCount : 1f;
        if (missRatio >= ctx.Values.climbWallExitMissRatio) return false;

        normal = n;
        point = p;
        return true;
    }

    #region 攀爬头部 IK（head Multi-Aim：方向 = 移动方向）
    /// <summary>
    /// 头部 IK 注视写入：climb ik.anim 已把 head ik weight 置 1，但 head Multi-Aim 的目标
    /// 无人写入（瞄准状态只在瞄准时写），头部会停在装配位。本方法把目标点写到“头部前方”：
    /// - 有输入：注视方向 = 墙面正向（看墙）向“移动方向”（墙上上下/左右）转动，最大转角
    ///   = Values.climbHeadLookAngle（°）——方向跟随移动方向、偏转角度可调；
    /// - 无输入：保持当前注视角度（不强制回正——四肢保持攀爬姿态时头突然回正很怪）；
    /// - 方向经 climbHeadLookDir 平滑（ClimbHeadLookSmoothTime），避免键位/方向切换时头部瞬跳。
    /// Multi-Aim 只以目标点位置定方向，因此把点放在头部前方 ClimbHeadLookDistance 处即可。
    /// </summary>
    void UpdateHeadLook(PlayerContext ctx)
    {
        if (!climbHeadLookReady || climbHead == null || climbHeadLookTarget == null) return;
        if (wallNormal.sqrMagnitude < MinNormalSqr) return;

        Vector3 forward = ctx.Transform.forward;
        Vector3 desired = climbHeadLookDir;   // 默认保持上一角度：无输入不回正
        Vector2 move = ctx.Input.Move;
        if (move.sqrMagnitude > ctx.Values.minMoveSqrMagnitude)
        {
            Vector3 wallUp = ClimbWallUp(wallNormal);
            Vector3 wallRight = ClimbWallRight(wallNormal, wallUp);
            Vector3 moveDir = wallUp * move.y + wallRight * move.x;
            if (moveDir.sqrMagnitude > MinNormalSqr)
            {
                desired = LookDirTowardMove(forward, moveDir.normalized, ctx.Values.climbHeadLookAngle);
            }
        }

        climbHeadLookDir = SmoothLookDir(climbHeadLookDir, desired, ClimbHeadLookSmoothTime);
        climbHeadLookTarget.position = climbHead.position + climbHeadLookDir * ClimbHeadLookDistance;
    }
    #endregion

    #region 攀爬身体 IK（body Multi-Aim：方向 = 移动方向，角度比头小）
    /// <summary>
    /// 进入攀爬：缓存 body Multi-Aim 的被约束骨骼（Chest）与 source[0]（body look target）。
    /// 装配不完整时告警并停用身体 IK。
    /// </summary>
    void BeginBodyLook(PlayerContext ctx)
    {
        Transform target = null;
        Transform origin = null;
        if (ctx.BodyAimConstraint != null)
        {
            if (ctx.BodyAimConstraint.data.sourceObjects.Count > 0)
            {
                target = ctx.BodyAimConstraint.data.sourceObjects[0].transform;
            }
            origin = ctx.BodyAimConstraint.data.constrainedObject;
        }
        if (origin == null) origin = climbHips;

        climbBodyLookReady = origin != null && target != null;
        if (!climbBodyLookReady)
        {
            Debug.LogWarning("[ClimbIK] 攀爬身体 IK 装配不完整（body Multi-Aim 的被约束骨骼或目标为空），已停用身体 IK（不影响攀爬）。");
            return;
        }

        climbBodyLookTarget = target;
        climbBodyLookOrigin = origin;
        climbBodyLookDir = ctx.Transform.forward;
    }

    /// <summary>
    /// 身体注视写入：方向 = 墙面正向向“移动方向”转动，最大转角 Values.climbBodyLookAngle（比头小）。
    /// 无输入保持上一角度（不回正，与头一致）；目标点放在被约束骨骼（Chest）前方，
    /// Multi-Aim 以 Chest → 目标点定方向。注意：body aim constraint 当前只约束 Y 轴（左右），
    /// 上下是否可见取决于其 constrainedXAxis 是否开启。
    /// </summary>
    void UpdateBodyLook(PlayerContext ctx)
    {
        if (!climbBodyLookReady || climbBodyLookOrigin == null || climbBodyLookTarget == null) return;
        if (wallNormal.sqrMagnitude < MinNormalSqr) return;

        Vector3 forward = ctx.Transform.forward;
        Vector3 desired = climbBodyLookDir;   // 默认保持上一角度：无输入不回正
        Vector2 move = ctx.Input.Move;
        if (move.sqrMagnitude > ctx.Values.minMoveSqrMagnitude)
        {
            Vector3 wallUp = ClimbWallUp(wallNormal);
            Vector3 wallRight = ClimbWallRight(wallNormal, wallUp);
            Vector3 moveDir = wallUp * move.y + wallRight * move.x;
            if (moveDir.sqrMagnitude > MinNormalSqr)
            {
                desired = LookDirTowardMove(forward, moveDir.normalized, ctx.Values.climbBodyLookAngle);
            }
        }

        climbBodyLookDir = SmoothLookDir(climbBodyLookDir, desired, ClimbBodyLookSmoothTime);
        climbBodyLookTarget.position = climbBodyLookOrigin.position + climbBodyLookDir * ClimbBodyLookDistance;
    }
    #endregion

    public override void OnAnimatorMove(PlayerContext ctx)
    {
        // 攀爬位移全量脚本接管：输入映射到墙面切平面 + 贴墙收敛（早期原型 同款）
        if (wallNormal.sqrMagnitude < MinNormalSqr)
        {
            return;
        }

        Vector3 delta = ComputeClimbDelta(ctx);
        delta += ComputeWallHugDelta(ctx);
        ctx.CharacterController.Move(delta);
    }

    /// <summary>墙面切平面位移：输入映射到墙上坐标（W=上、S=下、D=右、A=左）。</summary>
    Vector3 ComputeClimbDelta(PlayerContext ctx)
    {
        Vector3 wallUp = ClimbWallUp(wallNormal);
        Vector3 wallRight = ClimbWallRight(wallNormal, wallUp);

        Vector3 moveDir = wallUp * ctx.Input.Move.y + wallRight * ctx.Input.Move.x;
        return moveDir * ctx.Values.climbSpeed * Time.deltaTime;
    }

    /// <summary>沿法线收敛到目标贴墙距离（让手部 IK 目标落在臂展内）。</summary>
    Vector3 ComputeWallHugDelta(PlayerContext ctx)
    {
        float distance = Vector3.Dot(ctx.Transform.position - wallPoint, wallNormal);
        float correction = ctx.Values.climbWallHugDistance - distance;
        return wallNormal * correction * Mathf.Clamp01(ctx.Values.climbWallHugSpeed * Time.deltaTime);
    }

    /// <summary>朝向墙面：身体平面垂直于墙面法线（面向墙），up 沿墙面切平面。</summary>
    void RotateTowardWall(PlayerContext ctx)
    {
        if (wallNormal.sqrMagnitude < MinNormalSqr) return;

        Vector3 wallUp = ClimbWallUp(wallNormal);
        Quaternion target = Quaternion.LookRotation(-wallNormal, wallUp);
        ctx.Transform.rotation = Quaternion.RotateTowards(ctx.Transform.rotation, target,
                                                          ctx.Values.rotateSpeed * Time.deltaTime);
    }

    void WriteAnimatorParams(PlayerContext ctx)
    {
        var anim = ctx.Animator;
        // 攀爬动画分支不消费水平/垂直速度参数（body posture = 2 驱动），写 0
        anim.SetFloat(PlayerControllerScript.AnimVerticalSpeed, IdleSpeedParam);
        anim.SetFloat(PlayerControllerScript.AnimHorizontalSpeed, IdleSpeedParam);
        anim.SetFloat(PlayerControllerScript.AnimFallingSpeed, IdleSpeedParam);
    }

    #region 攀爬手部 IK
    /// <summary>
    /// 进入攀爬：缓存骨骼与双手 IK target；初始姿态 = 双手落在水平线上、中线两侧（避免从远处飞过来）；
    /// 装配不完整（缺头部/任一 target）时告警并停用攀爬手部 IK（身体攀爬不受影响）。
    /// </summary>
    void BeginHandIk(PlayerContext ctx)
    {
        climbHead = ctx.Animator.GetBoneTransform(HumanBodyBones.Head);
        // 头部 IK 目标：head Multi-Aim 的第一个源对象（场景装配为 "head look target"）
        Transform headLookTarget = null;
        if (ctx.HeadAimConstraint != null && ctx.HeadAimConstraint.data.sourceObjects.Count > 0)
        {
            headLookTarget = ctx.HeadAimConstraint.data.sourceObjects[0].transform;
        }
        climbHeadLookReady = climbHead != null && headLookTarget != null;
        if (climbHeadLookReady)
        {
            climbHeadLookTarget = headLookTarget;
            climbHeadLookDir = ctx.Transform.forward;
        }

        climbLeftTip = ctx.LeftHandConstraint != null ? ctx.LeftHandConstraint.data.tip : null;
        climbRightTip = ctx.RightHandConstraint != null ? ctx.RightHandConstraint.data.tip : null;
        if (climbLeftTip == null) climbLeftTip = ctx.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
        if (climbRightTip == null) climbRightTip = ctx.Animator.GetBoneTransform(HumanBodyBones.RightHand);
        climbLeftTarget = ctx.LeftHandConstraint != null ? ctx.LeftHandConstraint.data.target : null;
        climbRightTarget = ctx.RightHandConstraint != null ? ctx.RightHandConstraint.data.target : null;

        climbIkReady = climbHead != null && climbLeftTarget != null && climbRightTarget != null;
        if (!climbIkReady)
        {
            Debug.LogWarning("[ClimbIK] 攀爬手部 IK 装配不完整（头部骨骼或左/右手 TwoBoneIK 的 target 为空），已停用攀爬手部 IK（身体攀爬不受影响）。请检查 Rig 上左右手 TwoBoneIK 的 target 指派。");
            return;
        }

        climbHandPhase = false;
        climbHandLastCategory = 0;
        climbStepTimer = 0f;

        // 初始偏移：水平线（y=0）+ 中线两侧（x=±中线距离）
        climbHandLOffset = new Vector2(-ctx.Values.climbHandCenterDist, 0f);
        climbHandROffset = new Vector2(ctx.Values.climbHandCenterDist, 0f);
        climbHandLOffsetTarget = climbHandLOffset;
        climbHandROffsetTarget = climbHandROffset;
        // 初始旋转 = 墙面基准 × 各自偏移（立即到位，不做帧间插值，避免进入帧从旧旋转跳变）
        climbHandLRotation = ComputeClimbHandRotation(ctx, ctx.Values.climbLeftHandRotationOffset);
        climbHandRRotation = ComputeClimbHandRotation(ctx, ctx.Values.climbRightHandRotationOffset);
        WriteHandTargets(ctx);
    }

    /// <summary>
    /// 每帧手/脚 IK 姿态推进（共用同一节奏 climbStepInterval / climbSwitchTime）：
    /// 输入方向类别（0 静止 / ±1 左右 / ±2 上下）→ 类别切换立即交替相位、
    /// 持续同向按步进间隔交替、静止保持姿态 → 手脚偏移分别平滑 → 各自目标合成贴墙。
    /// </summary>
    void UpdateClimbIk(PlayerContext ctx)
    {
        ctx.ClimbIkDebugActive = climbIkReady;   // 仅表示本帧数据有效；是否绘制由主体类 showClimbIkGizmos 开关决定
        if (wallNormal.sqrMagnitude < MinNormalSqr) return;

        int category = ClimbDirCategory(ctx.Input.Move);
        if (category == 0)
        {
            // 无输入：保持当前手脚姿态
            climbStepTimer = 0f;
            climbHandLastCategory = 0;
        }
        else if (category != climbHandLastCategory)
        {
            // 方向类别变化：立即交替姿态（不让旧姿态"沿用"到新方向）
            climbHandPhase = !climbHandPhase;
            climbHandLastCategory = category;
            ComputeClimbHandPose(ctx, category);
            ComputeClimbFootPose(ctx, category);
            climbStepTimer = 0f;
        }
        else
        {
            // 持续同方向移动：按间隔步进交替（两种姿态间循环）
            climbStepTimer += Time.deltaTime;
            if (climbStepTimer >= ctx.Values.climbStepInterval)
            {
                climbHandPhase = !climbHandPhase;
                climbStepTimer = 0f;
                ComputeClimbHandPose(ctx, category);
                ComputeClimbFootPose(ctx, category);
            }
        }

        // 姿态偏移在约 climbSwitchTime 秒内平滑过渡（帧率无关的指数式逼近）
        float t = ctx.Values.climbSwitchTime > MinSwitchTime
            ? Mathf.Clamp01(Time.deltaTime / ctx.Values.climbSwitchTime)
            : 1f;
        climbHandLOffset = Vector2.Lerp(climbHandLOffset, climbHandLOffsetTarget, t);
        climbHandROffset = Vector2.Lerp(climbHandROffset, climbHandROffsetTarget, t);
        if (climbFootIkReady)
        {
            climbFootLOffset = Vector2.Lerp(climbFootLOffset, climbFootLOffsetTarget, t);
            climbFootROffset = Vector2.Lerp(climbFootROffset, climbFootROffsetTarget, t);
        }

        if (climbIkReady) WriteHandTargets(ctx);
        if (climbFootIkReady) WriteFootTargets(ctx);
    }

    /// <summary>
    /// 计算双手期望偏移（墙面坐标：x = 横向沿墙面右轴，y = 高度沿墙面上轴）。
    /// - 上下（±2）：中线两侧固定 climbHandCenterDist；高度 = 水平线 ± climbHandAlternateLength，
    ///   相位决定"哪只手在上"（交替长度一致、上下对称）；
    /// - 左右（±1）：对应移动方向侧的手在水平线下方 climbHandPlaceDist、相反侧手上方同值；
    ///   横向 = 收拢/打开（中线距离 ∓/+ 开合幅度）随相位交替（双手幅度一致）。
    /// </summary>
    void ComputeClimbHandPose(PlayerContext ctx, int category)
    {
        Vector2 leftTarget;
        Vector2 rightTarget;
        float center = ctx.Values.climbHandCenterDist;

        if (category == 1 || category == -1)
        {
            float lateral = climbHandPhase
                ? center + ctx.Values.climbHandOpenAmount                       // 打开（外开于肩膀外）
                : Mathf.Max(0f, center - ctx.Values.climbHandOpenAmount);       // 收拢（内收于头下）
            float place = ctx.Values.climbHandPlaceDist;
            if (category == 1)
            {
                // 向右：右手（对应侧）在下方、左手在上方
                leftTarget = new Vector2(-lateral, place);
                rightTarget = new Vector2(lateral, -place);
            }
            else
            {
                // 向左：左手（对应侧）在下方、右手在上方
                leftTarget = new Vector2(-lateral, -place);
                rightTarget = new Vector2(lateral, place);
            }
        }
        else
        {
            float alternate = ctx.Values.climbHandAlternateLength;
            if (climbHandPhase)
            {
                // 左手在上、右手在下
                leftTarget = new Vector2(-center, alternate);
                rightTarget = new Vector2(center, -alternate);
            }
            else
            {
                // 右手在上、左手在下
                leftTarget = new Vector2(-center, -alternate);
                rightTarget = new Vector2(center, alternate);
            }
        }

        climbHandLOffsetTarget = leftTarget;
        climbHandROffsetTarget = rightTarget;
    }

    /// <summary>
    /// 目标合成并写入：水平线锚点 = 头部 + 墙面上轴 × climbHandLineHeight（跟随头部）；
    /// 墙面右/上轴由墙面法线推出（与角色朝向一致：forward = -法线）；
    /// 合成后投影到当前墙面平面（贴墙），再沿墙面法线向角色方向外推 climbHandWallNormalOffset——
    /// TwoBoneIK target 定位的是腕骨，腕骨不是掌心；不后退时掌心/手会陷入墙内。
    /// </summary>
    void WriteHandTargets(PlayerContext ctx)
    {
        if (!climbIkReady || wallNormal.sqrMagnitude < MinNormalSqr) return;

        Vector3 wallUp = ClimbWallUp(wallNormal);
        Vector3 wallRight = ClimbWallRight(wallNormal, wallUp);
        Vector3 anchor = climbHead.position + wallUp * ctx.Values.climbHandLineHeight;
        float wallOffset = ctx.Values.climbHandWallNormalOffset;

        Vector3 lPos = anchor + wallRight * climbHandLOffset.x + wallUp * climbHandLOffset.y;
        Vector3 rPos = anchor + wallRight * climbHandROffset.x + wallUp * climbHandROffset.y;
        climbLeftTarget.position = ProjectToWall(lPos) + wallNormal * wallOffset;
        climbRightTarget.position = ProjectToWall(rPos) + wallNormal * wallOffset;

        // 旋转写入：target.rotation = 墙面基准旋转 × 左右手偏移；随 climbSwitchTime 平滑（与位置一致），
        // 保证 Inspector 改偏移时手腕不瞬跳。约束 targetRotationWeight=1 且不保留偏移 → 该旋转即腕骨最终朝向。
        if (ctx.Values.climbHandWriteRotation)
        {
            float t = ctx.Values.climbSwitchTime > MinSwitchTime
                ? Mathf.Clamp01(Time.deltaTime / ctx.Values.climbSwitchTime)
                : 1f;
            climbHandLRotation = Quaternion.Slerp(climbHandLRotation,
                ComputeClimbHandRotation(ctx, ctx.Values.climbLeftHandRotationOffset), t);
            climbHandRRotation = Quaternion.Slerp(climbHandRRotation,
                ComputeClimbHandRotation(ctx, ctx.Values.climbRightHandRotationOffset), t);
            climbLeftTarget.rotation = climbHandLRotation;
            climbRightTarget.rotation = climbHandRRotation;
        }

        if (ctx.ClimbIkDebugActive)
        {
            ctx.ClimbLeftTarget = climbLeftTarget.position;
            ctx.ClimbRightTarget = climbRightTarget.position;
            ctx.ClimbLeftHandPos = climbLeftTip != null ? climbLeftTip.position : lPos;
            ctx.ClimbRightHandPos = climbRightTip != null ? climbRightTip.position : rPos;
        }
    }

    /// <summary>
    /// 计算手部 target 的目标世界旋转：墙面基准旋转 × 偏移欧拉。
    /// 墙面基准 = 角色贴墙朝向（forward = -法线朝向墙面、up = 墙面切平面上轴），
    /// 即 RotateTowardWall 使用的同一坐标系——角色对齐墙面后，基准系固定不随身体动画摆动。
    /// </summary>
    Quaternion ComputeClimbHandRotation(PlayerContext ctx, Vector3 eulerOffset)
    {
        // 防御：墙面法线无效（进入时探测失败等）时回退到角色自身朝向，避免 LookRotation 零向量
        if (wallNormal.sqrMagnitude < MinNormalSqr)
        {
            return ctx.Transform.rotation * Quaternion.Euler(eulerOffset);
        }
        Vector3 wallUp = ClimbWallUp(wallNormal);
        Quaternion wallBase = Quaternion.LookRotation(-wallNormal, wallUp);
        return wallBase * Quaternion.Euler(eulerOffset);
    }

    /// <summary>把位置投影到经过 wallPoint、法线为 wallNormal 的墙面上。</summary>
    Vector3 ProjectToWall(Vector3 pos)
    {
        if (wallNormal.sqrMagnitude < MinNormalSqr) return pos;
        return pos - wallNormal * Vector3.Dot(pos - wallPoint, wallNormal);
    }

    /// <summary>输入方向类别：0 静止，1 右 / -1 左，2 上 / -2 下（与 早期原型 ClimbDirCategory 一致）。</summary>
    static int ClimbDirCategory(Vector2 dir)
    {
        if (dir.sqrMagnitude < InputDeadZoneSqr) return 0;
        return Mathf.Abs(dir.x) > Mathf.Abs(dir.y) ? (dir.x > 0 ? 1 : -1) : (dir.y > 0 ? 2 : -2);
    }

    /// <summary>墙面坐标系的“上”轴（世界 up 在墙面切平面上的投影）。</summary>
    static Vector3 ClimbWallUp(Vector3 wallNormal)
        => Vector3.ProjectOnPlane(Vector3.up, wallNormal).normalized;

    /// <summary>墙面坐标系的“右”轴（与角色贴墙朝向 -wallNormal 正交）。</summary>
    static Vector3 ClimbWallRight(Vector3 wallNormal, Vector3 wallUp)
        => Vector3.Cross(wallUp, -wallNormal).normalized;

    /// <summary>注视目标方向：从 forward 向 moveDir 最多转 maxAngleDeg°（Slerp 精确转角）。</summary>
    static Vector3 LookDirTowardMove(Vector3 forward, Vector3 moveDir, float maxAngleDeg)
    {
        float maxAngle = Mathf.Max(0f, maxAngleDeg);
        if (maxAngle <= 0f) return forward;
        float between = Vector3.Angle(forward, moveDir);
        float t = between > LookAlignAngleEpsilon ? Mathf.Clamp01(maxAngle / between) : 0f;
        return Vector3.Slerp(forward, moveDir, t).normalized;
    }

    /// <summary>注视方向平滑：帧率无关的指数式逼近（同 climbSwitchTime 风格）。</summary>
    static Vector3 SmoothLookDir(Vector3 current, Vector3 desired, float smoothTime)
    {
        float t = smoothTime > MinSwitchTime ? Mathf.Clamp01(Time.deltaTime / smoothTime) : 1f;
        return Vector3.Slerp(current, desired, t).normalized;
    }

    #region 攀爬脚部 IK（与手共用节奏 climbStepInterval / climbSwitchTime）
    /// <summary>
    /// 进入攀爬：缓存臀部骨骼与左右腿 TwoBoneIK 的 target/tip；初始双脚在“臀部基准线”两侧。
    /// 装配不完整时告警并停用脚部 IK（身体攀爬不受影响）。
    /// </summary>
    void BeginFootIk(PlayerContext ctx)
    {
        climbHips = ctx.Animator.GetBoneTransform(HumanBodyBones.Hips);
        climbLeftLegTarget = ctx.LeftLegConstraint != null ? ctx.LeftLegConstraint.data.target : null;
        climbRightLegTarget = ctx.RightLegConstraint != null ? ctx.RightLegConstraint.data.target : null;
        climbLeftFootTip = ctx.LeftLegConstraint != null ? ctx.LeftLegConstraint.data.tip : null;
        climbRightFootTip = ctx.RightLegConstraint != null ? ctx.RightLegConstraint.data.tip : null;
        if (climbLeftFootTip == null) climbLeftFootTip = ctx.Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        if (climbRightFootTip == null) climbRightFootTip = ctx.Animator.GetBoneTransform(HumanBodyBones.RightFoot);

        climbFootIkReady = climbHips != null && climbLeftLegTarget != null && climbRightLegTarget != null;
        if (!climbFootIkReady)
        {
            Debug.LogWarning("[ClimbIK] 攀爬脚部 IK 装配不完整（臀部骨骼或左/右腿 TwoBoneIK 的 target 为空），已停用脚部 IK（身体攀爬不受影响）。请检查 Rig 上左右腿 TwoBoneIK 的 target 指派。");
            return;
        }

        // 初始偏移：基准线（y=0）+ 中线两侧（避免进入帧从远处甩过来）
        float center = ctx.Values.climbFootCenterDist;
        climbFootLOffset = new Vector2(-center, 0f);
        climbFootROffset = new Vector2(center, 0f);
        climbFootLOffsetTarget = climbFootLOffset;
        climbFootROffsetTarget = climbFootROffset;
        // 初始旋转立即到位（不做帧间插值，避免进入帧跳变）
        climbFootLRotation = ComputeClimbFootRotation(ctx, ctx.Values.climbLeftFootRotationOffset);
        climbFootRRotation = ComputeClimbFootRotation(ctx, ctx.Values.climbRightFootRotationOffset);
        WriteFootTargets(ctx);
    }

    /// <summary>
    /// 计算双脚期望偏移（墙面坐标：x=横向，y=高度，相对“臀部 + climbFootLineHeight”基准线）。
    /// - 上下（±2）：与手同拍反相——climbHandPhase=true（左手在上）时左脚在下、右脚在上；
    /// - 左右（±1）：双脚与手同相【开合】——climbHandPhase=true 时打开（收拢偏移 + 开合幅度）、
    ///   false 时收拢（= 收拢偏移，不与中线并死）；两脚始终停在基准线上（y=0，不上下错动）。
    /// </summary>
    void ComputeClimbFootPose(PlayerContext ctx, int category)
    {
        if (!climbFootIkReady) return;

        float center = ctx.Values.climbFootCenterDist;
        if (category == 1 || category == -1)
        {
            // 与手同相开合（双脚都停在基准线上，不上下错动）
            float close = Mathf.Max(0f, ctx.Values.climbFootClosedOffset);
            float lateral = climbHandPhase
                ? close + ctx.Values.climbFootOpenAmount                        // 打开 = 收拢偏移 + 开合幅度
                : close;                                                        // 收拢 = 收拢偏移（不与中线并死）
            climbFootLOffsetTarget = new Vector2(-lateral, 0f);
            climbFootROffsetTarget = new Vector2(lateral, 0f);
        }
        else
        {
            // 与手同拍反相：climbHandPhase=true（左手在上）→ 左脚在下、右脚在上
            float alt = ctx.Values.climbFootAlternateLength;
            float leftY = climbHandPhase ? -alt : alt;
            float rightY = climbHandPhase ? alt : -alt;
            climbFootLOffsetTarget = new Vector2(-center, leftY);
            climbFootROffsetTarget = new Vector2(center, rightY);
        }
    }

    /// <summary>
    /// 脚部目标合成并写入：基准线锚点 = 臀部 + 墙面上轴 × climbFootLineHeight；
    /// 偏移合成后投影到墙面平面（贴墙），再沿墙面法线向角色方向外推 climbFootWallNormalOffset——
    /// TwoBoneIK target 定位的是脚踝而非脚掌/脚尖，不后退时脚会陷入墙内；
    /// 旋转 = 墙面基准 × 左右脚偏移欧拉，随共用 climbSwitchTime 平滑。
    /// </summary>
    void WriteFootTargets(PlayerContext ctx)
    {
        if (!climbFootIkReady || wallNormal.sqrMagnitude < MinNormalSqr) return;

        Vector3 wallUp = ClimbWallUp(wallNormal);
        Vector3 wallRight = ClimbWallRight(wallNormal, wallUp);
        Vector3 anchor = FootAnchor(ctx);
        float wallOffset = ctx.Values.climbFootWallNormalOffset;

        Vector3 lPos = anchor + wallRight * climbFootLOffset.x + wallUp * climbFootLOffset.y;
        Vector3 rPos = anchor + wallRight * climbFootROffset.x + wallUp * climbFootROffset.y;
        climbLeftLegTarget.position = ProjectToWall(lPos) + wallNormal * wallOffset;
        climbRightLegTarget.position = ProjectToWall(rPos) + wallNormal * wallOffset;

        float t = ctx.Values.climbSwitchTime > MinSwitchTime
            ? Mathf.Clamp01(Time.deltaTime / ctx.Values.climbSwitchTime)
            : 1f;
        climbFootLRotation = Quaternion.Slerp(climbFootLRotation,
            ComputeClimbFootRotation(ctx, ctx.Values.climbLeftFootRotationOffset), t);
        climbFootRRotation = Quaternion.Slerp(climbFootRRotation,
            ComputeClimbFootRotation(ctx, ctx.Values.climbRightFootRotationOffset), t);
        climbLeftLegTarget.rotation = climbFootLRotation;
        climbRightLegTarget.rotation = climbFootRRotation;
    }

    /// <summary>脚部基准线锚点（臀部 + 墙面上轴 × climbFootLineHeight）。</summary>
    Vector3 FootAnchor(PlayerContext ctx)
    {
        Vector3 wallUp = ClimbWallUp(wallNormal);
        Vector3 basePos = climbHips != null ? climbHips.position : ctx.Transform.position;
        return basePos + wallUp * ctx.Values.climbFootLineHeight;
    }

    /// <summary>脚部 target 的目标世界旋转：与手共用墙面基准旋转语义（target 轴向由旋转偏移微调）。</summary>
    Quaternion ComputeClimbFootRotation(PlayerContext ctx, Vector3 eulerOffset)
        => ComputeClimbHandRotation(ctx, eulerOffset);
    #endregion

    #endregion
}
