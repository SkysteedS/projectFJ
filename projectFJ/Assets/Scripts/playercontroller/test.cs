// ══════════════════════════════════════════════════════════════════════════
// 可行性原型（保留，不再维护）：本文件已被正式角色控制器取代
// （PlayerControllerScript + PlayerStateMachine + 各具体状态类，见 player-controller-design.md），
// 场景/预制/其余脚本均无运行时引用；仅作为行为设计对照与回归基准保留。
// 正式代码与原型行为出现分歧时以设计文档与正式实现为准（test.cs 只可读、不改）。
// ══════════════════════════════════════════════════════════════════════════
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Animations.Rigging;

#region 玩家姿态
/// <summary>
/// 玩家姿态：着陆 / 滞空。
/// </summary>
public enum testPlayerStance
{
    Grounded = 0,   // 着陆
    Airborne = 1,    // 滞空
    Climbing = 2
}

public enum testPlayerHanding
{
    Unweapon = 0,
    Rifle = 1,
    Sword = 2
}
#endregion

public class playercontroller : MonoBehaviour
{
    #region Animator 参数 Hash
    static readonly int AnimParamForwardBackSpeed = Animator.StringToHash("水平前后速度");
    static readonly int AnimParamLeftRightSpeed = Animator.StringToHash("水平左右速度");
    static readonly int AnimParamHanding = Animator.StringToHash("玩家手持");
    static readonly int AnimParamAiming = Animator.StringToHash("瞄准中");
    static readonly int AnimParamRightHandIKWeight = Animator.StringToHash("right hand ik weight");
    static readonly int AnimParamLeftHandIKWeight = Animator.StringToHash("left hand ik weight");
    static readonly int AnimParamStance = Animator.StringToHash("玩家姿态");
    static readonly int AnimParamVerticalVelocity = Animator.StringToHash("垂直速度");
    #endregion

    #region 组件引用
    Animator animator;
    CharacterController controller;
    Transform playerTransform;
    Camera mainCamera;

    public GameObject rifleOnHand;
    public GameObject rifleOnBack;
    public TwoBoneIKConstraint rightHandConstraint;
    public TwoBoneIKConstraint leftHandConstraint;
    #endregion

    #region 输入参数
    [Header("输入")]
    [SerializeField] TestPlayerInputState input = new TestPlayerInputState();
    #endregion

    #region 运动状态
    [Header("运动")]
    [SerializeField] MotionState motion = new MotionState();
    #endregion

    #region 攀爬检测
    [Header("攀爬")]
    [SerializeField] TestWallProbe climbProbe = new TestWallProbe();
    Vector3 climbWallNormal;    // 最近一次检测到的墙面法线（朝向角色）
    Vector3 climbWallPoint;     // 最近一次检测到的墙面锚点（命中点平均）
    int climbMissFrames;        // 墙面检测连续失败帧数
    public int climbExitMissFrames = 3; // 连续失败多少帧后退出攀爬（防边界抖动）
    public float climbWallHugDistance = 0.3f; // 攀爬时角色根部到墙面的目标距离（让手够得着墙）
    public float climbWallHugSpeed = 8f;      // 贴墙收敛速度

    /// <summary>
    /// 攀爬姿态切换：检测到可攀爬墙面且面向合适才进入；再按一次退出（测试用）。
    /// </summary>
    void UpdateClimbSwitch()
    {
        if (!input.ConsumeClimb()) return;

        // 测试用：攀爬中再按一次回到着陆
        if (motion.state == testPlayerStance.Climbing)
        {
            motion.state = testPlayerStance.Grounded;
            ExitClimbHandIK();
            return;
        }

        // 只有检测到可攀爬、夹角合适的连续墙面才切换姿态
        bool detected = climbProbe.Evaluate(playerTransform, playerTransform.forward, out climbWallNormal, out climbWallPoint);
        if (detected)
        {
            motion.state = testPlayerStance.Climbing;
            motion.verticalVelocity = 0f;
            EnterClimbHandIK();
        }
    }

    /// <summary>
    /// 攀爬中每帧重新检测墙面：平面随检测更新；检测失败（到顶/出界/墙消失）连续若干帧后回到默认姿态。
    /// </summary>
    void UpdateClimbPlane()
    {
        if (climbProbe.Evaluate(playerTransform, playerTransform.forward, out climbWallNormal, out climbWallPoint))
        {
            climbMissFrames = 0;
        }
        else
        {
            climbMissFrames++;
            if (climbMissFrames >= climbExitMissFrames)
            {
                motion.state = testPlayerStance.Grounded;
                ExitClimbHandIK();
                climbMissFrames = 0;
            }
        }
    }

    /// <summary>选中角色时绘制攀爬探测 Gizmos。</summary>
    void OnDrawGizmosSelected()
    {
        if (climbProbe != null)
        {
            climbProbe.DrawGizmos(transform, transform.forward);
        }
    }
    #endregion

