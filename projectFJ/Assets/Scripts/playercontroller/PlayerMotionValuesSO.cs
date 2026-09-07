using UnityEngine;

/// <summary>
/// 运动数值服务（设计文档 §5 数值层的"数值"部分）——ScriptableObject 资产版。
///
/// 背景：早期版本把数值放在 MonoBehaviour 内嵌的 [Serializable] 类（PlayerMotionValues）里，
/// Play Mode 中改 Inspector 会因组件重新序列化而失效（旧引用读到旧值）。按项目规范
/// （AGENTS.md 工作约定：数值管理优先考虑 ScriptableObject），数值改为资产级单例承载：
/// 场景/prefab 组件只持引用，修改资产即全局生效、且支持 Play Mode 实时调参。
///
/// 约定：
/// 1. 字段【直接摊在本类顶层】，不要再用 [Serializable] 子类包一层——否则 SO 资产被
///    Inspector 重新序列化时，内嵌子类实例仍会被替换，代码缓存其引用会重蹈覆辙。
///    唯一例外是叶级只读数据 SwitchIkWindow（拔/收枪时间窗），定义在同文件底部、
///    以内嵌实例序列化进本资产；消费侧每次经 Values 现取，不得长期缓存其引用。
/// 2. 本文件是运动数值的唯一定义来源（旧内嵌类已删除，资产数据不依赖任何旧文件）。
/// 3. 只负责"算数"——速度插值 / 重力累积 / 跳跃初速反推，不含姿态判定与切换决策。
///    状态类经 PlayerContext.Values 访问；计算结果写回 PlayerContext.Motion。
///    按 Handing 的档位查询（GroundTargetSpeed）属数据索引，是唯一的分支例外（非姿态判定）。
///
/// 参数分组说明：字段按功能域分类（运动 / 瞄准 / 手部 IK / 切换 IK / 攀爬 / 登顶），
/// 组内再按使用顺序排布；字段名是序列化键，整理时不可改名（见 code-conventions §2）。
/// </summary>
[CreateAssetMenu(fileName = "PlayerMotionValues", menuName = "Player/Motion Values")]
public class PlayerMotionValuesSO : ScriptableObject
{
    [Header("运动 · 速度档位（目标 = 档位 × 输入模长，按手持分类）")]
    [Tooltip("无武器行走速度（m/s）：空手地面状态目标档位")]
    public float unarmedWalkSpeed = 2f;

    [Tooltip("无武器奔跑速度（m/s）：空手 + Shift 目标档位")]
    public float unarmedRunSpeed = 4f;

    [Tooltip("步枪（持枪）行走速度（m/s）：非瞄准步枪地面状态的目标档位")]
    public float rifleWalkSpeed = 1.5f;

    [Tooltip("步枪（持枪）奔跑速度（m/s）：步枪 + Shift 目标档位")]
    public float rifleRunSpeed = 3.5f;

    [Tooltip("手枪（持枪）行走速度（m/s）：手枪地面状态目标档位（暂定 2，Inspector 可调）")]
    public float pistolWalkSpeed = 2f;

    [Tooltip("手枪（持枪）奔跑速度（m/s）：手枪 + Shift 目标档位（暂定 4，Inspector 可调）")]
    public float pistolRunSpeed = 4f;

    [Tooltip("手雷（持有）行走速度（m/s）：手雷地面状态目标档位（暂定与手枪同值 2，Inspector 可调）")]
    public float grenadeWalkSpeed = 2f;

    [Tooltip("手雷（持有）奔跑速度（m/s）：手雷 + Shift 目标档位（暂定与手枪同值 4，Inspector 可调）")]
    public float grenadeRunSpeed = 4f;

    [Header("手雷投掷 · 弧线预览（探测 / 显示 / 淡入）")]
    [Tooltip("落点探测精化步长（s）：粗采样进入落地区间后，用该步长精化截断点")]
    public float grenadeArcDetectTimeStep = 0.02f;

    [Tooltip("落点探测粗采样最大步数（0.1s × N ≈ 飞行上限；默认 60 ≈ 6s）")]
    public int grenadeArcDetectMaxSteps = 60;

    [Tooltip("着陆探测：垂直射线起点相对采样点的上抬（m）")]
    public float grenadeArcGroundProbeUp = 0.5f;

    [Tooltip("着陆探测：垂直射线最大长度（m）")]
    public float grenadeArcGroundProbeDistance = 5f;

