using UnityEngine;

/// <summary>
/// 运动数值服务（设计文档 §5 数值层的"数值"部分）：物理常量 + 纯计算函数。
/// 只负责"算数"——速度插值 / 重力累积 / 跳跃初速反推，
/// 不含姿态判定与切换决策（那是状态类与转换边的职责）。
///
/// 默认数值与 test.cs 的 MotionState（可行性原型）保持一致，保证行为回归；
/// [SerializeField] 便于在 Inspector 调试调参（字段均带 [Tooltip]，悬停可看含义）。
/// 状态类经 PlayerContext.Values 访问；计算结果写回 PlayerContext.Motion（跨状态交接槽）。
/// </summary>
[System.Serializable]
public class PlayerMotionValues
{
    [Header("速度档位（目标 = 档位 × 输入模长）")]
    [Tooltip("步枪（持枪）行走速度（m/s）：非瞄准步枪地面状态的目标档位")]
    public float rifleWalkSpeed = 1.5f;

    [Tooltip("步枪（持枪）奔跑速度（m/s）：步枪 + Shift 目标档位")]
    public float rifleRunSpeed = 3.5f;

    [Tooltip("无武器行走速度（m/s）：空手地面状态目标档位")]
    public float unarmedWalkSpeed = 2f;

    [Tooltip("无武器奔跑速度（m/s）：空手 + Shift 目标档位")]
    public float unarmedRunSpeed = 4f;

    [Header("加速度与转向（插值率：速度每秒变化量 m/s²；转向 °/s）")]
    [Tooltip("地面档位速度插值加速度（m/s²）：空手/步枪地面速度以该加速度逼近目标（MoveTowards 恒速）")]
    public float moveAcceleration = 12f;

    [Tooltip("瞄准速度插值加速度（m/s²）：瞄准档位与前后/左右轴速度均以该加速度逼近目标（瞄准锁定行走档）")]
    public float aimAcceleration = 12f;

    [Tooltip("转向速度（°/s）：移动时角色以该角速度 RotateTowards 旋转到移动方向（1000 ≈ 瞬时转身）")]
    public float rotateSpeed = 1000f;

    [Tooltip("瞄准转向平滑时间（s）：瞄准时角色朝向相机水平前方的 SmoothDampAngle 接近时间——越小越跟手，越大越「肉」（推荐 0.1~0.2）")]
    public float aimRotateSmoothTime = 0.12f;

    [Tooltip("瞄准转向最大角速度（°/s）：SmoothDampAngle 限速钳制，防止大角度误差时首帧跳变过陡（0 = 不限制）")]
    public float aimRotateMaxSpeed = 360f;

    [Tooltip("武器切换速度过渡兜底时长（s）：优先与 Switching Weapon 动画进度同步（动画播完=插值完成）；未检测到该动画时的兜底计时（0 = 不启用，仍用 moveAcceleration 恒加速逼近）")]
    public float weaponSwitchSpeedBlendTime = 0.5f;

    [Tooltip("瞄准进入对齐时长（s）：进入瞄准后此时间内使用高响应对齐转向（aimAlignSmoothTime），之后恢复常规瞄准转向（aimRotateSmoothTime）——兼顾「进入先快速到位、日常跟随平稳」")]
    public float aimAlignTime = 0.35f;

    [Tooltip("瞄准进入对齐的平滑时间（s）：进入瞄准后的对齐阶段使用（越小进入转身越快，但仍是平滑插值，无瞬转）")]
    public float aimAlignSmoothTime = 0.05f;

    [Header("垂直运动（手写重力）")]
    [Tooltip("重力加速度（m/s²，向下为负）：滞空阶段垂直速度每帧累积该值")]
    public float gravity = -15f;

    [Tooltip("预设跳跃高度（m）：起跳初速度由它和重力反推 v = √(2|g|h)")]
    public float jumpHeight = 0.8f;

    [Tooltip("着地时向下压速度（m/s，负值）：保持角色与地面接触，防止静止时轻微悬浮")]
    public float groundStickSpeed = -2f;

    [Tooltip("落地过渡时长（s）：落地后保持捕获的下落速度给动画的时长，应与动画 body posture 过渡时长对齐")]
    public float landingBlendTime = 0.25f;

    [Header("滞空判定阈值")]
    [Tooltip("下落死区下界（m/s）：离地后垂直速度低于该值才判定进入滞空（防边缘/台阶抖动）")]
    public float airborneFallThreshold = -2.5f;

    [Tooltip("上升死区上界（m/s）：离地后垂直速度高于该值才判定进入滞空（通常只有下落段触发）")]
    public float airborneRiseThreshold = 2f;

