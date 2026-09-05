using UnityEngine;
using UnityEngine.Animations.Rigging;

/// <summary>
/// 状态上下文：角色各系统的【共享引用集合】，也是状态类的操作通道——
/// 状态类通过它【读】共享信息、【写】共享状态（Motion 速度）、【调用/改写】组件
/// （controller.Move 位移、transform 旋转、IK target 移动、Animator 参数写入）。
///
/// 有意只读的仅有：Input（玩家上报帧快照，只消费）、当前状态描述（Body/Hand/Handing，
/// 修改的唯一通道是机器裁决）、IsGrounded（物理事实，由主体类经 GroundProbe 每帧写入）。
///
/// 注意：早期"能力许可表"（PlayerAction/PlayerCapabilities）已随扁平状态机退役——
/// 行为合法性由"状态枚举中的合法组合 + 工厂映射 + 转换边存在性"编码（如持枪攀爬
/// 没有对应状态类与边，天然不可达）；状态内行为差异（如步枪不响应奔跑档）在
/// 各具体状态类内部直接实现。
///
/// 运动数值已接入（PlayerMotionValuesSO 资产，经 Values 访问；计算函数与可调参数字段见该类）；
/// 着地判定由主体类每帧经 GroundProbe 探测写入 IsGrounded（不使用 CharacterController.isGrounded，
/// 该标志在台阶/边缘存在帧级抽搐，见 GroundProbe）。
/// </summary>
public class PlayerContext
{
    public readonly Animator Animator;                                   // 写动画参数 / 查动画状态
    /// <summary>Animator 参数缓存写入器（同值跳过；状态类与主体类写速度/姿态参数统一走这里，见 PlayerAnimatorParams）。</summary>
    public readonly PlayerAnimatorParams AnimParams;
    public readonly CharacterController CharacterController;             // 执行位移：Move(delta)
    public readonly Transform Transform;                                 // 读写位置/旋转
    public readonly Camera MainCamera;
    public readonly PlayerInputState Input;                              // 帧快照（只消费，见类注释）

    #region 四肢 IK 约束（程序化 IK target 引用）
    // 用途：状态类移动 constraint.data.target 实现程序化 IK（攀爬手部目标、瞄准手部目标等）。
    // 注意：IK weight 由动画状态机全权管理，脚本（含本上下文）不读不写 constraint.weight。
    public readonly TwoBoneIKConstraint RightHandConstraint;
    public readonly TwoBoneIKConstraint LeftHandConstraint;
    public readonly TwoBoneIKConstraint RightLegConstraint;
    public readonly TwoBoneIKConstraint LeftLegConstraint;
    // 双臂 ChainIK（瞄准时用其 tip 捕获"腕-枪"相对位姿、并把推算结果写入其 target）。
    public readonly ChainIKConstraint RightArmChainConstraint;
    public readonly ChainIKConstraint LeftArmChainConstraint;
    #endregion

    #region 瞄准引导约束（Multi-Aim：头/胸指向目标；枪方向由轴点旋转程序化覆盖，不再使用 Multi-Aim）
    // 用途：瞄准状态每帧把各约束 SourceObjects[0] 的目标对象【位置】设为"相机中心射线命中点/远点"，
    // 让视线/上身指向瞄准点。只移动目标位置，约束引用（装配即定）不被改写。
    // 权重（constraint.weight）由动画状态机参数经 SetIKweight 回写，本上下文不写。
    public readonly MultiAimConstraint HeadAimConstraint;
    public readonly MultiAimConstraint BodyAimConstraint;