    [Tooltip("着陆判定：采样点高度接近地面该值以内视为落地（m）")]
    public float grenadeArcLandingTolerance = 0.12f;

    [Tooltip("手雷弧线预览显示采样点数：越大弧线越平滑（仅显示密度；与落点探测解耦）。投掷初速不在此处配置，读取手雷上 BulletManage.bulletData.bulletInitialSpeed")]
    public int grenadeArcSampleCount = 60;

    [Tooltip("弧线淡入时长（s）：进入瞄准并稳定显示后，透明度从 0 平滑升到 1")]
    public float grenadeArcFadeInTime = 0.25f;

    [Tooltip("进瞄准后弧线显示延迟（s）：等手雷根过渡/手部到位后再浮现弧线")]
    public float grenadeArcShowDelay = 0.15f;

    [Header("运动 · 插值与转向")]
    [Tooltip("地面档位速度插值加速度（m/s²）：空手/步枪/手枪地面速度以该加速度逼近目标（MoveTowards 恒速）")]
    public float moveAcceleration = 12f;

    [Tooltip("转向速度（°/s）：移动时角色以该角速度 RotateTowards 旋转到移动方向（1000 ≈ 瞬时转身）")]
    public float rotateSpeed = 1000f;

    [Header("运动 · 武器切换速度过渡")]
    [Tooltip("武器切换速度过渡兜底时长（s）：优先与 Switching Weapon 动画进度同步（动画播完=插值完成）；未检测到该动画时的兜底计时（0 = 不启用，仍用 moveAcceleration 恒加速逼近）")]
    public float weaponSwitchSpeedBlendTime = 0.5f;

    [Header("运动 · 垂直（手写重力与跳跃）")]
    [Tooltip("重力加速度（m/s²，向下为负）：滞空阶段垂直速度每帧累积该值")]
    public float gravity = -15f;

    [Tooltip("预设跳跃高度（m）：起跳初速度由它和重力反推 v = √(2|g|h)")]
    public float jumpHeight = 0.8f;

    [Tooltip("着地时向下压速度（m/s，负值）：保持角色与地面接触，防止静止时轻微悬浮")]
    public float groundStickSpeed = -2f;

    [Header("运动 · 滞空/落地判定与落地过渡")]
    [Tooltip("下落死区下界（m/s）：离地后垂直速度低于该值才判定进入滞空（防边缘/台阶抖动）")]
    public float airborneFallThreshold = -2.5f;

    [Tooltip("上升死区上界（m/s）：离地后垂直速度高于该值才判定进入滞空（通常只有下落段触发）")]
    public float airborneRiseThreshold = 2f;

    [Tooltip("落地判定阈值（m/s）：垂直速度 ≤ 该值视为已不再上升，允许落地 / 触发下落速度捕获（0 = 下落或悬停）")]
    public float landingVerticalThreshold = 0f;

    [Tooltip("落地过渡时长（s）：落地后保持捕获的下落速度给动画的时长，应与动画 body posture 过渡时长对齐")]
    public float landingBlendTime = 0.25f;

    [Header("运动 · 输入判定与动画参数输出")]
    [Tooltip("输入/方向向量的平方长度下限：低于视为无效（防零向量归一化抖动）")]
    public float minMoveSqrMagnitude = 0.0001f;

    [Tooltip("非瞄准时水平面左右轴速度（m/s）：角色朝向移动方向，全速计入前后轴，左右轴固定用此值（0 = 无侧移分量）")]
    public float nonAimLateralSpeed = 0f;

    [Header("瞄准 · 输入模式与速度插值")]
    [Tooltip("瞄准模式：关 = 按住右键瞄准（松开退出）；开 = 右键切换瞄准（按一次进入、再按一次退出）")]
    public bool aimToggleMode = false;

    [Tooltip("瞄准速度插值加速度（m/s²）：瞄准档位与前后/左右轴速度均以该加速度逼近目标（瞄准锁定行走档）")]
    public float aimAcceleration = 12f;

    [Header("瞄准 · 角色转向（追相机）")]
    [Tooltip("瞄准进入对齐时长（s）：进入瞄准后此时间内使用高响应对齐转向（aimAlignSmoothTime），之后恢复常规瞄准转向（aimRotateSmoothTime）——兼顾「进入先快速到位、日常跟随平稳」")]
    public float aimAlignTime = 0.35f;

