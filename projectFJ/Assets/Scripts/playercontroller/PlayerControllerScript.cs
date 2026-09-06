using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.InputSystem;

public enum PlayerHanding
{
    Unarmed = 0,
    Rifle = 1,
    Pistol = 2,  // 手枪
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
    /// <summary>player handing（Int）：0 空手 / 1 步枪 / 2 手枪 / 3 手雷（对应 PlayerHanding）。</summary>
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
    /// <summary>手枪根（PlayerControllerScript.Pistol）：手枪正常状态左手 IK 目标参照；由你在场景拖入，未接入前可为空。</summary>
    public GameObject Pistol;
    /// <summary>手雷根（PlayerControllerScript.Grenade）：拔/收手雷动画事件切挂点用（GrabGrenade/PutGrenade）；由你在场景拖入，未接入前可为空。</summary>
    public GameObject Grenade;
    public GameObject RightHandWrist;

    [Tooltip("轴点（旋转不变点/枪托抵肩点）：由你在场景放置并拖入；是 Multi-Aim 的约束对象。代码只读，不写它的 transform")]
    public Transform aimAxisPoint;

    [Tooltip("枪根作为轴点子级时的 localPosition（offset）：瞄准期间每帧写入，Inspector 调整后立即生效")]
    public Vector3 aimAxisOffset = Vector3.zero;

    [Tooltip("手枪根作为轴点子级时的 localPosition（offset）：手枪瞄准期间每帧写入；手枪与步枪到轴点的距离不同，需单独标定，Inspector 调整后立即生效")]
    public Vector3 pistolAimAxisOffset = Vector3.zero;

    [Tooltip("手雷轴点（肘部附近）：手雷瞄准/抛掷俯仰的旋转原点；由你在场景放置并拖入。代码只读，不写它的 transform")]
    public Transform grenadeAimAxisPoint;

    [Tooltip("手雷根作为轴点子级时的 localPosition（offset）：手雷瞄准期间每帧写入；肘部到手雷握持点的标定值，Inspector 调整后立即生效")]
    public Vector3 grenadeAimAxisOffset = Vector3.zero;

    [Tooltip("手雷根作为轴点子级时的 localRotation（Euler）：手雷横握需要相对轴点的旋转标定（步枪/手枪瞄准姿态竖直、localRotation 恒等即可；手雷投掷前为横向，需把模型朝向转到目标方向）")]
    public Vector3 grenadeAimAxisLocalEuler = Vector3.zero;

    public TwoBoneIKConstraint rightHandConstraint;
    public ChainIKConstraint rightArmChainConstraint;
    public TwoBoneIKConstraint leftHandConstraint;
    public ChainIKConstraint leftArmChainConstraint;
    public TwoBoneIKConstraint rightLegConstraint;
    public TwoBoneIKConstraint leftLegConstraint;

    public MultiAimConstraint headAimConstraint;
    public MultiAimConstraint bodyAimConstraint;
    #endregion

    #region 输入（帧快照数据层，实现见 PlayerInputState.cs）
    [Header("输入")]
    [SerializeField] PlayerInputState input = new PlayerInputState();
    #endregion

    #region 运动数值（物理常量 + 纯计算，见 PlayerMotionValuesSO.cs；资产单例承载，组件只持引用——Play Mode 中改资产即实时全局生效）
    [Header("运动数值")]
    [SerializeField] PlayerMotionValuesSO motionValues;
    #endregion

    #region 物理探测（着地判定：替代 CharacterController.isGrounded——边缘帧级抽搐，见 GroundProbe.cs）
    [Header("着地探测")]
    [SerializeField] GroundProbe groundProbe = new GroundProbe();

    [Header("墙面探测（攀爬检测，参数化射线阵列，见 WallProbe.cs）")]
    [SerializeField] WallProbe wallProbe = new WallProbe();
    #endregion

    #region 调试可视化（Gizmos：开关配置 + 全部绘制；仅编辑器/开发构建调用，与业务逻辑隔离）
    [Header("调试 Gizmos（全部默认关闭；勾选后按类别在 Scene 视图显示）")]
    [Tooltip("瞄准调试（相机瞄准线/落点）")]
    [SerializeField] bool showAimGizmos;
    [Tooltip("攀爬手/脚 IK 调试（目标、手骨实际位置、连线）")]
    [SerializeField] bool showClimbIkGizmos;
    [Tooltip("登顶到顶探测调试（手掌锚点、向上竖线、探测射线颜色）")]
    [SerializeField] bool showClimbTopOutGizmos;
    [Tooltip("墙面探测阵列调试（选中角色时显示；编辑模式下开启即实时预览命中）")]
    [SerializeField] bool showWallProbeGizmos;
    [Tooltip("着地探测调试（选中角色时显示）")]
    [SerializeField] bool showGroundProbeGizmos;
    [Tooltip("临时调试：打印 body posture 参数过渡时间线（脚本姿态 vs Animator 实际参数 + 当前/下一状态 + 是否过渡中），排查脚本状态机与动画状态机不同步；确认后删除")]
    [SerializeField] bool debugBodyPostureTimeline;

    /// <summary>编辑模式/运行时选中角色时显示着地/墙面探测 Gizmos（受对应开关门控）。</summary>
    void OnDrawGizmosSelected()
    {
        var cc = characterController != null ? characterController : GetComponent<CharacterController>();
        if (showGroundProbeGizmos) groundProbe.DrawGizmos(transform, cc);
        if (showWallProbeGizmos) wallProbe.DrawGizmos(transform, transform.forward);
    }