    #region 攀爬手部 IK
    Transform climbHead;                  // 头部骨骼
    Transform climbLeftHand;              // 左手骨骼（用于调试对比 IK 是否生效）
    Transform climbRightHand;             // 右手骨骼
    int handIkLayerIndex = -1;            // "hand ik" 层索引（攀爬时停用，避免动画写回目标位置）
    float originalHandIkLayerWeight;      // 进入攀爬前的 hand ik 层权重
    Coroutine climbIkLogRoutine;          // IK 调试日志协程（节流输出）
    Vector3 climbHandLOffset;             // 当前（平滑后）左手相对头部偏移
    Vector3 climbHandROffset;             // 当前（平滑后）右手相对头部偏移
    Vector3 climbHandLOffsetTarget;       // 姿态目标偏移（相位/步进变化时更新）
    Vector3 climbHandROffsetTarget;       // 姿态目标偏移
    bool climbHandPhase;                  // 姿态相位：交替（上下时高手左右切换 / 贴近与打开切换）
    int lastClimbCategory;                // 上次输入方向类别（0 静止 / ±1 左右 / ±2 上下）
    float climbHandStepTimer;             // 手部步进交替计时

    [Header("攀爬手部IK")]
    public bool climbIkDebugLog = true;   // IK 调试日志（按 climbIkLogInterval 间隔输出）
    public float climbIkLogInterval = 0.2f; // IK 调试日志间隔（秒）
    public string handIkLayerName = "hand ik target"; // 动画控制器中手部 IK 目标层名
    public float climbHandSwitchTime = 0.4f;   // 手部姿态切换时间（秒）
    public float climbHandStepInterval = 0.6f; // 持续移动时手部步进交替间隔（秒）
    public float climbHandHighOffset = 0.15f;  // 高手相对头部上偏移（略高于头部）
    public float climbHandLowOffset = 0.2f;    // 低手相对头部下偏移
    public float climbHandLateralClose = 0.18f; // 双手贴近头部时的横向偏移
    public float climbHandLateralOpen = 0.35f;  // 双手打开时的横向偏移
    public float climbHandMaxReach = 0.35f;    // 手部目标相对头部在平面内的最大偏移（保证臂展可达）

    void EnterClimbHandIK()
    {
        if (rightHandConstraint == null || leftHandConstraint == null) return;

        Transform leftTarget = leftHandConstraint.data.target;
        Transform rightTarget = rightHandConstraint.data.target;
        if (leftTarget == null || rightTarget == null)
        {
            Debug.LogWarning("[Climb] 手部 IK 目标为空，无法通过移动目标实现攀爬手部 IK");
            return;
        }

        // 停掉 hand ik 层：该层动画每帧会把目标写回步枪/掏枪位置，必须让位给攀爬 IK
        handIkLayerIndex = animator.GetLayerIndex(handIkLayerName);
        if (handIkLayerIndex >= 0)
        {
            originalHandIkLayerWeight = animator.GetLayerWeight(handIkLayerIndex);
            animator.SetLayerWeight(handIkLayerIndex, 0f);
        }
        if (climbIkDebugLog)
        {
            Debug.Log($"[ClimbIK] 进入攀爬：L目标={leftTarget.name}，R目标={rightTarget.name}，hand ik 层[{handIkLayerName}]={handIkLayerIndex}（原权重={originalHandIkLayerWeight:F2}）");
        }

        // 手部初始位置放在头部两侧，避免从远处飞过来
        Vector3 headPos = climbHead != null ? climbHead.position : playerTransform.position + Vector3.up * 1.6f;
        Vector3 lateral = playerTransform.right * climbHandLateralClose;
        climbHandLOffset = -lateral;
        climbHandROffset = lateral;
        climbHandLOffsetTarget = climbHandLOffset;
        climbHandROffsetTarget = climbHandROffset;
        leftTarget.position = headPos + climbHandLOffset;
        rightTarget.position = headPos + climbHandROffset;
        climbHandPhase = false;
        lastClimbCategory = 0;
        climbHandStepTimer = 0f;

        if (climbIkDebugLog)
        {
            climbIkLogRoutine = StartCoroutine(ClimbIkLogLoop());
        }
    }

    void ExitClimbHandIK()
    {
        // 恢复 hand ik 层：动画会重新把目标写回步枪/掏枪位置，无需手动还原目标位置
        if (handIkLayerIndex >= 0 && animator != null)
        {
            animator.SetLayerWeight(handIkLayerIndex, originalHandIkLayerWeight);
        }
        if (climbIkDebugLog)
        {
            Debug.Log($"[ClimbIK] 退出攀爬：恢复 hand ik 层权重 {originalHandIkLayerWeight:F2}");
        }
        if (climbIkLogRoutine != null)
        {
            StopCoroutine(climbIkLogRoutine);
            climbIkLogRoutine = null;
        }
    }

    void UpdateClimbHandIK()
    {
        if (climbHead == null || rightHandConstraint == null || leftHandConstraint == null) return;

        Transform leftTarget = leftHandConstraint.data.target;
        Transform rightTarget = rightHandConstraint.data.target;
        if (leftTarget == null || rightTarget == null) return;

        int category = ClimbDirCategory(input.move);
        if (category == 0)
        {
            // 无输入：保持当前手部姿态
            climbHandStepTimer = 0f;
            lastClimbCategory = 0;
        }
        else if (category != lastClimbCategory)
        {
            // 方向类别变化：立即切换姿态
            climbHandPhase = !climbHandPhase; // 每次切换方向类别时交替姿态
            lastClimbCategory = category;
            ComputeClimbHandPose(category);
            climbHandStepTimer = 0f;
        }
        else
        {
            // 持续同方向移动：按间隔步进交替（两种姿态间循环）
            climbHandStepTimer += Time.deltaTime;
            if (climbHandStepTimer >= climbHandStepInterval)
            {
                climbHandPhase = !climbHandPhase;
                climbHandStepTimer = 0f;
                ComputeClimbHandPose(category);
            }
        }

        // 姿态偏移在约 climbHandSwitchTime 秒内平滑过渡；目标位置每帧按当前头部合成（手跟着身体走）
        float t = climbHandSwitchTime > 0.01f ? Time.deltaTime / climbHandSwitchTime : 1f;
        climbHandLOffset = Vector3.Lerp(climbHandLOffset, climbHandLOffsetTarget, t);
        climbHandROffset = Vector3.Lerp(climbHandROffset, climbHandROffsetTarget, t);
        Vector3 headPos = climbHead.position;
        // 每帧把姿态位置投影到当前墙面平面（通过 climbWallPoint），让手真正贴在墙面上
        leftTarget.position = ProjectToWallPlane(headPos + climbHandLOffset, climbWallPoint, climbWallNormal);
        rightTarget.position = ProjectToWallPlane(headPos + climbHandROffset, climbWallPoint, climbWallNormal);
    }