    [Tooltip("瞄准进入对齐的平滑时间（s）：进入瞄准后的对齐阶段使用（越小进入转身越快，但仍是平滑插值，无瞬转）")]
    public float aimAlignSmoothTime = 0.05f;

    [Tooltip("瞄准转向平滑时间（s）：瞄准时角色朝向相机水平前方的 SmoothDampAngle 接近时间——越小越跟手，越大越「肉」（推荐 0.1~0.2）")]
    public float aimRotateSmoothTime = 0.12f;

    [Tooltip("瞄准转向最大角速度（°/s）：SmoothDampAngle 限速钳制，防止大角度误差时首帧跳变过陡（0 = 不限制）")]
    public float aimRotateMaxSpeed = 360f;

    [Header("瞄准 · 视线与落点（相机射线）")]
    [Tooltip("瞄准射线距离（m）：瞄准状态从主相机中心向前的射线最大距离（命中检测上限，供准星判定/调试）")]
    public float aimRayDistance = 100f;

    [Tooltip("瞄准射线层掩码：射线只检测这些层。注意排除玩家自身层，否则会命中角色自己的碰撞体（默认排除 Unity 内建 Player 层 6——当前角色所在层）")]
    public LayerMask aimRayMask = ~(1 << 6);

    [Tooltip("视线远点距离（m）：枪口/头/胸的瞄准方向统一指向【相机 forward × 该距离处的远点】（不再是命中表面点）。方向随视线连续变化 → 命中↔未命中切换不再引起枪口转向跳变；近物命中时子弹（未来从出弹点沿该方向）与准星的微小视差偏移后续用弹道特效掩盖")]
    public float aimMissPointDistance = 100f;

    [Header("瞄准 · 程序化枪姿（轴点旋转 / 枪根过渡 / 手锁定）")]
    [Tooltip("瞄准程序化 IK：进入瞄准时枪根从持枪位平滑过渡到 aimAxisOffset 的时长（s）。期间枪口 aim 指向角色身前而非相机瞄准点（角色仍在转身对齐）")]
    public float aimRigTransitionTime = 0.35f;

    [Tooltip("轴点旋转最大角速度（°/s）：瞄准时轴点 worldRotation 程序化覆盖的限速——正常跟随不受影响（远大于每帧需求），快速转身时把旋转拉成连续受控过渡（原 Multi-Aim ±限制的防甩语义由它接管）")]
    public float aimAxisMaxRotSpeed = 720f;

    [Tooltip("枪口方向 = 轴点 -Z（沿用原枪口装配约定 aimAxis=Z_NEG）；若枪口实际朝向与轴点-Z 不符则改为 false（对应 +Z 语义）")]
    public bool aimAxisNegZ = true;

    [Tooltip("手-枪稳态握位重捕获延迟（s）：进入瞄准后等待该时长（需大于动画 crossfade，动画完成=手位稳定）再【一次性】重捕获腕-枪相对位姿锁定为稳态握位；等待期间手用进入瞬间捕获值贴枪（连续、不脱手、不穿模）")]
    public float aimHandCaptureTime = 0.5f;

    [Tooltip("稳态握位过渡时长（s）：重捕获后从进入瞬间握位平滑过渡到稳态握位的时间（手在枪上滑动，无跳变）")]
    public float aimHandRelockBlendTime = 0.15f;

    [Header("手部 IK 锚点 · 步枪（标定值，勿随意改）")]
    [Tooltip("左手 IK 锚点作为【武器子物体】的 local 位置——标定值（来源：原装配复合值 AK74 相对 handle (0,0.065,-0.0906) + 护木锚点相对 AK74 (0.089,-0.016,-0.158)，AK74 无旋转直接相加；已在场景标定生效，勿随意改；调整时按“场景摆枪→读该对象相对枪根 local”重标")] public Vector3 gunLeftHandAnchorLocalPosition = new Vector3(0.089f, 0.049f, -0.2486f);
    [Tooltip("左手 IK 锚点作为【武器子物体】的 local 旋转（Euler）——标定值（来源：护木锚点相对 AK74 的旋转 (63.164,-10.148,112.995)；场景标定生效，勿随意改")] public Vector3 gunLeftHandAnchorLocalEuler = new Vector3(63.164f, -10.148f, 112.995f);