    /// <summary>
    /// 调试可视化（Scene 视图常驻，运行时生效）：
    /// - 瞄准：落点（命中=青线绿球 / 远点=黄线黄球）；
    /// - 攀爬 IK（showClimbIkGizmos 且正攀爬时）：青色 = 双手 IK 目标，
    ///   黄色 = 手骨（腕）实际位置，红色 = 目标到手骨连线（线越长 = IK 越没拉到位）。
    /// - 登顶探测（showClimbTopOutGizmos）：手掌锚点上方射向墙面的射线——黄色 = 命中墙面（仍在爬），
    ///   绿色 = 未命中（已越过墙顶、到顶候选）。
    /// 以上均需对应开关开启（默认全关）。
    /// </summary>
    void OnDrawGizmos()
    {
        if (showAimGizmos) DrawAimGizmos();
        if (showClimbIkGizmos) DrawClimbIkGizmos();
        if (showClimbTopOutGizmos) DrawClimbTopOutGizmos();
    }

    void DrawAimGizmos()
    {
        if (context == null || !context.LastAimValid) return;

        Gizmos.color = context.LastAimHit
            ? new Color(0.2f, 1f, 0.4f)   // 命中（绿）
            : new Color(1f, 0.8f, 0.2f);  // 远点（黄）
        if (mainCamera != null)
            Gizmos.DrawLine(mainCamera.transform.position, context.LastAimPoint);
        Gizmos.DrawWireSphere(context.LastAimPoint, 0.06f);

    }

    /// <summary>攀爬手部 IK 调试绘制（攀爬状态经 PlayerContext 每帧写入；非攀爬时 ClimbIkDebugActive = false 不绘制）。</summary>
    void DrawClimbIkGizmos()
    {
        if (context == null || !context.ClimbIkDebugActive) return;

        // IK 目标位置（青色线框）
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(context.ClimbLeftTarget, 0.09f);
        Gizmos.DrawWireSphere(context.ClimbRightTarget, 0.09f);

        // IK 目标轴向（调旋转偏移时参考）：红 = 目标 +X、绿 = +Y、蓝 = +Z
        if (leftHandConstraint != null && leftHandConstraint.data.target != null)
            DrawTargetAxes(leftHandConstraint.data.target);
        if (rightHandConstraint != null && rightHandConstraint.data.target != null)
            DrawTargetAxes(rightHandConstraint.data.target);
        if (leftLegConstraint != null && leftLegConstraint.data.target != null)
            DrawTargetAxes(leftLegConstraint.data.target);
        if (rightLegConstraint != null && rightLegConstraint.data.target != null)
            DrawTargetAxes(rightLegConstraint.data.target);

        // 手骨实际位置（黄色线框）
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(context.ClimbLeftHandPos, 0.06f);
        Gizmos.DrawWireSphere(context.ClimbRightHandPos, 0.06f);

        // 目标与手骨连线：线越长说明 IK 越没拉到位
        Gizmos.color = Color.red;
        Gizmos.DrawLine(context.ClimbLeftTarget, context.ClimbLeftHandPos);
        Gizmos.DrawLine(context.ClimbRightTarget, context.ClimbRightHandPos);
    }

    /// <summary>
    /// 登顶到顶探测调试绘制（攀爬状态经 PlayerContext 每帧写入；非攀爬/未开启调试时 TopOutProbeActive = false 不绘制）：
    /// 白圈 = 被选中的手掌锚点（TwoBoneIK target），白色竖线 = 向上偏移；射线——黄色 = 命中墙面侧面（仍在爬），
    /// 绿色 = 未命中（已越过墙顶、到顶候选）；终点球画在墙面表面（命中时）或射线尽头（未命中时）。
    /// 登顶期间另画抓取点调试：品红球 = 墙顶扫描 + topOutMatchPositionOffset 后的 MatchTarget 目标（含扫描起点→抓取点连线），
    /// 橙色球 = 扫描失败回退的 IK target 快照（说明抓取点不是缘上，需调扫描参数）。
    /// </summary>
    void DrawClimbTopOutGizmos()
    {
        if (context == null) return;

        if (context.TopOutProbeActive)
        {
            // 手掌锚点（被选中的高位手 target）
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(context.TopOutProbeAnchor, 0.08f);
            // 向上偏移竖线：锚点 → 射线起点（起点沿墙面法线外拉，横向偏移很小）
            Gizmos.DrawLine(context.TopOutProbeAnchor, context.TopOutProbeOrigin);

            // 探测射线：黄色 = 命中墙面侧面（仍在爬）；绿色 = 未命中（到顶候选）
            Gizmos.color = context.TopOutProbeHitWall ? Color.yellow : Color.green;
            Gizmos.DrawLine(context.TopOutProbeOrigin, context.TopOutProbeEnd);
            Gizmos.DrawWireSphere(context.TopOutProbeOrigin, 0.06f);
            Gizmos.DrawWireSphere(context.TopOutProbeEnd, 0.06f);
        }

        // 登顶抓取点（Enter 一次性写入；退出登顶后清空）
        if (context.TopOutGrabPointValid)
        {
            Gizmos.color = context.TopOutGrabUsedScan ? Color.magenta : new Color(1f, 0.5f, 0f);
            Gizmos.DrawLine(context.TopOutGrabScanOrigin, context.TopOutGrabPoint);
            Gizmos.DrawWireSphere(context.TopOutGrabPoint, 0.1f);
        }
    }