    /// <summary>按 climbIkLogInterval 间隔循环输出 IK 关键状态。</summary>
    IEnumerator ClimbIkLogLoop()
    {
        while (true)
        {
            LogClimbIK();
            yield return new WaitForSeconds(climbIkLogInterval);
        }
    }

    /// <summary>场景视图常驻绘制：青色线框 = IK 目标位置，黄色线框 = 手骨实际位置，连线对比。</summary>
    void OnDrawGizmos()
    {
        DrawClimbHandIKGizmos();
    }

    void DrawClimbHandIKGizmos()
    {
        // 运行时数据（手骨在 Start 缓存），编辑模式下直接跳过
        if (leftHandConstraint == null || rightHandConstraint == null) return;
        if (climbLeftHand == null || climbRightHand == null) return;

        Transform leftTarget = leftHandConstraint.data.target;
        Transform rightTarget = rightHandConstraint.data.target;

        // IK 目标位置（青色线框）
        Gizmos.color = Color.cyan;
        if (leftTarget != null) Gizmos.DrawWireSphere(leftTarget.position, 0.09f);
        if (rightTarget != null) Gizmos.DrawWireSphere(rightTarget.position, 0.09f);

        // 手骨实际位置（黄色线框）
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(climbLeftHand.position, 0.06f);
        Gizmos.DrawWireSphere(climbRightHand.position, 0.06f);

        // 目标与手骨连线：线越长说明 IK 越没拉到位
        Gizmos.color = Color.red;
        if (leftTarget != null) Gizmos.DrawLine(leftTarget.position, climbLeftHand.position);
        if (rightTarget != null) Gizmos.DrawLine(rightTarget.position, climbRightHand.position);
    }

    /// <summary>输出 IK 关键状态：层权重、目标、期望/当前位置、手骨实际位置。</summary>
    void LogClimbIK()
    {
        if (!climbIkDebugLog) return;

        Transform leftTarget = leftHandConstraint != null ? leftHandConstraint.data.target : null;
        Transform rightTarget = rightHandConstraint != null ? rightHandConstraint.data.target : null;
        float layerWeight = handIkLayerIndex >= 0 && animator != null ? animator.GetLayerWeight(handIkLayerIndex) : -1f;
        Vector3 headPos = climbHead != null ? climbHead.position : Vector3.zero;
        Vector3 handL = climbLeftHand != null ? climbLeftHand.position : Vector3.zero;
        Vector3 handR = climbRightHand != null ? climbRightHand.position : Vector3.zero;

        Debug.Log($"[ClimbIK] layer={layerWeight:F2} " +
                  $"Ltarget={(leftTarget != null ? leftTarget.name : "NULL")} Rtarget={(rightTarget != null ? rightTarget.name : "NULL")} " +
                  $"Lw={(leftHandConstraint != null ? leftHandConstraint.weight : -1f):F2} Rw={(rightHandConstraint != null ? rightHandConstraint.weight : -1f):F2} " +
                  $"head={headPos.ToString("F2")} " +
                  $"LoffT={climbHandLOffsetTarget.ToString("F2")} Loff={climbHandLOffset.ToString("F2")} Lhand={handL.ToString("F2")} " +
                  $"RoffT={climbHandROffsetTarget.ToString("F2")} Roff={climbHandROffset.ToString("F2")} Rhand={handR.ToString("F2")}");
    }

    /// <summary>输入方向类别：0 静止，1 右 / -1 左，2 上 / -2 下。</summary>
    int ClimbDirCategory(Vector2 dir)
    {
        if (dir.sqrMagnitude < 0.01f) return 0;
        return Mathf.Abs(dir.x) > Mathf.Abs(dir.y) ? (dir.x > 0 ? 1 : -1) : (dir.y > 0 ? 2 : -2);
    }