    [Tooltip("落地判定阈值（m/s）：垂直速度 ≤ 该值视为已不再上升，允许落地 / 触发下落速度捕获（0 = 下落或悬停）")]
    public float landingVerticalThreshold = 0f;

    [Header("输入判定")]
    [Tooltip("输入/方向向量的平方长度下限：低于视为无效（防零向量归一化抖动）")]
    public float minMoveSqrMagnitude = 0.0001f;

    [Header("瞄准引导（Multi-Aim 约束目标）")]
    [Tooltip("瞄准射线距离（m）：瞄准状态从主相机中心向前的射线最大距离（命中检测上限，供准星判定/调试）")]
    public float aimRayDistance = 100f;

    [Tooltip("视线远点距离（m）：枪口/头/胸的瞄准方向统一指向【相机 forward × 该距离处的远点】（不再是命中表面点）。方向随视线连续变化 → 命中↔未命中切换不再引起枪口转向跳变；近物命中时子弹（未来从出弹点沿该方向）与准星的微小视差偏移后续用弹道特效掩盖")]
    public float aimMissPointDistance = 100f;

    [Tooltip("瞄准射线层掩码：射线只检测这些层。注意排除玩家自身层，否则会命中角色自己的碰撞体（默认排除 Unity 内建 Player 层 6——当前角色所在层）")]
    public LayerMask aimRayMask = ~(1 << 6);

    [Tooltip("轴点旋转最大角速度（°/s）：瞄准时轴点 worldRotation 程序化覆盖的限速——正常跟随不受影响（远大于每帧需求），快速转身时把旋转拉成连续受控过渡（原 Multi-Aim ±限制的防甩语义由它接管）")]
    public float aimAxisMaxRotSpeed = 720f;

    [Tooltip("枪口方向 = 轴点 -Z（与原 rifle Multi-Aim 的 aimAxis=Z_NEG 装配一致）；若枪口实际朝向与轴点-Z 不符则改为 false（对应 +Z 语义）")]
    public bool aimAxisNegZ = true;

    [Tooltip("手-枪稳态握位重捕获延迟（s）：进入瞄准后等待该时长（需大于动画 crossfade，动画完成=手位稳定）再【一次性】重捕获腕-枪相对位姿锁定为稳态握位；等待期间手用进入瞬间捕获值贴枪（连续、不脱手、不穿模）")]
    public float aimHandCaptureTime = 0.5f;

    [Tooltip("稳态握位过渡时长（s）：重捕获后从进入瞬间握位平滑过渡到稳态握位的时间（手在枪上滑动，无跳变）")]
    public float aimHandRelockBlendTime = 0.15f;

    [Tooltip("瞄准模式：关 = 按住右键瞄准（松开退出）；开 = 右键切换瞄准（按一次进入、再按一次退出）")]
    public bool aimToggleMode = false;

    [Tooltip("瞄准程序化 IK：进入瞄准时枪根从持枪位平滑过渡到 aimAxisOffset 的时长（s）。期间枪口 aim 指向角色身前而非相机瞄准点（角色仍在转身对齐）")]
    public float aimRigTransitionTime = 0.35f;

    [Tooltip("非瞄准时水平面左右轴速度（m/s）：角色朝向移动方向，全速计入前后轴，左右轴固定用此值（0 = 无侧移分量）")]
    public float nonAimLateralSpeed = 0f;

    [Header("左手 IK 锚点（专用变量：护木锚点 = 武器的子物体 local；值已标定）")]
    [Tooltip("左手 IK 锚点作为【武器子物体】的 local 位置——标定值（来源：原装配复合值 AK74 相对 handle (0,0.065,-0.0906) + 护木锚点相对 AK74 (0.089,-0.016,-0.158)，AK74 无旋转直接相加；已在场景标定生效，勿随意改；调整时按“场景摆枪→读该对象相对枪根 local”重标")] public Vector3 gunLeftHandAnchorLocalPosition = new Vector3(0.089f, 0.049f, -0.2486f);
    [Tooltip("左手 IK 锚点作为【武器子物体】的 local 旋转（Euler）——标定值（来源：护木锚点相对 AK74 的旋转 (63.164,-10.148,112.995)；场景标定生效，勿随意改")] public Vector3 gunLeftHandAnchorLocalEuler = new Vector3(63.164f, -10.148f, 112.995f);