    /// <summary>以 target 位置为原点绘制三色轴向（调参参照：红 X / 绿 Y / 蓝 Z）。</summary>
    static void DrawTargetAxes(Transform t)
    {
        const float axisLen = 0.12f;
        Gizmos.color = Color.red;
        Gizmos.DrawRay(t.position, t.right * axisLen);
        Gizmos.color = Color.green;
        Gizmos.DrawRay(t.position, t.up * axisLen);
        Gizmos.color = Color.blue;
        Gizmos.DrawRay(t.position, t.forward * axisLen);
    }

    #endregion

    #region 状态机（扁平单机：一个扁平状态枚举 Current + 一个状态类字典 + 一张转换边表）
    PlayerStateMachine machine;   // 唯一状态机（裁决 + 切换执行）
    PlayerContext context;        // 状态类的只读通道
    #endregion

    #region IK
    // IK 权重值缓存（float.NaN 初值保证首帧必写；同值帧跳过 constraint.weight 赋值）
    float lastRightHandWeight = float.NaN;
    float lastLeftHandWeight = float.NaN;
    float lastRightLegWeight = float.NaN;
    float lastLeftLegWeight = float.NaN;
    float lastRightArmChainWeight = float.NaN;
    float lastLeftArmChainWeight = float.NaN;
    float lastHeadAimWeight = float.NaN;
    float lastBodyAimWeight = float.NaN;

    /// <summary>
    /// IK 权重桥接（架构约定：权重由动画层参数控制，此处只做"读参数 → 回写 constraint.weight"）。
    /// 每帧读取动画参数并回写对应约束；约束未装配时跳过（null 防御，避免空手场景也 NRE）。
    /// 值缓存：仅当读到的参数与上次写入不同才赋 constraint.weight（多数帧同值，跳过赋值开销）。
    /// </summary>
    void SetIKweight()
    {
        WriteConstraintWeight(rightHandConstraint, AnimRightHandIKWeight, ref lastRightHandWeight);
        WriteConstraintWeight(leftHandConstraint, AnimLeftHandIKWeight, ref lastLeftHandWeight);
        WriteConstraintWeight(rightLegConstraint, AnimRightLegIKWeight, ref lastRightLegWeight);
        WriteConstraintWeight(leftLegConstraint, AnimLeftLegIKWeight, ref lastLeftLegWeight);
        WriteConstraintWeight(rightArmChainConstraint, AnimRightArmChainIKWeight, ref lastRightArmChainWeight);
        WriteConstraintWeight(leftArmChainConstraint, AnimLeftArmChainIKWeight, ref lastLeftArmChainWeight);
        WriteConstraintWeight(headAimConstraint, AnimHeadAimIKWeight, ref lastHeadAimWeight);
        WriteConstraintWeight(bodyAimConstraint, AnimBodyAimIKWeight, ref lastBodyAimWeight);
    }

    void WriteConstraintWeight(TwoBoneIKConstraint constraint, int paramHash, ref float lastWeight)
    {
        if (constraint == null) return;
        float next = animator.GetFloat(paramHash);
        if (lastWeight == next) return;
        lastWeight = next;
        constraint.weight = next;
    }

    void WriteConstraintWeight(ChainIKConstraint constraint, int paramHash, ref float lastWeight)
    {
        if (constraint == null) return;
        float next = animator.GetFloat(paramHash);
        if (lastWeight == next) return;
        lastWeight = next;
        constraint.weight = next;
    }

    void WriteConstraintWeight(MultiAimConstraint constraint, int paramHash, ref float lastWeight)
    {
        if (constraint == null) return;
        float next = animator.GetFloat(paramHash);
        if (lastWeight == next) return;
        lastWeight = next;
        constraint.weight = next;
    }

