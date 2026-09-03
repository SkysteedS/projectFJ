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
    [Tooltip("瞄准射线距离（m）：瞄准状态从主相机中心向前的射线最大距离；未命中时瞄准目标点设为【该距离处的远点】")]
    public float aimRayDistance = 100f;

    [Tooltip("瞄准射线层掩码：射线只检测这些层。注意排除玩家自身层，否则会命中角色自己的碰撞体（默认排除 Unity 内建 Player 层 6——当前角色所在层）")]
    public LayerMask aimRayMask = ~(1 << 6);

    [Tooltip("瞄准点插值率（每秒指数收敛系数，越大越快；0 = 不平滑）：每帧瞄准点 = 上一帧瞄准点与射线返回点之间的插值结果。吸收命中↔未命中远点/命中表面切换时的落点跳变，避免头与枪口在固定角度扭转")]
    public float aimPointInterpRate = 15f;

    [Tooltip("瞄准模式：关 = 按住右键瞄准（松开退出）；开 = 右键切换瞄准（按一次进入、再按一次退出）")]
    public bool aimToggleMode = false;

    [Tooltip("瞄准程序化 IK：进入瞄准时枪根从持枪位平滑过渡到 aimAxisOffset 的时长（s）。期间枪口 aim 指向角色身前而非相机瞄准点（角色仍在转身对齐）")]
    public float aimRigTransitionTime = 0.35f;

    [Tooltip("非瞄准时水平面左右轴速度（m/s）：角色朝向移动方向，全速计入前后轴，左右轴固定用此值（0 = 无侧移分量）")]
    public float nonAimLateralSpeed = 0f;

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