    [Header("右手 IK 锚点（专用变量：枪把手锚点 = Chest 骨骼子物体 local；值已标定）")]
    [Tooltip("右手 IK 锚点作为【Chest 骨骼子物体】的 local 位置——标定值（2026-09 重新标定：右手 TwoBoneIK target 相对 Chest 的局部坐标；target 在装配中是 Chest 子级（与原动画曲线同空间），标定生效，勿随意改")] public Vector3 chestRightHandAnchorLocalPosition = new Vector3(0.18f, -0.117f, 0.072f);
    [Tooltip("右手 IK 锚点作为【Chest 骨骼子物体】的 local 旋转（Euler）——标定值（2026-09 重新标定，与上方位置同批；标定生效，勿随意改")] public Vector3 chestRightHandAnchorLocalEuler = new Vector3(-230.47f, 185.38f, 261.551f);
    [Tooltip("右手 IK 逐帧调试日志：常态帧打印「Write（标定写入）」与「FrameEnd（帧末实际值）」，帧末 ≠ 写入即 Animator 写回覆盖；拔/收枪切换帧打印「Switch」说明由脚本按动画进度接管（帧末 ≠ Write 属预期）（调试完关闭）")]
    public bool rightHandIkFrameDebugLog = true;

    [Header("右手拔/收枪切换 IK 轨迹（grab/put ik clip 的 target 曲线改由脚本接管）")]
    [Tooltip("拔/收枪动画中段（握枪/松手瞬间）target 的局部位姿——原 grab/put ik clip 的中间关键帧值 (0.1499, 0.3577, -0.1358)，勿随意改；重新标定参照 clip 原曲线")]
    public Vector3 rifleSwitchIkMidLocalPosition = new Vector3(0.1499f, 0.3577f, -0.1358f);
    [Tooltip("拔/收枪动画中段 target 的局部旋转（Euler）——原 grab/put ik clip 的中间关键帧值 (-50, 185.38, 261.551)，勿随意改")]
    public Vector3 rifleSwitchIkMidLocalEuler = new Vector3(-50f, 185.38f, 261.551f);

    [Tooltip("grab rifle ik（1.1833s）位置轨迹归一化时间窗：开始离位 / 到达中段 / 回到标定位（由 clip 位置关键帧 0.15s / 0.4667s / 0.85s ÷ 时长换算）")]
    public SwitchIkWindow grabSwitchPositionWindow = new SwitchIkWindow(0.1268f, 0.3944f, 0.7183f);
    [Tooltip("grab rifle ik 旋转轨迹归一化时间窗：由 clip 欧拉关键帧 0s / 0.4667s / 1.1833s ÷ 时长换算（旋转自 0 即开始离位、到结尾才回位）")]
    public SwitchIkWindow grabSwitchRotationWindow = new SwitchIkWindow(0f, 0.3944f, 1f);
    [Tooltip("put rifle ik（1.8167s）位置轨迹归一化时间窗：由 clip 位置关键帧 0.35s / 0.8333s / 1.3334s ÷ 时长换算")]
    public SwitchIkWindow putSwitchPositionWindow = new SwitchIkWindow(0.1927f, 0.4587f, 0.734f);
    [Tooltip("put rifle ik 旋转轨迹归一化时间窗：由 clip 欧拉关键帧 0s / 0.8333s / 1.8s ÷ 时长换算（旋转自 0 即开始离位、接近结尾才回位）")]
    public SwitchIkWindow putSwitchRotationWindow = new SwitchIkWindow(0f, 0.4587f, 0.9908f);

    [Header("攀爬")]
    [Tooltip("攀爬移动速度（m/s）：输入映射到墙面切平面后的移动速度")]
    public float climbSpeed = 2f;

    [Tooltip("进入攀爬的朝向角（°）：水平速度方向与墙面法线反方向的最大夹角（速度朝墙才允许进攀爬）")]
    public float climbApproachAngle = 60f;

    [Tooltip("进入攀爬的最小水平速度（m/s）：低于该值视为无朝向墙面的移动")]
    public float climbApproachMinSpeed = 1f;

    [Tooltip("墙面连续探测失败多少帧后退出攀爬（防边界抖动）")]
    public int climbExitMissFrames = 3;

    [Tooltip("攀爬时角色根部到墙面的目标距离（让手够得着墙，配合手部 IK 使用）")]
    public float climbWallHugDistance = 0.3f;

    [Tooltip("贴墙收敛速度（每秒收敛比例）")]
    public float climbWallHugSpeed = 8f;

    [Header("攀爬手部 IK（程序化：跟随头部的水平线 + 中线对称姿态；只移动 TwoBoneIK target，权重由动画状态机管理）")]
    [Tooltip("水平线高度（m）：手部姿态的基准水平线相对头部骨骼的高度（沿墙面向上为正；该线每帧跟随头部）")]
    public float climbHandLineHeight = 0.1f;

    [Tooltip("中线距离（m）：左右手相对中线（身体在墙面上投影的中心竖线）的横向距离；上下攀爬时双手持该距离不动")]
    public float climbHandCenterDist = 0.25f;