    /// <summary>计算双手期望位置：左右移动时高手在移动反侧，上下移动时高手左右交替。</summary>
    void ComputeClimbHandPose(int category)
    {
        if (climbHead == null) return;

        Vector3 headPos = climbHead.position;
        Vector3 up = playerTransform.up;
        Vector3 right = playerTransform.right;
        float lateral = climbHandPhase ? climbHandLateralClose : climbHandLateralOpen;

        Vector3 targetL;
        Vector3 targetR;
        if (category == 1 || category == -1)
        {
            // 向右：左手高、右手与头平；向左：右手高、左手与头平
            if (category == 1)
            {
                targetL = headPos + up * climbHandHighOffset - right * lateral;
                targetR = headPos + right * lateral;
            }
            else
            {
                targetL = headPos - right * lateral;
                targetR = headPos + up * climbHandHighOffset + right * lateral;
            }
        }
        else
        {
            // 上下：一只手在头上方、一只手在头下方；高手随相位左右交替
            float side = climbHandLateralClose;
            if (climbHandPhase)
            {
                targetL = headPos + up * climbHandHighOffset - right * side;
                targetR = headPos - up * climbHandLowOffset + right * side;
            }
            else
            {
                targetL = headPos - up * climbHandLowOffset - right * side;
                targetR = headPos + up * climbHandHighOffset + right * side;
            }
        }

        // 姿态只存相对头部的形状偏移（上/右分量），并钳制在最大臂展内；
        // 贴合墙面在每帧合成目标时按当前墙面平面投影
        climbHandLOffsetTarget = ClampHandOffset(targetL - headPos);
        climbHandROffsetTarget = ClampHandOffset(targetR - headPos);
    }

    /// <summary>把相对头部的偏移钳制在 climbHandMaxReach 内，保证手臂能够到目标。</summary>
    Vector3 ClampHandOffset(Vector3 offset)
    {
        if (offset.sqrMagnitude > climbHandMaxReach * climbHandMaxReach)
        {
            return offset.normalized * climbHandMaxReach;
        }
        return offset;
    }

    /// <summary>把位置投影到经过 planePoint、法线为 normal 的平面上。</summary>
    Vector3 ProjectToWallPlane(Vector3 pos, Vector3 planePoint, Vector3 normal)
    {
        if (normal.sqrMagnitude < 0.0001f) return pos;
        return pos - normal * Vector3.Dot(pos - planePoint, normal);
    }
    #endregion

    #region 生命周期
    void Start()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
        playerTransform = transform;
        mainCamera = Camera.main;

        // 确保 Animator 与脚本状态一致（默认非瞄准）
        animator.SetBool(AnimParamAiming, input.aim);
        animator.SetFloat(AnimParamStance, motion.stance);
        motion.isGrounded = controller.isGrounded;