    /// <summary>
    /// 拔枪（Grab Rifle）/ 收枪（Put Rifle）期间接管右手 TwoBoneIK target 轨迹——
    /// 替代原 grab/put ik clip 里的 target TRS 曲线（clip 只保留权重曲线）。
    /// 背景：clip 一旦绑定 target，Animator 的 Write Defaults 会在切换帧（如 Grab Rifle→Rifle Idle）
    /// 把 target 写回初始化时缓存的场景值 → 与状态类标定写入竞态一帧；去掉绑定后 Animator 不再写它，
    /// 轨迹改由本方法按 "Switching Weapon" 动画进度逐帧写入（脚本侧唯一权威，时序同状态类 Update 写入）。
    /// 轨迹 = 标定位（chestRightHandAnchorLocalPosition/Euler）→ 中段位（rifleSwitchIkMidLocalPosition/Euler）
    /// → 回到标定位；grab/put 的离位/中段/回归时间窗取自原 clip 关键帧，见 PlayerMotionValuesSO。
    /// 非切换动画帧直接返回：常态由 Rifle_Normal_Ground_State.WriteRightHandCalibratedPose 维持标定值。
    /// 手枪/手雷不启用本接管：掏/收动作简单，拔枪/持枪帧由各自状态每帧直接写标定值。
    /// </summary>
    void UpdateRightHandSwitchIkTarget()
    {
        if (context == null || rightHandConstraint == null) return;
        Transform target = rightHandConstraint.data.target;
        if (target == null) return;

        // 切换动画（Grab/Put Rifle，tag "Switching Weapon"）未播放 → 不接管（状态类写标定值）
        if (!context.TryGetWeaponSwitchAnimProgress(out float nt)) return;

        // 判别收枪还是拔枪：机器在 SlotRifle 按下当帧即切到空手/步枪，动画层随后/同时播 Put/Grab Rifle。
        // 收枪不能只看当前 Handing（收任何武器后都是 Unarmed），用切换前手持识别是否步枪收枪；
        // 手枪/手雷不采用本接管（掏/收动作简单，拔枪/持枪帧由状态直接写标定值），在此排除。
        bool isGrab = context.Handing == PlayerHanding.Rifle;
        bool isPutRifle = context.Handing == PlayerHanding.Unarmed
                          && context.Motion.PreviousHanding == PlayerHanding.Rifle;
        if (!isGrab && !isPutRifle) return;

        PlayerMotionValuesSO v = context.Values;
        float t = Mathf.Clamp01(nt);

        // 位置与旋转在原 clip 中的关键帧时间窗不同，分别采样（原 clip 关键帧间为 0 切线，
        // 离位段与回归段各用 SmoothStep 逼近）
        bool isPut = isPutRifle;
        SwitchIkWindow posWin = isGrab ? v.grabSwitchPositionWindow : v.putSwitchPositionWindow;
        SwitchIkWindow rotWin = isGrab ? v.grabSwitchRotationWindow : v.putSwitchRotationWindow;
        float posF = SampleSwitchIkDip(t, posWin);
        float rotF = SampleSwitchIkDip(t, rotWin);

        target.localPosition = Vector3.Lerp(v.chestRightHandAnchorLocalPosition,
                                            v.rifleSwitchIkMidLocalPosition, posF);
        target.localRotation = Quaternion.Euler(
            Vector3.Lerp(v.chestRightHandAnchorLocalEuler, v.rifleSwitchIkMidLocalEuler, rotF));

        if (v.rightHandIkFrameDebugLog)
        {
            Debug.Log($"[RightHandIK] Switch f{Time.frameCount} {target.name} " +
                      $"nt={t:F3} posBlend={posF:F2} rotBlend={rotF:F2} localPos={target.localPosition:F3} " +
                      $"localRot={target.localRotation.eulerAngles:F1} (grab={isGrab})");
        }
    }

    /// <summary>切换 IK 中段轨迹采样：标定位 → 中段位 → 标定位（0..1，窗内 SmoothStep 缓动）。</summary>
    static float SampleSwitchIkDip(float t, SwitchIkWindow window)
    {
        if (t <= window.rampInStart || t >= window.rampOutEnd) return 0f;
        if (t < window.dipMid)
            return SmoothStep01((t - window.rampInStart) / (window.dipMid - window.rampInStart));
        return 1f - SmoothStep01((t - window.dipMid) / (window.rampOutEnd - window.dipMid));
    }

    /// <summary>归一化缓动（smoothstep，0→1 两端零导数，与原 0 切线关键帧语义一致）。</summary>
    static float SmoothStep01(float t) => t * t * (3f - 2f * t);