    /// <summary>枪根（PlayerControllerScript.Rifle）：瞄准时作为轴点的子级。</summary>
    public readonly Transform RifleRoot;
    /// <summary>手枪根（PlayerControllerScript.Pistol）：手枪正常状态左手 IK 计算用（未装配时手枪状态跳过左手写入并告警一次）。</summary>
    public readonly Transform PistolRoot;
    /// <summary>轴点（PlayerControllerScript.aimAxisPoint）：旋转不变点；代码只读，不写它的 transform。</summary>
    public readonly Transform AimAxisPoint;
    /// <summary>枪根作为轴点子级时的 localPosition（PlayerControllerScript.aimAxisOffset）。</summary>
    public readonly Vector3 RifleAxisOffset;
    /// <summary>手枪根作为轴点子级时的 localPosition（PlayerControllerScript.pistolAimAxisOffset；手枪与步枪到轴点距离不同，独立标定）。</summary>
    public readonly Vector3 PistolAxisOffset;
    /// <summary>退出瞄准时枪要恢复的父级（PlayerControllerScript.RightHandWrist；为空时用进入前父级）。</summary>
    public readonly Transform RightHandWrist;

    /// <summary>最近一帧瞄准落点（世界坐标；瞄准状态写入，Scene 视图 Gizmos 调试用；LastAimValid 表示是否有值）。</summary>
    public Vector3 LastAimPoint;
    /// <summary>最近一帧瞄准落点是否有效（瞄准状态写入）。</summary>
    public bool LastAimValid;
    /// <summary>最近一帧瞄准射线是否命中物体（false = 落点为远点）。</summary>
    public bool LastAimHit;

    /// <summary>瞄准 IK 接管是否激活（瞄准状态进入设 true / 退出设 false）：轴点旋转程序化覆盖据此门控。</summary>
    public bool AimRigActive { get; set; }

    /// <summary>本帧枪口应指向的瞄准点（世界坐标；瞄准状态每帧写入：过渡期 = 角色身前点，之后 = 视线远点）。</summary>
    public Vector3 GunAimPoint;

    /// <summary>
    /// 本帧轴点的世界旋转（确定性推进值）：瞄准状态 Tick 用 RotateTowards 推进并写入，
    /// PlayerControllerScript.LateUpdate 据此设置轴点 worldRotation，双手 ChainIK target
    /// 也用同一值推算——保证手/枪/轴点在同一帧使用相同旋转（消除"枪先转、手后算"的错位穿模）。
    /// </summary>
    public Quaternion AxisPointRotation;
    #endregion

    /// <summary>共享运动描述（跨状态交接槽）：当前状态类写入速度、新状态 Enter 读取（见 PlayerMotion）。</summary>
    public readonly PlayerMotion Motion = new PlayerMotion();

    /// <summary>运动数值服务（物理常量 + 纯计算函数，见 PlayerMotionValuesSO；资产单例，状态类读常量、调计算、写 Motion）。</summary>
    public readonly PlayerMotionValuesSO Values;

    /// <summary>墙面探测（参数化射线阵列，见 WallProbe）；攀爬进入检测与攀爬中逐帧重测共用。</summary>
    public readonly WallProbe WallProbe;

    #region 攀爬手部 IK 调试（攀爬状态每帧写入；PlayerControllerScript.OnDrawGizmos 读取绘制）
    /// <summary>攀爬手部 IK 调试数据是否有效（攀爬状态每帧写入；非攀爬恒 false）。是否显示由主体类 showClimbIkGizmos 开关决定。</summary>
    public bool ClimbIkDebugActive;
    /// <summary>左手 IK 目标位置（世界坐标）。</summary>
    public Vector3 ClimbLeftTarget;
    /// <summary>右手 IK 目标位置（世界坐标）。</summary>
    public Vector3 ClimbRightTarget;
    /// <summary>左手实际位置（TwoBoneIK tip / 手骨，世界坐标，调试对比用）。</summary>
    public Vector3 ClimbLeftHandPos;
    /// <summary>右手实际位置（TwoBoneIK tip / 手骨，世界坐标，调试对比用）。</summary>
    public Vector3 ClimbRightHandPos;
    #endregion