        climbHead = animator.GetBoneTransform(HumanBodyBones.Head);
        climbLeftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        climbRightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
    }

    void Update()
    {
        Rotate();
        Move();
        if (motion.state == testPlayerStance.Climbing)
        {
            UpdateClimbHandIK();
        }
        SetTwoHandsIKWeight();
    }
    #endregion

    #region IK
    void SetTwoHandsIKWeight()
    {
        // 攀爬时手部 IK 权重固定为 1，其余状态由动画状态机参数驱动
        if (motion.state == testPlayerStance.Climbing)
        {
            rightHandConstraint.weight = 1f;
            leftHandConstraint.weight = 1f;
            return;
        }
        rightHandConstraint.weight = animator.GetFloat(AnimParamRightHandIKWeight);
        leftHandConstraint.weight = animator.GetFloat(AnimParamLeftHandIKWeight);
    }
    #endregion

    #region 输入回调
    public void GetPlayerRunInput(InputAction.CallbackContext ctx)
    {
        input.SetRun(ctx);
    }

    public void GetPlayerClimbInput(InputAction.CallbackContext ctx)
    {
        input.SetClimb(ctx);
    }

    public void GetPlayerMovement(InputAction.CallbackContext ctx)
    {
        input.SetMove(ctx);
    }

    public void GetPlayerAimInput(InputAction.CallbackContext ctx)
    {
        input.SetAim(ctx);

        // 空手不能瞄准；非着陆姿态（滞空/攀爬）也不能瞄准
        if ((input.handing == 0 || motion.state != testPlayerStance.Grounded) && input.aim)
        {
            input.aim = false;
        }
        animator.SetBool(AnimParamAiming, input.aim);
    }

    public void GetPlayerJumpInput(InputAction.CallbackContext ctx)
    {
        input.SetJump(ctx);
    }

    public void GetRifleInput(InputAction.CallbackContext ctx)
    {
        if (ctx.ReadValue<float>() != 0) return;

        input.ToggleHanding();

        // 收回武器（空手）时强制取消瞄准
        if (input.handing == 0)
        {
            input.aim = false;
        }
        animator.SetInteger(AnimParamHanding, input.handing);
        animator.SetBool(AnimParamAiming, input.aim);
    }
    #endregion

    #region 武器
    /// <summary>
    /// 切换步枪位置。
    /// </summary>
    /// <param name="Onback">参数为1时枪在背上，为0时枪在手上</param>
    public void PutGrabRifle(int Onback)
    {
        if (Onback == 0)
        {
            rifleOnBack.SetActive(false);
            rifleOnHand.SetActive(true);
        }
        else
        {
            rifleOnBack.SetActive(true);
            rifleOnHand.SetActive(false);
        }
    }
    #endregion

    #region 移动
    void Rotate()
    {
        // 攀爬：身体垂直于墙面法线（面向墙面），不按移动方向转向
        if (motion.state == testPlayerStance.Climbing)
        {
            RotateToWall(climbWallNormal);
            return;
        }

        // 瞄准时：越肩固定视角下摄像机前方与角色前方一致，移动由 2D 混合树表现，不旋转
        if (input.aim) return;

        if (input.move.magnitude <= 0f) return;

        // 以摄像机前方为基准，把 WASD 换算成世界方向的移动朝向
        GetCameraAxes(out Vector3 camForward, out Vector3 camRight);
        Vector3 moveDir = camForward * input.move.y + camRight * input.move.x;

        if (moveDir.sqrMagnitude <= 0.0001f) return;

        moveDir.Normalize();
        Quaternion targetRotation = Quaternion.LookRotation(moveDir, Vector3.up);
        playerTransform.rotation = Quaternion.RotateTowards(playerTransform.rotation, targetRotation, motion.rotateSpeed * Time.deltaTime);
    }

    /// <summary>朝向墙面：身体平面垂直于墙面法线，up 沿墙面切平面。</summary>
    void RotateToWall(Vector3 wallNormal)
    {
        if (wallNormal.sqrMagnitude < 0.0001f) return;

        Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, wallNormal).normalized;
        Quaternion target = Quaternion.LookRotation(-wallNormal, wallUp);
        playerTransform.rotation = Quaternion.RotateTowards(playerTransform.rotation, target, motion.rotateSpeed * Time.deltaTime);
    }

    void Move()
    {
        // 非瞄准：垂直速度 1D 混合树；瞄准：水平/垂直 2D 混合树
        motion.UpdateSpeed(input, Time.deltaTime);

        // 非瞄准时才能跳跃（姿态切换）；瞄准中不可以跳跃
        motion.UpdateVerticalVelocity(input, Time.deltaTime, !input.aim);
        UpdateClimbSwitch();
        if (motion.state == testPlayerStance.Climbing)
        {
            UpdateClimbPlane();
        }
        motion.UpdateStance(Time.deltaTime);

        // 攀爬中不允许瞄准（进入攀爬或爬行中按下瞄准都强制取消）
        if (motion.state == testPlayerStance.Climbing && input.aim)
        {
            input.aim = false;
            animator.SetBool(AnimParamAiming, false);
        }

        if (input.aim)
        {
            GetCameraAxes(out Vector3 camForward, out Vector3 camRight);
            motion.UpdateAimSpeed(input, camForward, camRight, Time.deltaTime);

            animator.SetFloat(AnimParamForwardBackSpeed, motion.aimVerticalSpeed);
            animator.SetFloat(AnimParamLeftRightSpeed, motion.aimHorizontalSpeed);
        }
        else
        {
            animator.SetFloat(AnimParamForwardBackSpeed, motion.currentSpeed);

            // 瞄准参数衰减到 0，避免重新瞄准时残留上一次的旧速度
            motion.DecayAimSpeed(Time.deltaTime);
        }

        // 同步姿态与垂直速度给 Animator
        animator.SetFloat(AnimParamStance, motion.stance);
        animator.SetFloat(AnimParamVerticalVelocity, motion.VerticalVelocityParam);
    }

    void GetCameraAxes(out Vector3 camForward, out Vector3 camRight)
    {
        if (mainCamera != null)
        {
            // 投影到水平面(XZ)，避免相机俯仰影响朝向
            camForward = Vector3.ProjectOnPlane(mainCamera.transform.forward, Vector3.up).normalized;
            camRight = Vector3.ProjectOnPlane(mainCamera.transform.right, Vector3.up).normalized;
        }
        else
        {
            // 缺主摄像机时回退到世界轴
            camForward = Vector3.forward;
            camRight = Vector3.right;
        }
    }

    public void OnAnimatorMove()
    {
        Vector3 delta = animator.deltaPosition;

        // 攀爬：脚本沿墙面切平面移动，无重力
        if (motion.state == testPlayerStance.Climbing)
        {
            delta = motion.ComputeClimbDelta(input, climbWallNormal, Time.deltaTime);

            // 贴墙：把角色沿法线方向拉向墙面，保持固定离墙距离，让手部目标落在手臂可及范围内
            if (climbWallNormal.sqrMagnitude > 0.0001f)
            {
                float currentDist = Vector3.Dot(playerTransform.position - climbWallPoint, climbWallNormal);
                float correction = climbWallHugDistance - currentDist;
                delta += climbWallNormal * correction * Mathf.Clamp01(climbWallHugSpeed * Time.deltaTime);
            }

            controller.Move(delta);
            motion.isGrounded = controller.isGrounded;
            return;
        }

        // 滞空时：跳跃动画没有水平根运动，水平位移由脚本按当前速度/方向接管
        if (motion.state == testPlayerStance.Airborne)
        {
            GetCameraAxes(out Vector3 camForward, out Vector3 camRight);
            Vector3 moveDir = camForward * input.move.y + camRight * input.move.x;
            if (moveDir.sqrMagnitude > 0.0001f)
            {
                moveDir.Normalize();
            }

            delta.x = moveDir.x * motion.currentSpeed * Time.deltaTime;
            delta.z = moveDir.z * motion.currentSpeed * Time.deltaTime;
        }

        // 垂直位移始终由手写重力接管
        delta.y = motion.verticalVelocity * Time.deltaTime;
        controller.Move(delta);
        motion.isGrounded = controller.isGrounded;

        // 滞空且正在下落时触地才视为落地（verticalVelocity <= 0 防止起跳瞬间 isGrounded 残留误判）
        if (motion.state == testPlayerStance.Airborne && motion.isGrounded && motion.verticalVelocity <= 0f)
        {
            motion.state = testPlayerStance.Grounded;
            motion.CaptureLandingFall();                             // 先捕获当前下落姿态，再重置垂直速度
            motion.verticalVelocity = -2f;
            input.CancelJump();                                    // 落地瞬间清除跳跃按下信号，避免立刻二次起跳
        }
    }
    #endregion
}