    /// <summary>
    /// 瞄准进出交叉淡化帧处理右手两套 IK 的 target 对齐：
    /// 持枪站立时右臂由 TwoBoneIK（target = 标定位，Chest 子物体）接管；
    /// 瞄准时右臂由 ChainIK（target = 瞄准腕位，Rifle_Aiming_Ground_State 每帧写入）接管。
    /// 动画层在正常/瞄准状态间 crossfade 时两者权重同时 > 0，若两 target 各执一词，
    /// 同一右臂链会被解向两个不同位姿 → 扭转/争抢（此前 target 由动画绑定跟随混合，
    /// 脚本接管后该自然混合消失，差异便暴露）。
    /// 本方法在权重重叠帧把两个 target 写到【同一世界位姿】：
    /// P = Lerp/Slerp(持枪标定位, 瞄准 Chain 位, chainW/(twoW+chainW))——随动画交叉淡化
    /// 从持枪位自然过渡到瞄准位（或反向），两个约束始终一致，不再争夺。
    /// 权重单一侧（TwoBoneIK 或 ChainIK 接近 0）由对应状态自行维持，本方法不介入。
    /// </summary>
    void UpdateRightHandAimBlendTargets()
    {
        if (context == null) return;
        if (rightHandConstraint == null || rightArmChainConstraint == null) return;

        Transform twoBoneTarget = rightHandConstraint.data.target;
        Transform chainTarget = rightArmChainConstraint.data.target;
        if (twoBoneTarget == null || chainTarget == null) return;

        float twoWeight = rightHandConstraint.weight;
        float chainWeight = rightArmChainConstraint.weight;
        const float overlapEpsilon = 0.005f;
        if (twoWeight <= overlapEpsilon || chainWeight <= overlapEpsilon) return;

        PlayerMotionValuesSO v = context.Values;

        // 持枪标定位：标定值（Chest 局部）还原到世界——按当前手持选择锚点：
        // 手枪用 pistolRightHandAnchor*、手雷用 grenadeRightHandAnchor*、其余（步枪）用 chestRightHandAnchor*
        PlayerHanding handing = context.Handing;
        bool isPistol = handing == PlayerHanding.Pistol;
        bool isGrenade = handing == PlayerHanding.Grenade;
        Vector3 holdAnchorLocalPos = isPistol
            ? v.pistolRightHandAnchorLocalPosition
            : isGrenade
                ? v.grenadeRightHandAnchorLocalPosition
                : v.chestRightHandAnchorLocalPosition;
        Quaternion holdAnchorLocalRot = Quaternion.Euler(isPistol
            ? v.pistolRightHandAnchorLocalEuler
            : isGrenade
                ? v.grenadeRightHandAnchorLocalEuler
                : v.chestRightHandAnchorLocalEuler);

        Transform anchor = twoBoneTarget.parent;
        Vector3 holdPos = anchor != null
            ? anchor.TransformPoint(holdAnchorLocalPos)
            : twoBoneTarget.position;
        Quaternion holdRot = anchor != null
            ? anchor.rotation * holdAnchorLocalRot
            : twoBoneTarget.rotation;

        // 瞄准位：ChainIK target 当前值（进瞄准 = 本帧瞄准状态已写入；退瞄准 = 上一帧瞄准值快照）
        Vector3 aimPos = chainTarget.position;
        Quaternion aimRot = chainTarget.rotation;

        // 混合因子随权重比例推进：0 = 持枪位 … 1 = 瞄准位
        float t = chainWeight / (twoWeight + chainWeight);
        Vector3 pos = Vector3.Lerp(holdPos, aimPos, t);
        Quaternion rot = Quaternion.Slerp(holdRot, aimRot, t);

        twoBoneTarget.SetPositionAndRotation(pos, rot);
        chainTarget.SetPositionAndRotation(pos, rot);
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
        UpdateRightHandSwitchIkTarget(); // 拔/收枪切换帧：脚本按动画进度接管右手 target 轨迹（替代原 clip 内 target 曲线）
        UpdateRightHandAimBlendTargets(); // 瞄准进出交叉淡化帧：对齐右手 TwoBoneIK 与 ChainIK 的 target（消除争夺扭转）
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
    /// 轴点按手持选择：步枪/手枪 → aimAxisPoint（肩部附近）；手雷 → grenadeAimAxisPoint（肘部附近）。
    /// 轴向语义：沿用原枪口装配约定（aimAxis=Z_NEG）——aimAxisNegZ=true 时让轴点 -Z 指向瞄准点。
    /// </summary>
    void LateUpdate()
    {
        LogRightHandIkFrameEnd();   // 帧末调试日志：Animator 求值后、渲染前的实际值
        LogBodyPostureTimeline();   // 临时调试：body posture 参数过渡时间线（Animator 求值后读取，确认后删除）

        if (context == null || !context.AimRigActive) return;
        Transform pivot = context.Handing == PlayerHanding.Grenade
            ? context.GrenadeAxisPoint
            : context.AimAxisPoint;
        if (pivot == null) return;

        pivot.rotation = context.AxisPointRotation;
    }

    /// <summary>
    /// 帧末调试日志（Values.rightHandIkFrameDebugLog）：输出右手 IK target 在
    /// Animator 求值之后、渲染之前的实际局部值——与状态类「Write」日志对比：
    /// 帧末值 ≠ 写入值 = 有 Animator 写回（动画曲线绑定）在脚本之后覆盖了它。
    /// </summary>
    void LogRightHandIkFrameEnd()
    {
        if (context == null || !context.Values.rightHandIkFrameDebugLog) return;
        if (rightHandConstraint == null || rightHandConstraint.data.target == null) return;

        Transform t = rightHandConstraint.data.target;
        Debug.Log($"[RightHandIK] FrameEnd f{Time.frameCount} {t.name} " +
                  $"localPos={t.localPosition:F3} localRot={t.localRotation.eulerAngles:F1}");
    }

    /// <summary>
    /// 姿态三枚举 → 动画参数（body posture / hand posture / player handing）。
    /// 状态类只负责运动数据（速度分量），姿态切换后的动画分支由本处统一驱动；
    /// 覆盖"瞄准中跳跃 → 取消瞄准"等组合变化（切到 Normal 手部即回正常动画分支）。
    /// </summary>
    void SyncAnimatorPostureParams()
    {
        if (machine == null || context == null) return;
        // 经 AnimParams 缓存写入：姿态参数多数帧不变，同值跳过 SetInteger
        context.AnimParams.SetInteger(AnimBodyPosture, (int)machine.Body);
        context.AnimParams.SetInteger(AnimHandPosture, (int)machine.Hand);
        context.AnimParams.SetInteger(AnimPlayerHanding, (int)machine.Handing);
    }

    #region 调试 · body posture 参数过渡时间线（临时 DEBUG：排查脚本/动画状态机不同步，确认后删除）
    /// <summary>当前/下一状态名解析用状态短名表（须与 PlayerAnimationController 中 AnimatorState.m_Name 一致；未知名回退打印短名哈希）。</summary>
    static readonly string[] DebugBodyPostureStateNames =
    {
        "unarmed locomotion", "jump", "climb", "climb to end",
        "rifle unaiming locomotion", "pistol unaiming locomotion", "locomotion", "DoNothing"
    };

    int debugPostureLastParam = int.MinValue;
    bool debugPostureLastInTransition;
    int debugPostureLastCurrentHash;
    int debugPostureLastNextHash;

    /// <summary>
    /// 在 LateUpdate（Animator 求值后、当前帧渲染前）调用：打印动画机这一帧实际消费的
    /// body posture 参数值、基础层当前/下一状态与过渡进度，便于核对参数是否真的按 1→2→3 走过、
    /// 以及动画机在参数=3 期间是否仍停留在 jump→climb 过渡中。
    /// 只在参数/当前状态/下一状态/过渡标志发生变化时输出一行（形成紧凑时间线，不刷屏）。
    /// </summary>
    void LogBodyPostureTimeline()
    {
        if (!debugBodyPostureTimeline || machine == null || animator == null) return;

        int posture = animator.GetInteger(AnimBodyPosture);
        bool inTrans = animator.IsInTransition(0);
        var cur = animator.GetCurrentAnimatorStateInfo(0);
        var nxt = inTrans ? animator.GetNextAnimatorStateInfo(0) : default;
        int curHash = cur.shortNameHash;
        int nxtHash = inTrans ? nxt.shortNameHash : 0;

        bool changed = posture != debugPostureLastParam
                       || inTrans != debugPostureLastInTransition
                       || curHash != debugPostureLastCurrentHash
                       || nxtHash != debugPostureLastNextHash;
        if (!changed) return;

        debugPostureLastParam = posture;
        debugPostureLastInTransition = inTrans;
        debugPostureLastCurrentHash = curHash;
        debugPostureLastNextHash = nxtHash;

        string trans = "";
        if (inTrans)
        {
            var info = animator.GetAnimatorTransitionInfo(0);
            trans = $" | transDur={info.duration:F3}s transNt={info.normalizedTime:F3}";
        }
        string curTag = cur.tagHash == Animator.StringToHash("ClimbToEnd") ? " [Tag:ClimbToEnd]" : "";
        string nextTag = inTrans && nxt.tagHash == Animator.StringToHash("ClimbToEnd") ? " [nextTag:ClimbToEnd]" : "";
        Debug.Log($"[BodyPostureDebug] f{Time.frameCount} t={Time.time:F3} | scriptBody={(int)machine.Body} animPosture={posture} | " +
                  $"cur={DebugResolveStateName(curHash)} nt={cur.normalizedTime:F3}{curTag} next={DebugResolveStateName(nxtHash)}{nextTag} " +
                  $"inTrans={inTrans}{trans}", this);
    }

    static string DebugResolveStateName(int hash)
    {
        for (int i = 0; i < DebugBodyPostureStateNames.Length; i++)
        {
            if (Animator.StringToHash(DebugBodyPostureStateNames[i]) == hash) return DebugBodyPostureStateNames[i];
        }
        return $"#{hash}";
    }
    #endregion
    #endregion

    #region Animation Event 分发
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

    /// <summary>武器动画事件分发：按分发键调用对应武器（rifle/pistol）上 WeaponSwitch 的切换操作。</summary>
    void DispatchWeaponEvent(string message)
    {
        switch (message)
        {
            case "GrabRifle":
                SwitchWeaponMount(Rifle, true, message);   // 拔枪：切到使用中挂点
                break;
            case "PutRifle":
                SwitchWeaponMount(Rifle, false, message);  // 收枪：切到收纳挂点
                break;
            case "GrabPistol":
                SwitchWeaponMount(Pistol, true, message);  // 拔手枪：切到使用中挂点
                break;
            case "PutPistol":
                SwitchWeaponMount(Pistol, false, message); // 收手枪：切到收纳挂点
                break;
            case "GrabGrenade":
                SwitchWeaponMount(Grenade, true, message);   // 拔手雷：切到使用中挂点
                break;
            case "PutGrenade":
                SwitchWeaponMount(Grenade, false, message);  // 收手雷：切到收纳挂点
                break;
            default:
                Debug.LogWarning($"[WeaponEvent] 未处理的分发键：{message}", this);
                break;
        }
    }

    /// <summary>
    /// 把指定武器的单实体挂点切到使用中/收纳位。
    /// GetComponentInChildren：WeaponSwitch 挂在武器本体或内部子对象（如 handle）均可命中。
    /// </summary>
    void SwitchWeaponMount(GameObject weaponRoot, bool switchToInUse, string message)
    {
        if (weaponRoot == null)
        {
            Debug.LogWarning(
                $"[WeaponEvent] 武器事件 '{message}' 未找到武器根（Rifle/Pistol/Grenade 字段未指派）",
                this);
            return;
        }

        WeaponSwitch weaponSwitch = weaponRoot.GetComponentInChildren<WeaponSwitch>(true);
        if (weaponSwitch == null)
        {
            Debug.LogWarning(
                $"[WeaponEvent] 武器事件 '{message}' 未找到 WeaponSwitch（{weaponRoot.name}（含子物体）上无该组件）",
                this);
            return;
        }

        if (switchToInUse)
        {
            weaponSwitch.SwitchToInUse();      // isUsing = true
        }
        else
        {
            weaponSwitch.SwitchToStored();     // isUsing = false
        }
    }
    #endregion

    #region 攀爬进入检测（地面 Jump 信号边 + 滞空持久边共用条件：墙面命中 + 水平速度朝墙）
    /// <summary>
    /// 可攀爬判定（两处共用，见 InitStateMachine）：
    /// ① 墙面探测命中（参数化射线阵列，见 WallProbe）；
    /// ② 存在朝向墙面的速度——水平速度大小 ≥ 阈值，且方向与墙面法线反方向夹角 ≤ climbApproachAngle。
    /// 用途 1（地面 Jump 信号边）：跳跃键统一"移动动作"——命中 → 直接进攀爬，未命中 → 跳跃兜底；
    /// 用途 2（滞空持久边）：滞空期每帧重测——不论上升/下降，命中即抓墙进攀爬。起跳瞬间
    ///   离墙稍远（超出墙面探测距离）或水平速度不足导致地面判定未命中的场景，
    ///   由飞行/下落途中的本判定补上，不再只在"起跳按下"那一刻检测。
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
                                    headAimConstraint, bodyAimConstraint,
                                    rightArmChainConstraint, leftArmChainConstraint,
                                    Rifle != null ? Rifle.transform : null,
                                    aimAxisPoint,
                                    aimAxisOffset,
                                    RightHandWrist != null ? RightHandWrist.transform : null,
                                    Pistol != null ? Pistol.transform : null,
                                    pistolAimAxisOffset,
                                    Grenade != null ? Grenade.transform : null,
                                    grenadeAimAxisPoint,
                                    grenadeAimAxisOffset,
                                    Quaternion.Euler(grenadeAimAxisLocalEuler));

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
        machine.RegisterState(PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerStateFactory.Create(PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground));
        machine.RegisterState(PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            PlayerStateFactory.Create(PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping));
        machine.RegisterState(PlayerHanding.Pistol, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerStateFactory.Create(PlayerHanding.Pistol, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground));
        machine.RegisterState(PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerStateFactory.Create(PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground));
        machine.RegisterState(PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            PlayerStateFactory.Create(PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping));
        machine.RegisterState(PlayerHanding.Grenade, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerStateFactory.Create(PlayerHanding.Grenade, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground));
        machine.RegisterState(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,
            PlayerStateFactory.Create(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing));
        machine.RegisterState(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.ClimbTopOut,
            PlayerStateFactory.Create(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.ClimbTopOut));

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