    [Tooltip("交替长度（m）：上下攀爬时双手交替幅度——手在水平线上方/下方各距水平线的距离（两手一致、上下对称）")]
    public float climbHandAlternateLength = 0.18f;

    [Tooltip("放置距离（m）：左右攀爬时上下放置距离——对应移动方向侧的手在水平线下方、相反侧手在上方，双方距水平线的距离（一致）")]
    public float climbHandPlaceDist = 0.15f;

    [Tooltip("开合幅度（m）：左右攀爬时双手横向收拢/打开的摆动幅度：收拢 = 中线距离 − 开合幅度，打开 = 中线距离 + 开合幅度（双手幅度一致）")]
    public float climbHandOpenAmount = 0.12f;

    [Tooltip("姿态切换时间（s）：手部姿态（上下换手 / 收开互换）切换的平滑过渡时长")]
    public float climbHandSwitchTime = 0.4f;

    [Tooltip("步进交替间隔（s）：持续同方向攀爬时手臂姿态交替（上下换手 / 收开互换）的间隔")]
    public float climbHandStepInterval = 0.6f;

    [Tooltip("最大臂展（m）：手部目标相对基准点（头部骨骼 + 水平线偏移）在墙面平面内的最大偏移；超限被钳制以保证双臂可达")]
    public float climbHandMaxReach = 0.35f;

    [Tooltip("攀爬 IK 调试可视化（Scene 视图）：青色 = IK 目标，黄色 = 实际手骨（腕），红色 = 目标到手骨的连线（线越长 = IK 越没拉到位）")]
    public bool climbIkDebugGizmos = true;

    [Tooltip("退出攀爬时把手/头 IK 权重参数归零（脚本侧兜底）：动画层 climbing 退出后无状态驱动这些参数（层回 DoNothing 无曲线），参数会滞留 1 导致退攀后双手被钉在旧目标上；开启后由状态 Exit 写 0。若后续动画层补上权重曲线可关闭")]
    public bool climbResetIkWeightsOnExit = true;

    /// <summary>起跳初速度：由跳跃高度与重力反推 v = √(2·|g|·h)。</summary>
    public float JumpSpeed => Mathf.Sqrt(2f * Mathf.Abs(gravity) * jumpHeight);

    /// <summary>
    /// 步枪（持枪）档位速度插值：目标 = 档位（走 1.5 / 跑 3.5）× 输入模长，恒加速度逼近。
    /// </summary>
    public float MoveRifleSpeed(float current, float inputMagnitude, bool run, float deltaTime)
        => Mathf.MoveTowards(current, (run ? rifleRunSpeed : rifleWalkSpeed) * inputMagnitude,
                             moveAcceleration * deltaTime);

    /// <summary>
    /// 无武器档位速度插值：目标 = 无武器档位（走 2 / 跑 4）× 输入模长，恒加速度逼近。
    /// </summary>
    public float MoveUnarmedSpeed(float current, float inputMagnitude, bool run, float deltaTime)
        => Mathf.MoveTowards(current, (run ? unarmedRunSpeed : unarmedWalkSpeed) * inputMagnitude,
                             moveAcceleration * deltaTime);

    /// <summary>
    /// 瞄准档位速度插值：目标 = 步枪行走档 × 输入模长，恒加速度逼近。
    /// 瞄准不接受奔跑（Shift 无效，锁定行走档）——规划上"跑 + 瞄准"表现不可用，
    /// 且瞄准 2D 树横向带宽与跑档不匹配（参数会滞留两环之间导致滑步）。
    /// </summary>
    public float MoveAimSpeed(float current, float inputMagnitude, float deltaTime)
        => Mathf.MoveTowards(current, rifleWalkSpeed * inputMagnitude, aimAcceleration * deltaTime);

    /// <summary>重力累积（一帧）。</summary>
    public float ApplyGravity(float verticalVelocity, float deltaTime)
        => verticalVelocity + gravity * deltaTime;
}

/// <summary>拔/收枪切换 IK 的一段“离位-中段-回位”归一化时间窗（0..1，对应原 clip 关键帧换算时间）。</summary>
[System.Serializable]
public class SwitchIkWindow
{
    /// <summary>轨迹开始离开标定位的归一化时间。</summary>
    public float rampInStart;
    /// <summary>到达中段位（rifleSwitchIkMidLocal*）的归一化时间。</summary>
    public float dipMid;
    /// <summary>回到标定位的归一化时间。</summary>
    public float rampOutEnd;

    public SwitchIkWindow(float rampInStart, float dipMid, float rampOutEnd)
    {
        this.rampInStart = rampInStart;
        this.dipMid = dipMid;
        this.rampOutEnd = rampOutEnd;
    }
}