    [Tooltip("右手 IK 锚点作为【Chest 骨骼子物体】的 local 位置——标定值（2026-09 重新标定：右手 TwoBoneIK target 相对 Chest 的局部坐标；target 在装配中是 Chest 子级（与原动画曲线同空间），标定生效，勿随意改")] public Vector3 chestRightHandAnchorLocalPosition = new Vector3(0.18f, -0.117f, 0.072f);
    [Tooltip("右手 IK 锚点作为【Chest 骨骼子物体】的 local 旋转（Euler）——标定值（2026-09 重新标定，与上方位置同批；标定生效，勿随意改")] public Vector3 chestRightHandAnchorLocalEuler = new Vector3(-230.47f, 185.38f, 261.551f);

    [Header("手部 IK 锚点 · 手枪（复刻步枪体系；标定值空，编辑器标定后勿随意改）")]
    [Tooltip("左手 IK 锚点作为【手枪子物体】的 local 位置——复刻步枪 gunLeftHandAnchorLocalPosition 体系；当前为占位 0，待编辑器标定（标定流程：场景摆枪→读该对象相对手枪根 local）")] public Vector3 pistolLeftHandAnchorLocalPosition = Vector3.zero;
    [Tooltip("左手 IK 锚点作为【手枪子物体】的 local 旋转（Euler）——复刻步枪体系；当前为占位 0，待编辑器标定")] public Vector3 pistolLeftHandAnchorLocalEuler = Vector3.zero;
    [Tooltip("右手 TwoBoneIK target 作为【Chest 骨骼子物体】的 local 位置——复刻步枪 chestRightHandAnchorLocalPosition 体系；当前为占位 0，待编辑器标定")] public Vector3 pistolRightHandAnchorLocalPosition = Vector3.zero;
    [Tooltip("右手 TwoBoneIK target 作为【Chest 骨骼子物体】的 local 旋转（Euler）——复刻步枪体系；当前为占位 0，待编辑器标定")] public Vector3 pistolRightHandAnchorLocalEuler = Vector3.zero;

    [Header("手部 IK 锚点 · 手雷 · 右手（复刻手枪体系；手雷以右手动作为主，左手不写入、由动画驱动）")]
    [Tooltip("右手 TwoBoneIK target 作为【Chest 骨骼子物体】的 local 位置——复刻步枪 chestRightHandAnchorLocalPosition 体系；当前为占位 0，待编辑器标定（全 0 未标定时 Grenade 状态类跳过写入并告警）")] public Vector3 grenadeRightHandAnchorLocalPosition = Vector3.zero;
    [Tooltip("右手 TwoBoneIK target 作为【Chest 骨骼子物体】的 local 旋转（Euler）——复刻步枪体系；当前为占位 0，待编辑器标定（全 0 未标定时 Grenade 状态类跳过写入并告警）")] public Vector3 grenadeRightHandAnchorLocalEuler = Vector3.zero;

    [Header("调试 · 右手 IK 写入日志")]
    [Tooltip("右手 IK 逐帧调试日志：常态帧打印「Write（标定写入）」与「FrameEnd（帧末实际值）」，帧末 ≠ 写入即 Animator 写回覆盖；拔/收枪切换帧打印「Switch」说明由脚本按动画进度接管（帧末 ≠ Write 属预期）（调试完关闭）")]
    public bool rightHandIkFrameDebugLog = true;

    [Header("武器切换 IK 轨迹 · 中段位姿（步枪 grab/put，clip 的 target 曲线由脚本接管）")]
    [Tooltip("拔/收枪动画中段（握枪/松手瞬间）target 的局部位姿——原 grab/put ik clip 的中间关键帧值 (0.1499, 0.3577, -0.1358)，勿随意改；重新标定参照 clip 原曲线")]
    public Vector3 rifleSwitchIkMidLocalPosition = new Vector3(0.1499f, 0.3577f, -0.1358f);

    [Tooltip("拔/收枪动画中段 target 的局部旋转（Euler）——原 grab/put ik clip 的中间关键帧值 (-50, 185.38, 261.551)，勿随意改")]
    public Vector3 rifleSwitchIkMidLocalEuler = new Vector3(-50f, 185.38f, 261.551f);

    [Header("武器切换 IK 轨迹 · grab 时间窗")]
    [Tooltip("grab rifle ik（1.1833s）位置轨迹归一化时间窗：开始离位 / 到达中段 / 回到标定位（由 clip 位置关键帧 0.15s / 0.4667s / 0.85s ÷ 时长换算）")]
    public SwitchIkWindow grabSwitchPositionWindow = new SwitchIkWindow(0.1268f, 0.3944f, 0.7183f);

