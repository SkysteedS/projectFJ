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

public enum PlayerHandPosture
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
    #region Animator 哈希参数（状态类直接引用本类公开静态成员；参数语义见各注释）
    /// <summary>body posture（Int）：0 着地 / 1 滞空 / 2 攀爬 / 3 登顶（对应 PlayerBodyPosture）。</summary>
    public static readonly int AnimBodyPosture = Animator.StringToHash("body posture");
    /// <summary>hand posture（Int）：0 正常 / 1 瞄准（对应 PlayerHandPosture）。</summary>
    public static readonly int AnimHandPosture = Animator.StringToHash("hand posture");
    /// <summary>player handing（Int）：0 空手 / 1 步枪 / 2 剑 / 3 手雷（对应 PlayerHanding）。</summary>
    public static readonly int AnimPlayerHanding = Animator.StringToHash("player handing");
    /// <summary>vertical speed（Float）：水平面内【前后轴】速度（m/s），非瞄准时全速计入此轴。</summary>
    public static readonly int AnimVerticalSpeed = Animator.StringToHash("vertical speed");
    /// <summary>horizontal speed（Float）：水平面内【左右轴】速度（m/s），非瞄准时为 0。</summary>
    public static readonly int AnimHorizontalSpeed = Animator.StringToHash("horizontal speed");
    /// <summary>falling speed（Float）：【垂直方向】速度（原始 m/s，正值向上 / 负值向下，越界由混合树钳制）。</summary>
    public static readonly int AnimFallingSpeed = Animator.StringToHash("falling speed");
    /// <summary>程序化 IK 权重（Float）：由动画状态机全权管理，脚本只读取回写 constraint.weight。</summary>
    public static readonly int AnimRightHandIKWeight = Animator.StringToHash("right hand ik weight");
    public static readonly int AnimLeftHandIKWeight = Animator.StringToHash("left hand ik weight");
    public static readonly int AnimRightLegIKWeight = Animator.StringToHash("right leg ik weight");
    public static readonly int AnimLeftLegIKWeight = Animator.StringToHash("left leg ik weight");

    public static readonly int AnimRifleAimIKWeight = Animator.StringToHash("rifle ik weight");
    public static readonly int AnimHeadAimIKWeight = Animator.StringToHash("head ik weight");
    public static readonly int AnimBodyAimIKWeight = Animator.StringToHash("body ik weight");

    public static readonly int AnimRightArmChainIKWeight = Animator.StringToHash("right arm chain ik weight");
    public static readonly int AnimLeftArmChainIKWeight = Animator.StringToHash("left arm chain ik weight");
    #endregion

    #region 组件引用
    Animator animator;
    CharacterController characterController;
    Transform playerTransform;
    Camera mainCamera;

    public GameObject Rifle;
    public GameObject RightHandWrist;

    [Tooltip("轴点（旋转不变点/枪托抵肩点）：由你在场景放置并拖入；是 Multi-Aim 的约束对象。代码只读，不写它的 transform")]
    public Transform aimAxisPoint;

    [Tooltip("枪根作为轴点子级时的 localPosition（offset）：瞄准期间每帧写入，Inspector 调整后立即生效")]
    public Vector3 aimAxisOffset = Vector3.zero;

    public TwoBoneIKConstraint rightHandConstraint;
    public ChainIKConstraint rightArmChainConstraint;
    public TwoBoneIKConstraint leftHandConstraint;
    public ChainIKConstraint leftArmChainConstraint;
    public TwoBoneIKConstraint rightLegConstraint;
    public TwoBoneIKConstraint leftLegConstraint;

    public MultiAimConstraint rifleAimConstraint;
    public MultiAimConstraint headAimConstraint;
    public MultiAimConstraint bodyAimConstraint;
    #endregion

    #region 输入（帧快照数据层，实现见 PlayerInputState.cs）
    [Header("输入")]
    [SerializeField] PlayerInputState input = new PlayerInputState();
    #endregion

    #region 运动数值（物理常量 + 纯计算，实现见 PlayerMotionValues.cs；状态类经 PlayerContext.Values 读取）
    [Header("运动数值")]
    [SerializeField] PlayerMotionValues motionValues = new PlayerMotionValues();
    #endregion

    #region 物理探测（着地判定：替代 CharacterController.isGrounded——边缘帧级抽搐，见 GroundProbe.cs）
    [Header("着地探测")]
    [SerializeField] GroundProbe groundProbe = new GroundProbe();

    [Header("墙面探测（攀爬检测，参数化射线阵列，见 WallProbe.cs）")]
    [SerializeField] WallProbe wallProbe = new WallProbe();

    /// <summary>编辑模式选中角色时显示着地探测 Gizmos（下沿球/射线/命中点）。</summary>
    void OnDrawGizmosSelected()
    {
        var cc = characterController != null ? characterController : GetComponent<CharacterController>();
        groundProbe.DrawGizmos(transform, cc);
        wallProbe.DrawGizmos(transform, transform.forward);
    }

    /// <summary>
    /// 瞄准调试可视化（Scene 视图常驻，运行时生效）：
    /// - 瞄准落点：相机中心射线（命中=青色线+绿色落点球；未命中=黄色线+黄色远点球）；
    /// - 枪约束驱动对象：白色球（对象位置）+ 品红射线（其 aimAxis 当前世界方向，正确时应指向落点）。
    /// 对照观察：品红线是否指向落点球——不指向 = 轴向/limits/驱动对象(handle 是否为整枪根)问题；
    /// 落点球是否在准星上——不在 = 射线层掩码/起点问题。
    /// </summary>
    void OnDrawGizmos()
    {
        if (context == null || !context.LastAimValid) return;

        Gizmos.color = context.LastAimHit
            ? new Color(0.2f, 1f, 0.4f)   // 命中（绿）
            : new Color(1f, 0.8f, 0.2f);  // 远点（黄）
        if (mainCamera != null)
            Gizmos.DrawLine(mainCamera.transform.position, context.LastAimPoint);
        Gizmos.DrawWireSphere(context.LastAimPoint, 0.06f);

        if (rifleAimConstraint != null)
        {
            Transform gun = rifleAimConstraint.data.constrainedObject;
            if (gun != null)
            {
                Vector3 axis = AimAxisToVector(rifleAimConstraint.data.aimAxis);
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(gun.position, 0.04f);
                Gizmos.color = Color.magenta;
                Gizmos.DrawRay(gun.position, gun.rotation * axis * 20f);
            }
        }
    }

    static Vector3 AimAxisToVector(MultiAimConstraintData.Axis axis)
    {
        switch (axis)
        {
            case MultiAimConstraintData.Axis.X: return Vector3.right;
            case MultiAimConstraintData.Axis.X_NEG: return Vector3.left;
            case MultiAimConstraintData.Axis.Y: return Vector3.up;
            case MultiAimConstraintData.Axis.Y_NEG: return Vector3.down;
            case MultiAimConstraintData.Axis.Z: return Vector3.forward;
            default: return Vector3.back;   // Z_NEG
        }
    }
    #endregion

    #region 状态机（扁平单机：一个扁平状态枚举 Current + 一个状态类字典 + 一张转换边表）
    PlayerStateMachine machine;   // 唯一状态机（裁决 + 切换执行）
    PlayerContext context;        // 状态类的只读通道
    #endregion

    #region IK
    void SetIKweight()
    {
        rightHandConstraint.weight = animator.GetFloat(AnimRightHandIKWeight);
        leftHandConstraint.weight = animator.GetFloat(AnimLeftHandIKWeight);
        rightArmChainConstraint.weight = animator.GetFloat(AnimRightArmChainIKWeight);
        leftArmChainConstraint.weight = animator.GetFloat(AnimLeftArmChainIKWeight);
        rightLegConstraint.weight = animator.GetFloat(AnimRightLegIKWeight);
        leftLegConstraint.weight = animator.GetFloat(AnimLeftLegIKWeight);
        if (headAimConstraint != null)
            headAimConstraint.weight = animator.GetFloat(AnimHeadAimIKWeight);
        if (bodyAimConstraint != null)
            bodyAimConstraint.weight = animator.GetFloat(AnimBodyAimIKWeight);
        // rifle Multi-Aim 已由"轴点旋转程序化覆盖"（LateUpdate 确定性写入）退役：
        // 强制 0——即使场景中组件仍存在/将来误重挂回轴点，也不参与求值，
        // 避免与确定性旋转（rigAxisRotation）冲突造成手/枪偶发错位穿模
        if (rifleAimConstraint != null)
            rifleAimConstraint.weight = 0f;
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
        SetIKweight();
        machine?.Tick(context); // 状态机：信号裁决 → 持久边裁决 → 当前状态 Tick（注册为空时经 ?. 跳过）
        SyncAnimatorPostureParams(); // 姿态组合（body/hand/handing）每帧同步：状态切换后动画分支随之切换
    }

    /// <summary>
    /// 轴点旋转的应用（LateUpdate 执行：保证在 Animator 求值之后，见下方说明）：
    /// 轴点仍是 Chest 的子级（位置跟随胸口，防穿模），但其【旋转】不再交给被胸部动画
    /// 逐帧扰动的 Multi-Aim 增量补偿（±限制的补偿额被动画消耗→移动/甩枪跟不上），
    /// 而是由瞄准状态在 Tick 中用 RotateTowards 以 aimAxisMaxRotSpeed 限速推进
    /// （确定性值 ctx.AxisPointRotation，见 Rifle_Aiming_Ground_State.TickAxisRotation），
    /// 此处只做应用：写入轴点 worldRotation。
    /// 关键：双手 ChainIK target 与轴点写入使用【同一确定性旋转】——若在本处各自推进/
    /// 推算，两者会用到不同时点的旋转，导致"枪先转、手后算"的同帧错位穿模（已修复）。
    /// 轴向语义：与原 rifle Multi-Aim（aimAxis=Z_NEG）一致——aimAxisNegZ=true 时让轴点 -Z 指向瞄准点。
    /// </summary>
    void LateUpdate()
    {
        if (context == null || !context.AimRigActive) return;
        Transform pivot = context.AimAxisPoint;
        if (pivot == null) return;

        pivot.rotation = context.AxisPointRotation;
    }

    /// <summary>
    /// 姿态三枚举 → 动画参数（body posture / hand posture / player handing）。
    /// 状态类只负责运动数据（速度分量），姿态切换后的动画分支由本处统一驱动；
    /// 覆盖"瞄准中跳跃 → 取消瞄准"等组合变化（切到 Normal 手部即回正常动画分支）。
    /// </summary>
    void SyncAnimatorPostureParams()
    {
        if (machine == null) return;
        animator.SetInteger(AnimBodyPosture, (int)machine.Body);
        animator.SetInteger(AnimHandPosture, (int)machine.Hand);
        animator.SetInteger(AnimPlayerHanding, (int)machine.Handing);
    }
    #endregion

    #region 武器动画事件（Animation Event 分发：Function 填 OnWeaponEvent，String 填分发键）
    // 入口写在主体类（Animator 所在对象）：Rifle 引用已在手，Rifle.GetComponent<WeaponSwitch>()
    // 直接取到武器切换脚本，无需再挂独立组件/拖引用。
    /// <summary>武器动画事件入口（仅 String 参数）。</summary>
    public void OnWeaponEvent(string message) => DispatchWeaponEvent(message);

    /// <summary>武器动画事件入口（Float + String 参数）。</summary>
    public void OnWeaponEvent(float value, string message) => DispatchWeaponEvent(message);

    /// <summary>武器动画事件入口（Float + Int + String 参数，与事件面板当前配置匹配）。</summary>
    public void OnWeaponEvent(float value, int index, string message) => DispatchWeaponEvent(message);

    /// <summary>武器动画事件入口（Float + Int + String + Object 参数）。</summary>
    public void OnWeaponEvent(float value, int index, string message, Object sender) => DispatchWeaponEvent(message);

    /// <summary>按分发键调用 rifle 上 WeaponSwitch 的切换操作。</summary>
    void DispatchWeaponEvent(string message)
    {
        // GetComponentInChildren：WeaponSwitch 挂在 Rifle 本体或 rifle 内部子对象（如 handle）均可命中
        WeaponSwitch weaponSwitch = Rifle != null ? Rifle.GetComponentInChildren<WeaponSwitch>(true) : null;
        if (weaponSwitch == null)
        {
            Debug.LogWarning($"[WeaponEvent] 武器事件 '{message}' 未找到 WeaponSwitch（Rifle 未指派或 Rifle（含子物体）上无该组件）", this);
            return;
        }
        switch (message)
        {
            case "GrabRifle":
                weaponSwitch.SwitchToInUse();      // 拔枪：切到使用中挂点，isUsing = true
                break;
            case "PutRifle":
                weaponSwitch.SwitchToStored();     // 收枪：切到未使用/收纳挂点，isUsing = false
                break;
            default:
                Debug.LogWarning($"[WeaponEvent] 未处理的分发键：{message}", this);
                break;
        }
    }
    #endregion

    #region 攀爬进入检测（Jump 信号边条件：攀爬优先、跳跃兜底）
    /// <summary>
    /// 跳跃键统一"移动动作"的攀爬优先判定：
    /// ① 墙面探测命中（参数化射线阵列，见 WallProbe）；
    /// ② 存在朝向墙面的速度——水平速度大小 ≥ 阈值，且方向与墙面法线反方向夹角 ≤ climbApproachAngle。
    /// 站立按 Jump（速度 ≈ 0）不满足条件 → 走跳跃兜底。
    /// </summary>
    bool TryEnterClimb(PlayerContext ctx)
    {
        if (!ctx.WallProbe.Evaluate(ctx.Transform, ctx.Transform.forward, out _, out _)) return false;
        if (!ctx.WallProbe.LastSucceeded) return false;

        if (ctx.Motion.HorizontalSpeed < ctx.Values.climbApproachMinSpeed) return false;

        Vector3 horizontal = ctx.Motion.Velocity;
        horizontal.y = 0f;
        float angle = Vector3.Angle(horizontal.normalized, -ctx.WallProbe.LastNormal);
        return angle <= ctx.Values.climbApproachAngle;
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
        machine = new PlayerStateMachine();
        context = new PlayerContext(animator, characterController, playerTransform, mainCamera, input, machine,
                                    rightHandConstraint, leftHandConstraint, rightLegConstraint, leftLegConstraint,
                                    motionValues, wallProbe,
                                    rifleAimConstraint, headAimConstraint, bodyAimConstraint,
                                    rightArmChainConstraint, leftArmChainConstraint,
                                    Rifle != null ? Rifle.transform : null,
                                    aimAxisPoint,
                                    aimAxisOffset,
                                    RightHandWrist != null ? RightHandWrist.transform : null);

        // 状态注册（组合 → 状态类实例；工厂映射见 PlayerStateFactory。仅注册已实现的状态）
        machine.RegisterState(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerStateFactory.Create(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground));
        machine.RegisterState(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            PlayerStateFactory.Create(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping));
        machine.RegisterState(PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerStateFactory.Create(PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground));
        machine.RegisterState(PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            PlayerStateFactory.Create(PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping));
        machine.RegisterState(PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerStateFactory.Create(PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground));
        machine.RegisterState(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,
            PlayerStateFactory.Create(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing));

        // —— 空手 ——
        // ⓪ 信号边（攀爬优先，必须注册在 ① 跳跃边【之前】——注册顺序 = 裁决优先级，命中即切换）：
        //    跳跃键 = 统一"移动动作"：存在可攀爬墙面 + 水平速度朝墙 → 进攀爬；未命中则走 ① 跳跃兜底
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,
            condition: TryEnterClimb,
            triggerSignal: PlayerInputState.Signal.Jump,
            label: "跳跃·攀爬检测优先"));

        // ① 信号边：地面按下 Jump → 起跳（需物理着地）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.IsGrounded,
            triggerSignal: PlayerInputState.Signal.Jump,
            label: "跳跃（空手·地面起跳）"));

        // ② 请求边：滞空 → 地面（落地；垂直速度 ≤ 判定阈值防起跳瞬间着地标志残留误判，对应 test.cs 落地断言）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => ctx.IsGrounded && ctx.Motion.VerticalVelocity <= ctx.Values.landingVerticalThreshold,
            label: "落地（空手）"));

        // ③ 请求边：地面 → 滞空（走出边沿：离地且垂直速度越过死区阈值；条件与 Ground 状态提议一致）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.Motion.VerticalVelocity < ctx.Values.airborneFallThreshold
                           || ctx.Motion.VerticalVelocity > ctx.Values.airborneRiseThreshold,
            label: "走出边沿进入滞空（空手）"));

        // ③b 信号边：攀爬中按退出输入（QuitClimb）→ 退回默认姿态（地面）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            triggerSignal: PlayerInputState.Signal.QuitClimb,
            label: "退出攀爬"));

        // ③c 请求边：攀爬 → 地面（物理驱逐：墙面连续失效，由攀爬状态在 Tick 中提议）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.WallProbe.LastSucceeded,
            label: "墙面失效退出攀爬"));

        // —— 步枪 ——
        // ④ 信号边：空手按 1（SlotRifle）→ 切换步枪（地面正常手部）。
        //    条件阻塞：武器切换动画（Switching Weapon tag）播放期间禁止切换——动画播完才允许。
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.IsWeaponSwitchAnimPlaying(),
            triggerSignal: PlayerInputState.Signal.SlotRifle,
            label: "切换步枪"));

        // ④b 信号边：步枪地面再按 1（SlotRifle）→ 收回步枪（切回空手）。
        //    同样阻塞于武器切换动画（收回动画自身播放期间再按键无效）。
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.IsWeaponSwitchAnimPlaying(),
            triggerSignal: PlayerInputState.Signal.SlotRifle,
            label: "收回步枪（回空手）"));

        // ⑤ 信号边：步枪地面按 Jump → 起跳（与空手规则一致）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.IsGrounded,
            triggerSignal: PlayerInputState.Signal.Jump,
            label: "跳跃（步枪·地面起跳）"));

        // ⑥ 信号边：步枪瞄准中按 Jump → 取消瞄准并起跳（切到正常手部滞空）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.IsGrounded,
            triggerSignal: PlayerInputState.Signal.Jump,
            label: "跳跃（瞄准中·取消瞄准）"));

        // ⑦ 瞄准进出（两种模式按 Values.aimToggleMode 互斥；支持运行时切模式）——
        //    按住模式（持久边）：按住 Aim 进入、松开退出
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.Values.aimToggleMode && ctx.Input.Aim,
            evaluateEveryFrame: true,
            label: "按住瞄准"));
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.Values.aimToggleMode && !ctx.Input.Aim,
            evaluateEveryFrame: true,
            label: "松开瞄准"));
        //    切换模式（信号边）：按一次 Aim 进瞄准、再按一次退出
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            condition: ctx => ctx.Values.aimToggleMode,
            triggerSignal: PlayerInputState.Signal.AimPressed,
            label: "切换瞄准（进）"));
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => ctx.Values.aimToggleMode,
            triggerSignal: PlayerInputState.Signal.AimPressed,
            label: "切换瞄准（出）"));

        // ⑧ 请求边：步枪滞空 → 落地（正常手部）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => ctx.IsGrounded && ctx.Motion.VerticalVelocity <= ctx.Values.landingVerticalThreshold,
            label: "落地（步枪）"));

        // ⑨ 请求边：步枪地面 → 滞空（走出边沿）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.Motion.VerticalVelocity < ctx.Values.airborneFallThreshold
                           || ctx.Motion.VerticalVelocity > ctx.Values.airborneRiseThreshold,
            label: "走出边沿进入滞空（步枪）"));

        // ⑩ 请求边：步枪瞄准中走出边沿 → 切滞空正常手部（瞄准无滞空组合，切换即取消瞄准）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.Motion.VerticalVelocity < ctx.Values.airborneFallThreshold
                           || ctx.Motion.VerticalVelocity > ctx.Values.airborneRiseThreshold,
            label: "瞄准中走出边沿进入滞空"));

        // 进入初始状态（地面）
        machine.Enter(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground, context);
    }
    #endregion

    #region 位移接管（委托给当前状态）
    /// <summary>
    /// Animator 根运动入口：委托给当前状态的 OnAnimatorMove（地面沿用根运动 + 手写垂直、
    /// 滞空全量脚本接管）。不实现本方法时 Unity 会把根运动直接写 transform，绕过
    /// CharacterController 造成穿墙/跳步，因此必须接管。
    /// </summary>
    void OnAnimatorMove()
    {
        machine?.Current?.OnAnimatorMove(context);
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

    public void GetQuitClimbInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnQuitClimb(input, ctx);
    }

    #endregion
}