    #region 登顶到顶探测调试（攀爬状态每帧写入；PlayerControllerScript.OnDrawGizmos 读取绘制）
    /// <summary>到顶探测数据是否有效（本帧确实发射了探测射线；攀爬状态写入、进入登顶后恒 false）。是否显示由主体类 showClimbTopOutGizmos 开关决定。</summary>
    public bool TopOutProbeActive;
    /// <summary>最近一帧到顶探测选中的手掌锚点（TwoBoneIK target / 手骨，世界坐标；调试竖线用）。</summary>
    public Vector3 TopOutProbeAnchor;
    /// <summary>最近一帧到顶探测射线的起点（手骨上方 + 手掌方向偏差，世界坐标）。</summary>
    public Vector3 TopOutProbeOrigin;
    /// <summary>最近一帧到顶探测射线的可视终点：命中墙面侧面时 = 墙面命中点，未命中时 = 起点 + 墙面方向 × 射线长度。</summary>
    public Vector3 TopOutProbeEnd;
    /// <summary>最近一帧到顶探测是否命中墙面侧面（法线近水平；true = 仍在爬墙，false = 已越过墙顶、到顶候选）。</summary>
    public bool TopOutProbeHitWall;

    /// <summary>登顶抓取点调试数据是否有效（TopOut 状态 Enter 写入；退出登顶后恒 false）。是否显示由主体类 showClimbTopOutGizmos 开关决定。</summary>
    public bool TopOutGrabPointValid;
    /// <summary>最近一次登顶确定的抓取点（MatchTarget 目标位，世界坐标；品红球显示）。</summary>
    public Vector3 TopOutGrabPoint;
    /// <summary>墙顶扫描起点（世界坐标；与抓取点连线显示扫描方向）。</summary>
    public Vector3 TopOutGrabScanOrigin;
    /// <summary>抓取点是否来自墙顶扫描（true）；false = 扫描失败回退的 IK target 快照（橙球提示）。</summary>
    public bool TopOutGrabUsedScan;
    #endregion

    /// <summary>
    /// 攀爬自动退出请求标志（攀爬状态满足条件时置 true 并请求回 Normal 地面姿态；
    /// “退出攀爬”请求边 ③c 的条件并入这两个标志；攀爬状态 Enter 负责清零）：
    /// - ClimbDownReachedGround：持续向下输入 + GroundProbe 确认着地，连续达 Values.climbDownExitFrames 帧；
    /// - ClimbWallLost：墙面覆盖式复测缺失 ≥ Values.climbWallExitMissRatio 的射线且连续 climbExitMissFrames 帧。
    /// </summary>
    public bool ClimbDownReachedGround;
    public bool ClimbWallLost;

    /// <summary>
    /// 登顶时要固定的手掌（攀爬状态在到顶请求前写入，登顶状态 Enter 读取；true = 左手、false = 右手）。
    /// 值语义 = 到顶探测选择的那只较高手（交替攀爬中更接近墙顶、动画悬挂时按在边缘上的手）。
    /// </summary>
    public bool TopOutGrabLeftHand;

    readonly PlayerStateMachine machine;

    public PlayerContext(Animator animator, CharacterController characterController, Transform transform,
                         Camera mainCamera, PlayerInputState input, PlayerStateMachine machine,
                         TwoBoneIKConstraint rightHandConstraint, TwoBoneIKConstraint leftHandConstraint,
                         TwoBoneIKConstraint rightLegConstraint, TwoBoneIKConstraint leftLegConstraint,
                         PlayerMotionValuesSO values = null, WallProbe wallProbe = null,
                         MultiAimConstraint headAimConstraint = null,
                         MultiAimConstraint bodyAimConstraint = null,
                         ChainIKConstraint rightArmChainConstraint = null, ChainIKConstraint leftArmChainConstraint = null,
                         Transform rifleRoot = null, Transform aimAxisPoint = null,
                         Vector3 rifleAxisOffset = default, Transform rightHandWrist = null,
                         Transform pistolRoot = null, Vector3 pistolAxisOffset = default)
    {
        Animator = animator;
        AnimParams = new PlayerAnimatorParams(animator);
        CharacterController = characterController;
        Transform = transform;
        MainCamera = mainCamera;
        Input = input;
        this.machine = machine;
        RightHandConstraint = rightHandConstraint;
        LeftHandConstraint = leftHandConstraint;
        RightLegConstraint = rightLegConstraint;
        LeftLegConstraint = leftLegConstraint;
        RightArmChainConstraint = rightArmChainConstraint;
        LeftArmChainConstraint = leftArmChainConstraint;
        // 未在 Inspector 指派资产时用运行时默认实例兜底（内存对象、不落盘不共享；正常应指派资产）
        Values = values != null ? values : ScriptableObject.CreateInstance<PlayerMotionValuesSO>();
        WallProbe = wallProbe ?? new WallProbe();
        HeadAimConstraint = headAimConstraint;
        BodyAimConstraint = bodyAimConstraint;
        RifleRoot = rifleRoot;
        PistolRoot = pistolRoot;
        AimAxisPoint = aimAxisPoint;
        RifleAxisOffset = rifleAxisOffset;
        PistolAxisOffset = pistolAxisOffset;
        RightHandWrist = rightHandWrist;
    }

