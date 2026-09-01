using UnityEngine;

/// <summary>
/// 状态上下文：状态类与外部世界的【唯一通道】（只读视图 + 移交/提议）。
/// 组件与当前状态由 <see cref="PlayerStateMachine"/> 持有；本类只读并转发提议。
///
/// 说明：早期"能力许可表"（PlayerAction/PlayerCapabilities）已随扁平状态机退役——
/// 行为合法性由"状态枚举中的合法组合 + 工厂映射 + 转换边存在性"编码（如持枪攀爬
/// 没有对应状态类与边，天然不可达）；状态内行为差异（如步枪不响应奔跑档）在
/// 各具体状态类内部直接实现。
///
/// 当前为骨架版本：运动数值层（MotionState）尚未接入，
/// 落地判定暂时直接用 CharacterController.isGrounded（Phase 2 接入后替换）。
/// </summary>
public class PlayerContext
{
    public readonly Animator Animator;
    public readonly CharacterController CharacterController;
    public readonly Transform Transform;
    public readonly Camera MainCamera;
    public readonly PlayerInputState Input;

    /// <summary>共享运动描述（跨状态交接槽）：当前状态类写入、新状态 Enter 读取（见 PlayerMotion）。</summary>
    public readonly PlayerMotion Motion = new PlayerMotion();

    readonly PlayerStateMachine machine;

    public PlayerContext(Animator animator, CharacterController characterController, Transform transform,
                         Camera mainCamera, PlayerInputState input, PlayerStateMachine machine)
    {
        Animator = animator;
        CharacterController = characterController;
        Transform = transform;
        MainCamera = mainCamera;
        Input = input;
        this.machine = machine;
    }

    #region 当前状态只读（状态组合 = 三枚举；Body/Hand/Handing 直接供动画参数对齐）
    public PlayerBodyPosture Body => machine.Body;
    public PlyaerHandPosture Hand => machine.Hand;
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
    public void RequestTransition(PlayerHanding handing, PlyaerHandPosture hand, PlayerBodyPosture body)
        => machine.RequestTransition(handing, hand, body);
}
