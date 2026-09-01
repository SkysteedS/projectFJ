using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.InputSystem;

public enum PlayerHanding
{
    Unarmed = 0,
    Rifle = 1,
    Sword = 2,
    Grenade = 3
}

public enum PlyaerHandPosture
{
    Normal = 0,
    Aiming = 1,
    Pulling = 2,
    Pushing = 3
}

public enum PlayerBodyPosture
{
    Ground = 0,
    Jumping = 1,
    Climbing = 2,
    ClimbTopOut = 3
}

public class PlayerControllerScript : MonoBehaviour
{
    #region Animator哈希参数
    // 垂直速度
    static readonly int AnimVerticalSpeed = Animator.StringToHash("vertical speed");
    // 水平速度
    static readonly int AnimHorizontalSpeed = Animator.StringToHash("horizontal speed");
    // 竖直速度
    static readonly int AnimFallingSpeed = Animator.StringToHash("falling speed");
    // 姿态参数
    static readonly int AnimBodyPosture = Animator.StringToHash("body posture");
    // 瞄准参数
    static readonly int AnimIsAiming = Animator.StringToHash("isAiming");
    // 手持参数
    static readonly int AnimPlyaerHanding = Animator.StringToHash("player handing");

    static readonly int AnimRightHandIKWeight = Animator.StringToHash("right hand ik weight");

    static readonly int AnimLeftHandIKWeight = Animator.StringToHash("left hand ik weight");
    static readonly int AnimRightLegIKWeight = Animator.StringToHash("right leg ik weight");
    static readonly int AnimLeftLegIKWeight = Animator.StringToHash("left leg ik weight");

    #endregion

    #region 组件引用
    Animator animator;
    CharacterController characterController;
    Transform playerTransform;
    Camera mainCamera;

    public TwoBoneIKConstraint rightHandConstraint;
    public TwoBoneIKConstraint leftHandConstraint;
    public TwoBoneIKConstraint rightLegConstraint;
    public TwoBoneIKConstraint leftLegConstraint;
    #endregion

    #region 输入（帧快照数据层，实现见 PlayerInputState.cs）
    [Header("输入")]
    [SerializeField] PlayerInputState input = new PlayerInputState();
    #endregion

    #region 物理探测（着地判定：替代 CharacterController.isGrounded——边缘帧级抽搐，见 GroundProbe.cs）
    [Header("着地探测")]
    [SerializeField] GroundProbe groundProbe = new GroundProbe();

    /// <summary>编辑模式选中角色时显示着地探测 Gizmos（下沿球/射线/命中点）。</summary>
    void OnDrawGizmosSelected()
    {
        var cc = characterController != null ? characterController : GetComponent<CharacterController>();
        groundProbe.DrawGizmos(transform, cc);
    }
    #endregion

    #region 状态机（扁平单机：一个扁平状态枚举 Current + 一个状态类字典 + 一张转换边表）
    PlayerStateMachine machine;   // 唯一状态机（裁决 + 切换执行）
    PlayerContext context;        // 状态类的只读通道
    #endregion

    #region IK
    void SetIKTarget()
    {

    }

    void SetIKweight()
    {
        rightHandConstraint.weight = animator.GetFloat(AnimRightHandIKWeight);
        leftHandConstraint.weight = animator.GetFloat(AnimLeftHandIKWeight);
        rightLegConstraint.weight = animator.GetFloat(AnimRightLegIKWeight);
        leftLegConstraint.weight = animator.GetFloat(AnimLeftLegIKWeight);
    }
    #endregion

    #region 生命周期
    // Start is called before the first frame update
    void Start()
    {
        animator = GetComponent<Animator>();
        characterController = GetComponent<CharacterController>();
        playerTransform = transform;
        mainCamera = Camera.main;

        InitStateMachine();
    }

    // Update is called once per frame
    void Update()
    {
        input.Capture();        // 帧快照：必须在一切输入消费逻辑之前（输入层约定，见设计文档 §4）
        if (context != null)
        {
            context.IsGrounded = groundProbe.Evaluate(playerTransform, characterController);  // 着地探测（去抖）
        }
        SetIKTarget();
        SetIKweight();
        machine?.Tick(context); // 状态机：信号裁决 → 持久边裁决 → 当前状态 Tick（注册为空时经 ?. 跳过）
    }
    #endregion

    #region 状态机初始化（状态与转换边注册——数据驱动的变更点）
    /// <summary>
    /// 注册状态与转换边。新增状态 = 加枚举 + 加状态类 + 加相关边，旧状态零改动。
    /// 转换边类型（详见 TransitionEdge 注释）：
    /// - 信号边：绑定输入信号，注册顺序 = 裁决优先级（Jump = 攀爬优先、跳跃兜底）；
    /// - 持久边：每帧评估的真值条件（瞄准按住/松开）；
    /// - 请求边：状态类 Tick 内提议（物理条件，如落地）。
    /// 合法性由"状态/边是否存在"编码：非法组合（如持枪攀爬、瞄准中切枪）没有对应边，
    /// 信号被评估后丢弃 → 天然不可达，无需额外规则表。
    /// </summary>
    void InitStateMachine()
    {
        // 暂空：状态机注册暂不接入（先把状态机本身处理好再填）。
        // 完整注册清单（7 个状态类 + 信号边/持久边/请求边）见设计文档 §6.3；
        // 期间 machine 为 null，Update 中 machine.Tick 经 ?. 判空跳过。
    }
    #endregion

    #region Animation Event转发
    /// <summary>
    /// 动画事件分发器，为了降低AnimationEvent的反射开销，仅有这一个方法接收信息
    /// string参数用于分发判断，其余参数用于继续传参
    /// </summary>
    public void AnimationEvent(float param_float, int param_int, string param_string, object param_object)
    {

    }
    #endregion

    #region 输入回调（纯转发到 InputActionBridge，无业务逻辑）
    public void GetMoveInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnMove(input, ctx);
    }
    public void GetRunInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnRun(input, ctx);
    }
    public void GetJumpInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnJump(input, ctx);
    }
    public void GetRifleInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnRifle(input, ctx);
    }
    public void GetSwordInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnSword(input, ctx);
    }
    public void GetGrenadeInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnGrenade(input, ctx);
    }
    public void GetFireInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnFire(input, ctx);
    }
    public void GetAimingInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnAim(input, ctx);
    }
    public void GetReloadInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnReload(input, ctx);
    }

    public void GetInteractInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnInteract(input, ctx);
    }

    #endregion
}