    #region 当前状态只读（状态组合 = 三枚举；Body/Hand/Handing 直接供动画参数对齐）
    public PlayerBodyPosture Body => machine.Body;
    public PlayerHandPosture Hand => machine.Hand;
    public PlayerHanding Handing => machine.Handing;

    /// <summary>当前状态实例（只读；一般场景用 Body/Hand/Handing 即可）。</summary>
    public PlayerStateBase Current => machine.Current;
    #endregion

    /// <summary>
    /// 着地标志（状态类只读；由主体类每帧经 <see cref="GroundProbe"/> 探测写入）。
    /// 不读取 CharacterController.isGrounded——该标志在台阶/边缘存在帧级抽搐，
    /// 直接用于落地/起跳判定会导致姿态不稳定。
    /// </summary>
    public bool IsGrounded { get; set; }

    /// <summary>状态类提议切换（仅提议；裁决与执行在 PlayerStateMachine）。外层直接给三枚举。</summary>
    public void RequestTransition(PlayerHanding handing, PlayerHandPosture hand, PlayerBodyPosture body)
        => machine.RequestTransition(handing, hand, body);

    #region 武器切换动画阻塞判定
    /// <summary>"Switching Weapon" tag 的哈希（对应 Animator 控制器中切换武器动画状态的共用 Tag；未设置时恒不命中）。</summary>
    static readonly int SwitchingWeaponTagHash = Animator.StringToHash("Switching Weapon");

    /// <summary>
    /// 武器切换动画播放中判定：当前（或正在过渡到的下一个）动画状态带 "Switching Weapon" tag 时返回 true。
    /// 用途：作为武器切换信号边的【阻塞条件】——掏出/收回动画播放结束前禁止再次切换武器
    /// （信号边仍被评估与消费，但条件不满足即不切换；动画结束后再次按键即可生效）。
    /// 检查覆盖所有动画层（含过渡中的下一状态），避免切层/交叉淡入期间的漏判。
    /// </summary>
    public bool IsWeaponSwitchAnimPlaying() => TryGetWeaponSwitchAnimProgress(out _);

    /// <summary>
    /// 取武器切换动画的播放进度（0..1）：匹配 "Switching Weapon" tag 的当前/下一动画状态的
    /// normalizedTime——供"武器切换速度插值"与动画进度同步（动画播完即插值结束）。
    /// 未播放（无 tag 状态）返回 false。
    /// </summary>
    public bool TryGetWeaponSwitchAnimProgress(out float progress)
    {
        int layers = Animator.layerCount;
        for (int i = 0; i < layers; ++i)
        {
            var cur = Animator.GetCurrentAnimatorStateInfo(i);
            if (cur.tagHash == SwitchingWeaponTagHash)
            {
                progress = cur.normalizedTime;
                return true;
            }
            if (Animator.IsInTransition(i))
            {
                var next = Animator.GetNextAnimatorStateInfo(i);
                if (next.tagHash == SwitchingWeaponTagHash)
                {
                    progress = next.normalizedTime;
                    return true;
                }
            }
        }
        progress = 0f;
        return false;
    }
    #endregion
}