    [Tooltip("grab rifle ik 旋转轨迹归一化时间窗：由 clip 欧拉关键帧 0s / 0.4667s / 1.1833s ÷ 时长换算（旋转自 0 即开始离位、到结尾才回位）")]
    public SwitchIkWindow grabSwitchRotationWindow = new SwitchIkWindow(0f, 0.3944f, 1f);

    [Header("武器切换 IK 轨迹 · put 时间窗")]
    [Tooltip("put rifle ik（1.8167s）位置轨迹归一化时间窗：由 clip 位置关键帧 0.35s / 0.8333s / 1.3334s ÷ 时长换算")]
    public SwitchIkWindow putSwitchPositionWindow = new SwitchIkWindow(0.1927f, 0.4587f, 0.734f);

    [Tooltip("put rifle ik 旋转轨迹归一化时间窗：由 clip 欧拉关键帧 0s / 0.8333s / 1.8s ÷ 时长换算（旋转自 0 即开始离位、接近结尾才回位）")]
    public SwitchIkWindow putSwitchRotationWindow = new SwitchIkWindow(0f, 0.4587f, 0.9908f);

    [Header("攀爬 · 速度与进入判定")]
    [Tooltip("攀爬移动速度（m/s）：输入映射到墙面切平面后的移动速度")]
    public float climbSpeed = 2f;

    [Tooltip("进入攀爬的朝向角（°）：水平速度方向与墙面法线反方向的最大夹角（速度朝墙才允许进攀爬）")]
    public float climbApproachAngle = 60f;

    [Tooltip("进入攀爬的最小水平速度（m/s）：低于该值视为无朝向墙面的移动")]
    public float climbApproachMinSpeed = 1f;

    [Header("攀爬 · 贴墙与退出判定")]
    [Tooltip("攀爬时角色根部到墙面的目标距离（让手够得着墙，配合手部 IK 使用）")]
    public float climbWallHugDistance = 0.3f;

    [Tooltip("贴墙收敛速度（每秒收敛比例）")]
    public float climbWallHugSpeed = 8f;

    [Tooltip("墙面连续探测失败多少帧后退出攀爬（防边界抖动）")]
    public int climbExitMissFrames = 3;

    [Tooltip("攀爬中墙面复测的“缺失射线比例”退出阈值：墙面网格全部射线发射统计，缺失比例 ≥ 该值（默认 0.5 = 缺失一半）且连续 climbExitMissFrames 帧才视为脱墙退出——容忍头顶射线越过墙顶，避免与登顶检测冲突导致无法登顶")]
    public float climbWallExitMissRatio = 0.5f;

    [Tooltip("向下爬至可着陆的自动退出帧数：持续按“下”（S）且 GroundProbe 去抖确认着地（ctx.IsGrounded）连续达到该帧数后，自动退出攀爬回 Normal 地面姿态（0 或负值 = 关闭该自动退出）")]
    public int climbDownExitFrames = 3;

    [Header("攀爬 · 手脚步进节奏（双手双脚共用）")]
    [Tooltip("姿态切换时间（s）：双手双脚共用的姿态切换平滑时长（上下换手/换脚、收开互换、横向挪脚等）")]
    public float climbSwitchTime = 0.4f;

    [Tooltip("步进交替间隔（s）：持续同方向攀爬时双手双脚共用的姿态交替节奏间隔")]
    public float climbStepInterval = 0.6f;

    [Header("攀爬 · 头部与身体 IK（Multi-Aim：看向移动方向，无输入保持上一角度）")]
    [Tooltip("攀爬时头部最大偏转角（°）：由“看墙面”正向向“移动方向”（墙上上下/左右）转动，方向跟随移动方向、偏转量以该角度为上限；0 = 始终看墙。建议 20~60，Play 中实时标定")]
    public float climbHeadLookAngle = 45f;

    [Tooltip("攀爬时身体（Chest）最大偏转角（°）：由墙面正向向移动方向转动，上下左右都生效于目标方向；实际可见幅度取决于 body aim constraint 的约束轴（当前只开 Y 轴=左右，上下需另开 X 轴）")]
    public float climbBodyLookAngle = 12f;

