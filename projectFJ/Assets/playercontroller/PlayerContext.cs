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
/// 运动数值已接入（PlayerMotionValues，经 Values 访问；计算函数与可调参数字段见该类）；
/// 着地判定由主体类每帧经 GroundProbe 探测写入 IsGrounded（不使用 CharacterController.isGrounded，
/// 该标志在台阶/边缘存在帧级抽搐，见 GroundProbe）。
/// </summary>
public class PlayerContext
{
    public readonly Animator Animator;                                   // 写动画参数 / 查动画状态
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

    #region 瞄准引导约束（Multi-Aim：枪/头/胸指向目标）
    // 用途：瞄准状态每帧把各约束 SourceObjects[0] 的目标对象【位置】设为"相机中心射线命中点/远点"，
    // 让枪口/视线/上身指向瞄准点。只移动目标位置，约束引用（装配即定）不被改写。
    // 权重（constraint.weight）由动画状态机参数经 SetIKweight 回写，本上下文不写。
    public readonly MultiAimConstraint RifleAimConstraint;
    public readonly MultiAimConstraint HeadAimConstraint;
    public readonly MultiAimConstraint BodyAimConstraint;

    /// <summary>枪根（PlayerControllerScript.Rifle）：瞄准时作为轴点的子级。</summary>
    public readonly Transform RifleRoot;
    /// <summary>轴点（PlayerControllerScript.aimAxisPoint）：旋转不变点；代码只读，不写它的 transform。</summary>
    public readonly Transform AimAxisPoint;
    /// <summary>枪根作为轴点子级时的 localPosition（PlayerControllerScript.aimAxisOffset）。</summary>
    public readonly Vector3 RifleAxisOffset;
    /// <summary>退出瞄准时枪要恢复的父级（PlayerControllerScript.RightHandWrist；为空时用进入前父级）。</summary>
    public readonly Transform RightHandWrist;

    /// <summary>最近一帧瞄准落点（世界坐标；瞄准状态写入，Scene 视图 Gizmos 调试用；LastAimValid 表示是否有值）。</summary>
    public Vector3 LastAimPoint;
    /// <summary>最近一帧瞄准落点是否有效（瞄准状态写入）。</summary>
    public bool LastAimValid;
    /// <summary>最近一帧瞄准射线是否命中物体（false = 落点为远点）。</summary>
    public bool LastAimHit;
    #endregion

    /// <summary>共享运动描述（跨状态交接槽）：当前状态类写入速度、新状态 Enter 读取（见 PlayerMotion）。</summary>
    public readonly PlayerMotion Motion = new PlayerMotion();

    /// <summary>运动数值服务（物理常量 + 纯计算函数，见 PlayerMotionValues）；状态类读常量、调计算、写 Motion。</summary>
    public readonly PlayerMotionValues Values;

    /// <summary>墙面探测（参数化射线阵列，见 WallProbe）；攀爬进入检测与攀爬中逐帧重测共用。</summary>
    public readonly WallProbe WallProbe;

    readonly PlayerStateMachine machine;

    public PlayerContext(Animator animator, CharacterController characterController, Transform transform,
                         Camera mainCamera, PlayerInputState input, PlayerStateMachine machine,
                         TwoBoneIKConstraint rightHandConstraint, TwoBoneIKConstraint leftHandConstraint,
                         TwoBoneIKConstraint rightLegConstraint, TwoBoneIKConstraint leftLegConstraint,
                         PlayerMotionValues values = null, WallProbe wallProbe = null,
                         MultiAimConstraint rifleAimConstraint = null, MultiAimConstraint headAimConstraint = null,
                         MultiAimConstraint bodyAimConstraint = null,
                         ChainIKConstraint rightArmChainConstraint = null, ChainIKConstraint leftArmChainConstraint = null,
                         Transform rifleRoot = null, Transform aimAxisPoint = null,
                         Vector3 rifleAxisOffset = default, Transform rightHandWrist = null)
    {
        Animator = animator;
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
        Values = values ?? new PlayerMotionValues();
        WallProbe = wallProbe ?? new WallProbe();
        RifleAimConstraint = rifleAimConstraint;
        HeadAimConstraint = headAimConstraint;
        BodyAimConstraint = bodyAimConstraint;
        RifleRoot = rifleRoot;
        AimAxisPoint = aimAxisPoint;
        RifleAxisOffset = rifleAxisOffset;
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