#region 输入状态
/// <summary>
/// 输入参数：只管"玩家按了什么"，不关心怎么用。
/// 注意：正式输入层已由 PlayerInputState.cs 承担，本类保留仅供 test.cs 使用（Test 前缀隔离）。
/// </summary>
[System.Serializable]
public class TestPlayerInputState
{
    public Vector2 move;    // WASD 输入
    public bool run;        // 是否跑步
    public bool aim;        // 是否瞄准
    public bool jumpPressed; // 单次按下信号：按下置位，判定后清除
    public bool climbPressed; // 单次按下信号：按下置位，判定后清除
    public int handing;     // 0 空手 / 1 手持武器

    public void SetMove(InputAction.CallbackContext ctx) => move = ctx.ReadValue<Vector2>();

    public void SetRun(InputAction.CallbackContext ctx) => run = ctx.ReadValue<float>() > 0f;

    public void SetJump(InputAction.CallbackContext ctx)
    {
        if (ctx.action.phase == InputActionPhase.Started)
        {
            jumpPressed = true;
        }
        else if (ctx.action.phase == InputActionPhase.Canceled)
        {
            // 松开后必须重新按下才能再次跳跃
            jumpPressed = false;
        }
    }

    /// <summary>读取并清除按下信号（每次按下只响应一次）。</summary>
    public bool ConsumeJump()
    {
        bool pressed = jumpPressed;
        jumpPressed = false;
        return pressed;
    }

    /// <summary>清除未消费的跳跃按下信号（落地时调用，防止立刻二次起跳）。</summary>
    public void CancelJump() => jumpPressed = false;

    public void SetClimb(InputAction.CallbackContext ctx)
    {
        if (ctx.action.phase == InputActionPhase.Started)
        {
            climbPressed = true;
        }
        else if (ctx.action.phase == InputActionPhase.Canceled)
        {
            // 松开后必须重新按下才能再次切换
            climbPressed = false;
        }
    }

    /// <summary>读取并清除按下信号（每次按下只响应一次）。</summary>
    public bool ConsumeClimb()
    {
        bool pressed = climbPressed;
        climbPressed = false;
        return pressed;
    }

    public void SetAim(InputAction.CallbackContext ctx)
    {
        if (ctx.action.phase == InputActionPhase.Started) aim = true;
        else if (ctx.action.phase == InputActionPhase.Canceled) aim = false;
    }

    public void ToggleHanding() => handing = handing != 1 ? 1 : 0;
}
#endregion

#region 运动状态
/// <summary>
/// 运动状态：由输入推导出的速度/朝向，包含平滑过渡逻辑。
/// </summary>
[System.Serializable]
public class MotionState
{
    public float walkSpeed = 1.5f;
    public float runSpeed = 3.5f;
    public float moveAcceleration = 12f;
    public float aimAcceleration = 12f;
    public float rotateSpeed = 1000f;
    public float gravity = -15f;    // 重力加速度（负值向下）
    public float jumpHeight = 0.8f; // 预设跳跃高度（米），起跳初速度由此反推
    public float climbSpeed = 2f;   // 攀爬移动速度（沿墙面切平面）
    public bool isGrounded;         // 物理着地，每帧由 CharacterController 更新
    public float fallBlendThreshold = -2.5f; // 死区下界：下落速度低于该值才进入滞空混合
    public float jumpBlendThreshold = 2f;    // 死区上界：上升速度高于该值才进入滞空混合

    public testPlayerStance state = testPlayerStance.Grounded;  // 逻辑姿态：着陆/滞空
    public float stance;                                // 姿态混合值 0~1，供 1D 混合树切换 Locomotion/Jump
    public float stanceAcceleration = 30f;              // 姿态切换速度（越大切换越快）
    public float currentSpeed;
    public float targetSpeed;
    public float aimVerticalSpeed;
    public float aimHorizontalSpeed;
    public float verticalVelocity;  // Y 轴速度（手写重力）
    float landingFallBlend;         // 落地时捕获的下落速度（原始 m/s），落地混合期间保持下落姿态

    /// <summary>起跳初速度：由跳跃高度与重力反推 v = √(2·|g|·h)。</summary>
    public float JumpSpeed => Mathf.Sqrt(2f * Mathf.Abs(gravity) * jumpHeight);

    /// <summary>
    /// 垂直速度参数（原始 m/s，不做归一化）。
    /// 落地后 stance 尚未回落到 0 时保持下落速度，避免 Jump 树跳到顶点/悬空 pose 混入落地过渡。
    /// </summary>
    public float VerticalVelocityParam
    {
        get
        {
            if (state == testPlayerStance.Grounded)
            {
                return stance > 0f ? landingFallBlend : 0f;
            }
            if (state == testPlayerStance.Climbing)
            {
                // 攀爬姿态不使用垂直速度参数
                return 0f;
            }
            return verticalVelocity;
        }
    }

    /// <summary>落地瞬间捕获当前下落速度，供落地姿态混合期间使用。</summary>
    public void CaptureLandingFall()
    {
        landingFallBlend = verticalVelocity;
    }