    [Header("攀爬 · 脚部 IK（上下：与手同拍反相；左右：双脚同相开合，均停在基准线不上不下）")]
    [Tooltip("脚部基准线相对臀部骨骼的高度（沿墙面上轴；默认在臀部下方 0.25m，正值=更高、负值=更低）")]
    public float climbFootLineHeight = -0.25f;

    [Tooltip("脚部中线距离（m）：双脚相对身体中心竖线的横向距离（上下攀爬与进入时的基准横向间隔）")]
    public float climbFootCenterDist = 0.1f;

    [Tooltip("脚部交替幅度（m）：上下攀爬时双脚相对基准线的上下幅度（与手同拍反相：同一侧手在上则脚在下）")]
    public float climbFootAlternateLength = 0.13f;

    [Tooltip("脚部收拢偏移（m）：左右攀爬“收拢”相位时脚与中线的横向距离（并拢但留出该偏移，不要完全贴中线）")]
    public float climbFootClosedOffset = 0.06f;

    [Tooltip("脚部开合幅度（m）：左右攀爬时在收拢偏移基础上叠加的开合量——打开 = 收拢偏移 + 开合幅度、收拢 = 收拢偏移（与手同拍，双脚不上不下）")]
    public float climbFootOpenAmount = 0.06f;

    [Tooltip("脚踝离墙偏移（m）：脚部 TwoBoneIK target 定位的是脚踝，脚踝不是脚掌/脚尖——target 若精确贴在墙面上会陷入墙内。该值 = 脚 target 投影到墙面后、再沿墙面法线向角色方向（远离墙面）偏移的距离。正值 = 脚踝离墙更远、脚掌贴墙；方向相反改负值")]
    public float climbFootWallNormalOffset = 0f;

    [Tooltip("左脚 target 相对墙面基准旋转的附加欧拉角（°），语义同手部偏移；上下交替与横向开合的脚尖角度都由它微调（先补 ±90/180 让脚掌朝向正确，再微调）")]
    public Vector3 climbLeftFootRotationOffset = Vector3.zero;

    [Tooltip("右脚 target 相对墙面基准旋转的附加欧拉角（°），语义同手部偏移；上下交替与横向开合的脚尖角度都由它微调")]
    public Vector3 climbRightFootRotationOffset = Vector3.zero;

    [Header("攀爬 · 手部 IK（程序化：位置 = 头部水平线 + 中线对称姿态；旋转 = 墙面基准 + 左右手偏移）")]
    [Tooltip("水平线高度（m）：手部姿态的基准水平线相对头部骨骼的高度（沿墙面向上为正；该线每帧跟随头部）")]
    public float climbHandLineHeight = 0.1f;

    [Tooltip("腕骨离墙偏移（m）：TwoBoneIK target 定位的是腕骨，腕骨不是掌心——腕到掌存在厚度，target 若精确贴在墙面上，掌心/手会陷入墙内。该值 = 手部 IK 目标位置投影到墙面后、再沿墙面法线向角色方向（远离墙面）偏移的距离：让腕骨退到掌心后方、掌心正好贴墙。正值 = 目标离墙更远；若发现方向相反改负值。建议从 0.05 起步在 Play Mode 里实时标定")]
    public float climbHandWallNormalOffset = 0f;

    [Tooltip("中线距离（m）：左右手相对中线（身体在墙面上投影的中心竖线）的横向距离；上下攀爬时双手持该距离不动")]
    public float climbHandCenterDist = 0.25f;

    [Tooltip("交替长度（m）：上下攀爬时双手交替幅度——手在水平线上方/下方各距水平线的距离（两手一致、上下对称）")]
    public float climbHandAlternateLength = 0.18f;

    [Tooltip("放置距离（m）：左右攀爬时上下放置距离——对应移动方向侧的手在水平线下方、相反侧手在上方，双方距水平线的距离（一致）")]
    public float climbHandPlaceDist = 0.15f;

    [Tooltip("开合幅度（m）：左右攀爬时双手横向收拢/打开的摆动幅度：收拢 = 中线距离 − 开合幅度，打开 = 中线距离 + 开合幅度（双手幅度一致）")]
    public float climbHandOpenAmount = 0.12f;

    [Tooltip("是否每帧写入攀爬手部 target 的 rotation。关 = 只写位置、保持 target 进入攀爬前的旋转（与动画手部姿态对照调试用；正常攀爬请保持开）")]
    public bool climbHandWriteRotation = true;