        // ② 请求边：滞空 → 地面（落地；垂直速度 ≤ 判定阈值防起跳瞬间着地标志残留误判，对应 早期原型 落地断言）
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

        // ③a 持久边：滞空 → 攀爬（空中抓墙，上升/下降均可）——
        //    地面 Jump 判定只在"起跳按下瞬间"探测一次：起跳时离墙稍远（超过墙面探测距离）或
        //    水平速度尚不足即漏判，此后滞空期再无检测。本边在滞空期每帧用同一条件重测，
        //    飞行上升段 / 下落途中一旦墙面命中 + 水平速度朝墙即进入攀爬（不要求再次按键）。
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,
            condition: TryEnterClimb,
            evaluateEveryFrame: true,
            label: "空中抓墙（滞空·上升/下降均检测）"));

        // ③b 信号边：攀爬中按退出输入（QuitClimb）→ 退回默认姿态（地面）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            triggerSignal: PlayerInputState.Signal.QuitClimb,
            label: "退出攀爬"));

        // ③c 请求边：攀爬 → 地面（物理驱逐 / 自动退出，均由攀爬状态在 Tick 中提议）：
        //    - 墙面覆盖式复测缺失 ≥ climbWallExitMissRatio 连续 climbExitMissFrames 帧（ctx.ClimbWallLost）；
        //    - 向下爬至可着陆连续 climbDownExitFrames 帧（ctx.ClimbDownReachedGround）；
        //    - 兜底：严格探测 LastSucceeded=false（历史语义保留）。
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.WallProbe.LastSucceeded || ctx.ClimbWallLost || ctx.ClimbDownReachedGround,
            label: "退出攀爬（脱墙 / 向下爬至可着陆）"));

        // ③d 请求边：登顶 → 地面（登顶动画 Tag=ClimbToEnd 播放结束，由登顶状态 Tick 提议）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.ClimbTopOut,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            label: "登顶动画结束退出"));

        // ③e 请求边：攀爬 → 登顶（手部到顶探测命中后由攀爬状态提议并写入固定手，
        //    见 Unarmed_Normal_Climbing_State.TryRequestTopOut）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.ClimbTopOut,
            label: "到顶进入登顶（悬挂手固定）"));

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

        // —— 手枪 ——
        // ⑪ 信号边：空手按 2（SlotPistol）→ 切换手枪（地面正常手部）。
        //    条件阻塞：武器切换动画（Switching Weapon tag）播放期间禁止切换（与步枪 ④ 同规则）。
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.IsWeaponSwitchAnimPlaying(),
            triggerSignal: PlayerInputState.Signal.SlotPistol,
            label: "切换手枪"));

        // ⑪b 信号边：手枪地面再按 2（SlotPistol）→ 收回手枪（切回空手）。
        //    同样阻塞于武器切换动画（收回动画自身播放期间再按键无效）。
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.IsWeaponSwitchAnimPlaying(),
            triggerSignal: PlayerInputState.Signal.SlotPistol,
            label: "收回手枪（回空手）"));

        // ⑫ 信号边：手枪地面按下 Jump → 起跳（需物理着地；与步枪 ⑤ 规则一致）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.IsGrounded,
            triggerSignal: PlayerInputState.Signal.Jump,
            label: "跳跃（手枪·地面起跳）"));

        // ⑬ 请求边：手枪滞空 → 地面（落地；垂直速度 ≤ 判定阈值防起跳瞬间着地标志残留误判）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => ctx.IsGrounded && ctx.Motion.VerticalVelocity <= ctx.Values.landingVerticalThreshold,
            label: "落地（手枪）"));

        // ⑭ 请求边：手枪地面 → 滞空（走出边沿：离地且垂直速度越过死区阈值；
        //    条件与 Pistol_Normal_Ground_State 的提议一致）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.Motion.VerticalVelocity < ctx.Values.airborneFallThreshold
                           || ctx.Motion.VerticalVelocity > ctx.Values.airborneRiseThreshold,
            label: "走出边沿进入滞空（手枪）"));

        // ⑮ 手枪瞄准进出（两种模式按 Values.aimToggleMode 互斥，与步枪 ⑦ 同规则；支持运行时切模式）——
        //    按住模式（持久边）：按住 Aim 进入、松开退出
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Pistol, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.Values.aimToggleMode && ctx.Input.Aim,
            evaluateEveryFrame: true,
            label: "手枪·按住瞄准"));
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.Values.aimToggleMode && !ctx.Input.Aim,
            evaluateEveryFrame: true,
            label: "手枪·松开瞄准"));
        //    切换模式（信号边）：按一次 Aim 进瞄准、再按一次退出
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Pistol, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            condition: ctx => ctx.Values.aimToggleMode,
            triggerSignal: PlayerInputState.Signal.AimPressed,
            label: "手枪·切换瞄准（进）"));
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => ctx.Values.aimToggleMode,
            triggerSignal: PlayerInputState.Signal.AimPressed,
            label: "手枪·切换瞄准（出）"));

        // ⑯ 信号边：手枪瞄准中按 Jump → 取消瞄准并起跳（切到正常手部滞空；与步枪 ⑥ 同规则）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.IsGrounded,
            triggerSignal: PlayerInputState.Signal.Jump,
            label: "跳跃（手枪瞄准中·取消瞄准）"));

        // ⑰ 请求边：手枪瞄准中走出边沿 → 切滞空正常手部（瞄准无滞空组合，切换即取消瞄准）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Pistol, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Pistol, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.Motion.VerticalVelocity < ctx.Values.airborneFallThreshold
                           || ctx.Motion.VerticalVelocity > ctx.Values.airborneRiseThreshold,
            label: "瞄准中走出边沿进入滞空（手枪）"));

        // —— 手雷 ——
        // ⑱ 信号边：空手按 3（SlotGrenade）→ 切换手雷（地面正常手部）。
        //    条件阻塞：武器切换动画（Switching Weapon tag）播放期间禁止切换（与步枪 ④ 同规则）。
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.IsWeaponSwitchAnimPlaying(),
            triggerSignal: PlayerInputState.Signal.SlotGrenade,
            label: "切换手雷"));

        // ⑱b 信号边：手雷地面再按 3（SlotGrenade）→ 收回手雷（切回空手）。
        //    同样阻塞于武器切换动画（收回动画自身播放期间再按键无效）。
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.IsWeaponSwitchAnimPlaying(),
            triggerSignal: PlayerInputState.Signal.SlotGrenade,
            label: "收回手雷（回空手）"));

        // ⑲ 信号边：手雷地面按 Jump → 起跳（需物理着地；与手枪 ⑫ 规则一致）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.IsGrounded,
            triggerSignal: PlayerInputState.Signal.Jump,
            label: "跳跃（手雷·地面起跳）"));

        // ⑳ 请求边：手雷滞空 → 地面（落地；垂直速度 ≤ 判定阈值防起跳瞬间着地标志残留误判）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => ctx.IsGrounded && ctx.Motion.VerticalVelocity <= ctx.Values.landingVerticalThreshold,
            label: "落地（手雷）"));

        // ㉑ 请求边：手雷地面 → 滞空（走出边沿：离地且垂直速度越过死区阈值；
        //    条件与 Grenade_Normal_Ground_State 的提议一致）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.Motion.VerticalVelocity < ctx.Values.airborneFallThreshold
                           || ctx.Motion.VerticalVelocity > ctx.Values.airborneRiseThreshold,
            label: "走出边沿进入滞空（手雷）"));

        // —— 手雷瞄准（骨架：弧线/偏航求解后续接入，当前枪口方向 = 角色 yaw × 相机 pitch）——
        // ㉒ 按住模式（持久边）：按住 Aim 进入、松开退出
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Grenade, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.Values.aimToggleMode && ctx.Input.Aim,
            evaluateEveryFrame: true,
            label: "手雷·按住瞄准"));
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => !ctx.Values.aimToggleMode && !ctx.Input.Aim,
            evaluateEveryFrame: true,
            label: "手雷·松开瞄准"));
        //    切换模式（信号边）：按一次 Aim 进瞄准、再按一次退出
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            PlayerHanding.Grenade, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            condition: ctx => ctx.Values.aimToggleMode,
            triggerSignal: PlayerInputState.Signal.AimPressed,
            label: "手雷·切换瞄准（进）"));
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
            condition: ctx => ctx.Values.aimToggleMode,
            triggerSignal: PlayerInputState.Signal.AimPressed,
            label: "手雷·切换瞄准（出）"));

        // ㉖ 信号边：手雷瞄准中按 Jump → 取消瞄准并起跳（与步枪 ⑥ 同规则）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.IsGrounded,
            triggerSignal: PlayerInputState.Signal.Jump,
            label: "跳跃（手雷瞄准中·取消瞄准）"));

        // ㉗ 请求边：手雷瞄准中走出边沿 → 切滞空正常手部（瞄准无滞空组合，切换即取消瞄准）
        machine.RegisterEdge(new TransitionEdge(
            PlayerHanding.Grenade, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
            PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
            condition: ctx => ctx.Motion.VerticalVelocity < ctx.Values.airborneFallThreshold
                           || ctx.Motion.VerticalVelocity > ctx.Values.airborneRiseThreshold,
            label: "瞄准中走出边沿进入滞空（手雷）"));

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
    public void GetPistolInput(InputAction.CallbackContext ctx)
    {
        InputActionBridge.OnPistol(input, ctx);
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