    /// <summary>更新总速度（走/跑 + 输入幅度），恒定加速度平滑。</summary>
    public void UpdateSpeed(TestPlayerInputState input, float deltaTime)
    {
        targetSpeed = (input.run ? runSpeed : walkSpeed) * input.move.magnitude;
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, moveAcceleration * deltaTime);
    }

    /// <summary>
    /// 攀爬位移：输入映射到墙面切平面（W = 角色上方，S = 下方，D = 右方，A = 左方），返回本帧位移。
    /// </summary>
    public Vector3 ComputeClimbDelta(TestPlayerInputState input, Vector3 wallNormal, float deltaTime)
    {
        if (wallNormal.sqrMagnitude < 0.0001f) return Vector3.zero;

        // 墙面切平面内的"上/右"：世界 up 投影到切平面，right 由 forward = -normal 推出
        Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, wallNormal).normalized;
        Vector3 forward = -wallNormal;
        Vector3 wallRight = Vector3.Cross(wallUp, forward).normalized;

        Vector3 moveDir = wallUp * input.move.y + wallRight * input.move.x;
        return moveDir * climbSpeed * deltaTime;
    }

    /// <summary>瞄准时：把输入投影到摄像机前/右轴，得到水平/垂直速度并平滑。</summary>
    public void UpdateAimSpeed(TestPlayerInputState input, Vector3 camForward, Vector3 camRight, float deltaTime)
    {
        Vector3 moveDir = camForward * input.move.y + camRight * input.move.x;
        if (moveDir.sqrMagnitude > 0.0001f)
        {
            moveDir.Normalize();
        }

        float targetVertical = Vector3.Dot(moveDir, camForward) * currentSpeed;
        float targetHorizontal = Vector3.Dot(moveDir, camRight) * currentSpeed;

        aimVerticalSpeed = Mathf.MoveTowards(aimVerticalSpeed, targetVertical, aimAcceleration * deltaTime);
        aimHorizontalSpeed = Mathf.MoveTowards(aimHorizontalSpeed, targetHorizontal, aimAcceleration * deltaTime);
    }

    /// <summary>更新垂直速度与姿态：着地贴地/起跳，离地累积重力，下落速度超过阈值才进入滞空混合。</summary>
    public void UpdateVerticalVelocity(TestPlayerInputState input, float deltaTime, bool canJump)
    {
        // 无论能否起跳都消费按下信号，避免瞄准时按下被缓冲到解除瞄准后才触发
        bool jumpPress = input.ConsumeJump();

        if (state == testPlayerStance.Climbing)
        {
            // 攀爬姿态下不应用重力（测试阶段）
            verticalVelocity = 0f;
            return;
        }

        if (state == testPlayerStance.Grounded)
        {
            if (isGrounded)
            {
                // 轻微向下压，保持与地面接触
                verticalVelocity = -2f;

                if (canJump && jumpPress)
                {
                    verticalVelocity = JumpSpeed;
                    state = testPlayerStance.Airborne;
                }
            }
            else
            {
                // 离开地面（小落差）但垂直速度还在死区内：先累积重力，不切滞空
                verticalVelocity += gravity * deltaTime;
                if (verticalVelocity < fallBlendThreshold || verticalVelocity > jumpBlendThreshold)
                {
                    state = testPlayerStance.Airborne;
                }
            }
        }
        else
        {
            verticalVelocity += gravity * deltaTime;
        }
    }

    /// <summary>姿态混合值向目标平滑过渡：着陆 → 0，滞空 → 1，攀爬 → 2。</summary>
    public void UpdateStance(float deltaTime)
    {
        float targetStance;
        switch (state)
        {
            case testPlayerStance.Airborne:
                targetStance = 1f;
                break;
            case testPlayerStance.Climbing:
                targetStance = 2f;
                break;
            default:
                targetStance = 0f;
                break;
        }
        stance = Mathf.MoveTowards(stance, targetStance, stanceAcceleration * deltaTime);
    }

    /// <summary>非瞄准时把瞄准参数衰减到 0，避免残留旧速度。</summary>
    public void DecayAimSpeed(float deltaTime)
    {
        aimVerticalSpeed = Mathf.MoveTowards(aimVerticalSpeed, 0f, aimAcceleration * deltaTime);
        aimHorizontalSpeed = Mathf.MoveTowards(aimHorizontalSpeed, 0f, aimAcceleration * deltaTime);
    }
}
#endregion

#region 攀爬检测
/// <summary>
/// 墙面探测：2 列 × N 条射线，命中可攀爬层且满足 连续/竖直/面向 判定才认为可攀爬。
/// 射线从角色根 Transform 发出（不跟骨骼），探测点固定。
/// </summary>
[System.Serializable]
public class TestWallProbe
{
    public LayerMask climbableLayer;      // 可攀爬层
    public float startHeight = 0.1f;      // 底部射线高度（避免贴地误命中）
    public float spacing = 0.4f;          // 纵向射线间距
    public int rayCount = 5;              // 每列射线数
    public float columnOffset = 0.1f;     // 左右列偏移（±columnOffset）
    public float rayLength = 0.3f;        // 射线长度
    public float maxFacingAngle = 30f;    // 角色前向与墙面法线最大夹角
    public float maxSlope = 0.25f;        // 墙面法线 |y| 上限（竖直判定，排除斜坡/天花板）
    public float maxNormalSpread = 45f;   // 各命中点法线与平均法线的最大夹角（容忍凹凸，排除转角/分叉面）
    public bool includeTriggers = false;  // 是否检测触发器碰撞体（攀爬面常做成 trigger）