    [Tooltip("左手 target 相对「墙面基准旋转」的附加欧拉角（°）。墙面基准旋转 = 角色贴墙朝向：target 的 +Z 指向墙面（角色面墙前方）、+Y 沿墙面上、+X 沿墙面横向；偏移按 Unity 欧拉顺序叠加在基准系上，x/y/z 直觉上分别对应 横向倾侧 / 指尖俯仰 / 掌心翻转。全 0 时 target 自身轴即墙面基准轴；若模型手骨轴约定与基准不一致，先补 ±90/180 的初值再微调（Scene 视图以 target 三色轴为参照）")]
    public Vector3 climbLeftHandRotationOffset = Vector3.zero;

    [Tooltip("右手 target 相对「墙面基准旋转」的附加欧拉角（°），语义同左手参数。左右手相互独立（通常镜像：调准一侧后另一侧可先试对应分量反号）；Scene 视图以 target 三色轴为参照")]
    public Vector3 climbRightHandRotationOffset = Vector3.zero;

    [Header("登顶 · 到顶探测（从较高手探测是否越过墙顶）")]
    [Tooltip("开启攀爬自动到顶探测：从较高手的手腕上方射向墙面，连续 topOutProbeMissFrames 帧射不到墙（已越过墙顶）即请求进入登顶动画；关闭后需另走手动触发（当前无其他入口）")]
    public bool topOutAutoTrigger = true;

    [Tooltip("探测射线起点相对手部 IK target（手掌/腕目标）的向上偏移（m）：射线从“手掌上方该高度”射出，检测该高度是否仍有墙面（仍命中=还可继续爬，未命中=已越过墙顶可登顶）")]
    public float topOutProbeUpOffset = 0.15f;

    [Tooltip("探测射线起点相对手部 IK target 的离墙偏差（m，正值 = 沿墙面法线向角色方向拉开起点）：IK target 通常已贴墙，起点若不外拉就可能处于墙碰撞体内部——Unity Raycast 从碰撞体内部发射不会命中该碰撞体，导致正常爬墙也被误判“到顶”。该值保证起点在墙外，射线从墙外射向墙面")]
    public float topOutProbePalmOffset = 0.05f;

    [Tooltip("探测射线长度（m）：从起点沿墙面方向的最大距离；需大于“起点到墙面”的间隙，也决定“越过墙顶后仍把墙后物体误判为墙”的范围")]
    public float topOutProbeRayLength = 0.3f;

    [Tooltip("探测连续未命中的帧数（去抖）：射线连续该帧数未射到墙面才视为到顶；1 = 单帧即触发")]
    public int topOutProbeMissFrames = 3;

    [Header("登顶 · 抓取点扫描（垂直向下扫墙顶缘）")]
    [Tooltip("开启后登顶抓取点 = 进入登顶瞬间“垂直向下每 topOutGrabScanStep 米扫描”命中的墙顶面位置（不再用 IK target 快照，IK target 贴墙面而不是墙顶缘，抓取点偏下）；关闭则回退旧逻辑（IK target 位置快照）")]
    public bool topOutGrabUseLedgeScan = true;

    [Tooltip("垂直向下扫描的采样间隔（m）：用户约定 1cm = 0.01")]
    public float topOutGrabScanStep = 0.01f;

    [Tooltip("扫描起点相对被抓取手骨的向上高度（m）：从该高度开始逐厘米向下扫；需高于墙顶边缘，让第一次命中出现在墙顶面上")]
    public float topOutGrabScanStartAbove = 0.5f;

    [Tooltip("垂直向下扫描的最大采样步数（每步 topOutGrabScanStep 米）：默认 120 步 = 1.2m 向下覆盖")]
    public int topOutGrabScanSteps = 120;

    [Tooltip("扫描垂线相对手骨沿角色前向（朝墙内）的偏移（m）：垂线必须落在墙顶面的正上方才能命中顶面——从墙面外侧垂直向下永远打不到竖直墙面；该值需大于“手掌到墙前表面”的间隙、且小于墙体厚度，否则会越过墙顶打到墙后地面")]
    public float topOutGrabScanForward = 0.15f;

    [Tooltip("抓取点相对墙顶命中点的上抬（m）：MatchTarget 对齐的是腕骨，抓握时腕骨高于顶面约一个掌心厚度；0 = 腕骨正好落在顶面（手会半穿入顶面）")]
    public float topOutGrabPointUpOffset = 0.05f;