    readonly Vector3[] hitPoints = new Vector3[20]; // 预分配，避免每帧 GC
    readonly Vector3[] hitNormals = new Vector3[20]; // 各命中点表面法线（预分配）
    int lastHitCount;          // 最近一次检测收集到的命中数（含失败前的部分命中）
    bool lastSucceeded;        // 最近一次检测是否通过
    Vector3 lastNormal;        // 最近一次通过的墙面法线
    Vector3 lastPoint;         // 最近一次通过的墙面锚点

    /// <summary>
    /// 检测角色前方是否是可攀爬的连续墙面。
    /// </summary>
    /// <param name="root">角色根 Transform（射线起点基于根部世界坐标）</param>
    /// <param name="forward">角色前向（自动投影到水平面）</param>
    /// <param name="wallNormal">命中点拟合的墙面法线（朝向角色）</param>
    /// <param name="wallPoint">命中点平均（墙面锚点）</param>
    public bool Evaluate(Transform root, Vector3 forward, out Vector3 wallNormal, out Vector3 wallPoint)
    {
        bool ok = TryEvaluate(root, forward, out wallNormal, out wallPoint);
        lastSucceeded = ok;
        if (ok)
        {
            lastNormal = wallNormal;
            lastPoint = wallPoint;
        }
        return ok;
    }

    bool TryEvaluate(Transform root, Vector3 forward, out Vector3 wallNormal, out Vector3 wallPoint)
    {
        wallNormal = Vector3.zero;
        wallPoint = Vector3.zero;
        lastHitCount = 0;

        int rows = Mathf.Clamp(rayCount, 1, 10);

        Vector3 dir = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (dir.sqrMagnitude < 0.0001f) return false;
        dir.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
        Vector3 basePos = root.position + Vector3.up * startHeight;
        QueryTriggerInteraction triggerMode = includeTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;

        int count = 0;
        for (int c = -1; c <= 1; c += 2)
        {
            Vector3 columnOrigin = basePos + right * (c * columnOffset);
            for (int i = 0; i < rows; i++)
            {
                Vector3 origin = columnOrigin + Vector3.up * (i * spacing);
                if (Physics.Raycast(origin, dir, out RaycastHit hit, rayLength, climbableLayer, triggerMode))
                {
                    hitPoints[count] = hit.point;
                    hitNormals[count] = hit.normal;
                    count++;
                    lastHitCount = count;
                }
                else
                {
                    return false; // 任一射线未命中 → 表面不连续
                }
            }
        }

        // 宏观法线 = 各命中点表面法线的平均：墙面视为宏观平整、局部可有凹凸（凹凸会被平均掉）
        Vector3 n = Vector3.zero;
        for (int i = 0; i < count; i++) n += hitNormals[i];
        if (n.sqrMagnitude < 0.0001f) return false;
        n.Normalize();
        if (Vector3.Dot(n, dir) > 0f) n = -n; // 法线统一朝向角色

        wallNormal = n;

        // 竖直判定：墙面法线 y 接近 0
        if (Mathf.Abs(n.y) > maxSlope) return false;

        // 面向判定：墙面法线与角色前向反向夹角 ≤ maxFacingAngle
        float facingAngle = Vector3.Angle(n, -dir);
        if (facingAngle > maxFacingAngle) return false;

        // 法线一致性：容忍局部凹凸，但排除转角/朝向分叉的多个表面
        Vector3 centroid = Vector3.zero;
        for (int i = 0; i < count; i++) centroid += hitPoints[i];
        centroid /= count;

        for (int i = 0; i < count; i++)
        {
            if (Vector3.Angle(hitNormals[i], n) > maxNormalSpread) return false;
        }

        wallPoint = centroid;
        return true;
    }

    /// <summary>
    /// 绘制探测点、射线、命中点与拟合法线；编辑模式下会实时执行检测预览。
    /// </summary>
    public void DrawGizmos(Transform root, Vector3 forward)
    {
        if (root == null) return;

        // 编辑模式下实时预览检测结果，方便调整探测参数
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            Evaluate(root, forward, out _, out _);
        }
#endif

        Vector3 dir = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (dir.sqrMagnitude < 0.0001f) return;
        dir.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
        Vector3 basePos = root.position + Vector3.up * startHeight;
        int rows = Mathf.Clamp(rayCount, 1, 10);

        // 探测点 + 射线（未通过黄色，通过绿色）
        Gizmos.color = lastSucceeded ? Color.green : Color.yellow;
        for (int c = -1; c <= 1; c += 2)
        {
            Vector3 columnOrigin = basePos + right * (c * columnOffset);
            for (int i = 0; i < rows; i++)
            {
                Vector3 origin = columnOrigin + Vector3.up * (i * spacing);
                Gizmos.DrawWireSphere(origin, 0.03f);
                Gizmos.DrawLine(origin, origin + dir * rayLength);
            }
        }

        // 命中点
        if (lastHitCount > 0)
        {
            Gizmos.color = Color.green;
            for (int i = 0; i < lastHitCount; i++)
            {
                Gizmos.DrawSphere(hitPoints[i], 0.04f);
                Gizmos.DrawLine(hitPoints[i], hitPoints[i] + hitNormals[i] * 0.15f); // 局部表面法线
            }
        }

        // 宏观法线 + 墙面锚点
        if (lastSucceeded)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(lastPoint, 0.06f);
            Gizmos.DrawLine(lastPoint, lastPoint + lastNormal * 0.5f);
        }
    }
}
#endregion