    [Tooltip("扫描命中点高度校验上限（m）：命中点高于被抓取手骨超过该值即视为打到了错误表面（如更高处的其它平台），改用旧逻辑兜底；避免误抓")]
    public float topOutGrabMaxLedgeHeight = 0.5f;

    [Header("登顶 · MatchTarget 落位与退出")]
    [Tooltip("登顶 MatchTarget 的开始时间（normalizedTime，0~1）：匹配混合从该进度开始，到 topOutMatchEndNormalizedTime 完成落位；默认 0 = 动画开播即开始混合（若发现 crossfade 帧下达异常可调到 0.06~0.07 避开过渡）")]
    public float topOutMatchStartNormalizedTime = 0f;

    [Tooltip("登顶 MatchTarget 的落位时间（normalizedTime，0~1）：动画播放到该进度时被抓手精确匹配到目标点；默认 0.1 = 动画 10% 处落位（匹配到点后自然结束，不再强制锁手）")]
    public float topOutMatchEndNormalizedTime = 0.1f;

    [Tooltip("登顶 MatchTarget 的位置匹配权重（X/Y/Z）：1 = 该轴向完全把被抓手拉向进入时快照位，0 = 该轴向仍由动画根运动控制")]
    public Vector3 topOutMatchPositionWeight = Vector3.one;

    [Tooltip("MatchTarget 目标点的可调位置偏移（m，在墙顶扫描抓取点基础上叠加，Play Mode 实时调参）：X = 角色右轴（沿墙横向），Y = 世界向上，Z = 角色前向（指向墙面）；例如手看起来偏低就加 Y，偏前/偏后调 Z")]
    public Vector3 topOutMatchPositionOffset = Vector3.zero;

    [Tooltip("登顶 MatchTarget 的旋转匹配权重（0~1）：1 = 被抓手的旋转也对齐进入时快照（攀爬 IK 手型≈贴墙朝前，与悬挂抓缘手型常差约 90°，默认 0 让动画自己转手）；若发现手型/穿模问题再按需调高并配合 IK 优化")]
    public float topOutMatchRotationWeight = 0f;

    [Tooltip("登顶动画播到该 normalizedTime 即退出回 Unarmed Normal Ground（默认 0.9：提前离开末段站稳收尾，把最后姿态过渡交给地面状态/动画 crossfade，避免播满后在地面状态上短暂冻结）")]
    public float topOutExitNormalizedTime = 0.9f;

    [Tooltip("登顶 MatchTarget 调试日志：下达时打印目标/窗口与 isMatchingTarget（排查“匹配是否被引擎接受、是否窗口太短”）")]
    public bool topOutDebugLog = false;

    /// <summary>起跳初速度：由跳跃高度与重力反推 v = √(2·|g|·h)。</summary>
    public float JumpSpeed => Mathf.Sqrt(2f * Mathf.Abs(gravity) * jumpHeight);

    /// <summary>
    /// 地面档位目标速度（按手持查询）：目标 = 该手持行走/奔跑档位 × 输入模长。
    /// 常态地面与武器切换速度插值共用；Handing 层叶子不再各自重算目标公式。
    /// </summary>
    public float GroundTargetSpeed(float inputMagnitude, bool run, PlayerHanding handing)
    {
        switch (handing)
        {
            case PlayerHanding.Rifle:   return (run ? rifleRunSpeed   : rifleWalkSpeed)   * inputMagnitude;
            case PlayerHanding.Pistol:  return (run ? pistolRunSpeed  : pistolWalkSpeed)  * inputMagnitude;
            case PlayerHanding.Grenade: return (run ? grenadeRunSpeed : grenadeWalkSpeed) * inputMagnitude;
            case PlayerHanding.Unarmed:
            default:                    return (run ? unarmedRunSpeed : unarmedWalkSpeed)  * inputMagnitude;
        }
    }

    /// <summary>
    /// 地面档位速度插值（按手持查询档位）：常态 Normal×Ground 状态用它向 GroundTargetSpeed 恒加速逼近。
    /// </summary>
    public float MoveGroundSpeed(float current, float inputMagnitude, bool run, float deltaTime, PlayerHanding handing)
        => Mathf.MoveTowards(current, GroundTargetSpeed(inputMagnitude, run, handing), moveAcceleration * deltaTime);

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
