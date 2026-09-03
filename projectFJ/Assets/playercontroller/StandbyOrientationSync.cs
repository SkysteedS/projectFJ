using System.Collections.Generic;
using UnityEngine;
using Cinemachine;

/// <summary>
/// 玩家相机前方同步（合并版：standby 追随 + blend 期方向插值），CinemachineExtension，
/// 挂在 StateDrivenCamera 所在对象（e.g. "player camera"）上。
///
/// 【目标】"玩家摄像机朝向不变"：
/// - standby（非 live、不在 blend 中）：每帧把 live 相机输出的朝向（yaw/pitch）写入本 rig 下
///   各 standby 子相机自己的朝向源 —— FreeLook 的 X/Y 轴、瞄准 vcam 的 POV H/V 轴，
///   使任意切换时刻各方 forward 平行于 live 相机 forward；位置不牵涉（只写轴）。
/// - blend 期间：StateDrivenCamera 的混合会把两端朝向 Slerp 旋转 → 视线方向不平行 →
///   视框被拉扯（这是"拉扯"的直接原因）。本扩展在 Finalize 阶段（混合状态已算完、写入相机之前）
///   把混合出的朝向替换为"自【切换前视框方向】向【新相机实际前方】的曲线插值"：
///   - 切换前方向 = 混合第一帧捕获（主相机当前朝向 = 上一帧最终输出）；
///   - 新相机前方 = CamB 每帧的实际 FinalOrientation——含新相机自身的俯仰钳制
///     （瞄准相机 POV 垂直边界、FreeLook 0..1 轨道值等都是"新相机前方可能被限制"的情形）；
///   - 权重 = ActiveBlend.BlendWeight（与位置混合同一条曲线）。
///   混合结束时方向恰好等于新相机实际前方 → 无归位跳变、无端部拉扯。
///   位置插值保持 Cinemachine 原样（只控制前方，位置不动的诉求不在本范围）。
///
/// 判定：CinemachineBrain.IsLive(vcam) —— live 与 blend 两端点均返回 true（源码语义），
/// 因此 blend 期间 standby 同步自动让位（两端不受写），只由上面的插值接管。
/// </summary>
public class StandbyOrientationSync : CinemachineExtension
{
    [Header("引用（留空自动查找）")]
    [Tooltip("主摄像机上的 CinemachineBrain（留空 = Camera.main 自动获取）")]
    [SerializeField] CinemachineBrain brain;

    [Header("FreeLook 俯仰映射（Y 轴 0=底部 rig / 1=顶部 rig 对应的相机俯仰角，随 orbit 配置估计）")]
    [Tooltip("FreeLook Y 轴 = 0（底部 rig，相机低位仰视）时的相机俯仰角（°）")]
    public float pitchAtYZero = 14.9f;

    [Tooltip("FreeLook Y 轴 = 1（顶部 rig，相机高位俯视）时的相机俯仰角（°）")]
    public float pitchAtYOne = -40.6f;

    [Tooltip("是否回写 FreeLook 的 Y 轴（俯仰）：开 = 退出瞄准后第三人称俯仰跟随瞄准方向（轨道取景可能变化）；关 = 保留 FreeLook 原有轨道俯仰（退出时方向会有一次平滑俯仰过渡）")]
    public bool syncFreeLookPitch = true;

    [Header("调试")]
    [Tooltip("输出每次混合起/止的关键状态（起终点相机、位置、主相机当前位置），用于定位'奇怪开始位置'")]
    public bool logBlend = false;

    // 运行时解析的引用（Awake 一次性解析；运行期回调不做任何物体/路径查询）
    CinemachineStateDrivenCamera stateDriven;
    Camera outputCamera;
    CinemachineFreeLook freeLook;
    CinemachineVirtualCamera aimVcam;
    CinemachinePOV aimPov;

    // blend 期方向插值状态
    bool blending;
    Quaternion fromOrientation;   // 切换前视框方向（混合第一帧捕获）
    int blendFrameIndex;          // 当前混合的第几帧（调试用：首帧轨迹）

    // 混合前基准锚点（侦查位置）：混合开始前的最后采样位置 + 视线 yaw，用于把每帧位移分解为"前向/侧向/垂直"
    Vector3 preBlendAnchorPos;
    float preBlendAnchorYaw;
    bool lateralWarningLogged;    // 本次混合是否已标记过横向突变
    Vector3 preBlendLastDelta;    // 混合前最后一帧的实际位移（≈ 应平滑续接的速度）
    bool preBlendHasDelta;

    // 混合前轨迹探测（logBlend 时记录切换前的相机/玩家位置，用于对比混合首帧是否平滑续接移动轨迹）
    struct PreBlendSample { public Vector3 camPos; public Vector3 playerPos; }
    readonly List<PreBlendSample> preBlendSamples = new List<PreBlendSample>();
    const int PreBlendSampleCap = 12;

    // 渲染级轨迹（CinemachineCore.CameraUpdatedEvent 采样——Brain 写相机之后的真实渲染位姿）
    bool renderedTraceActive;
    int renderedTraceCount;

    protected override void OnEnable()
    {
        base.OnEnable();
        CinemachineCore.CameraUpdatedEvent.AddListener(OnRenderedCameraUpdated);
    }

    void OnDisable()
    {
        CinemachineCore.CameraUpdatedEvent.RemoveListener(OnRenderedCameraUpdated);
    }

    /// <summary>
    /// 渲染级轨迹：CameraUpdatedEvent 在 Brain 写完相机之后触发——本回调采样到的才真正是
    /// "渲染到屏幕"的主相机位姿。与 Finalize 回调里的计算态（blendPos）并列对比，
    /// 验证"记录的首帧 = 眼睛看到的首帧"（若两者不一致 → Brain 写出前有修正，即此前盲区）。
    /// </summary>
    void OnRenderedCameraUpdated(CinemachineBrain updatedBrain)
    {
        if (!logBlend || updatedBrain != brain || brain == null || brain.OutputCamera == null) return;
        if (!renderedTraceActive || renderedTraceCount >= 20) return;

        renderedTraceCount++;
        Debug.Log($"[CamSync]   ★渲染 f{renderedTraceCount:D2} pos={VecStr(brain.OutputCamera.transform.position)} " +
                  $"rot={EulerStr(brain.OutputCamera.transform.rotation)}");
    }

    protected override void Awake()
    {
        // 必须调用基类：CinemachineExtension.Awake 执行 ConnectToVcam(true)（把本扩展注册进
        // vcam 的扩展列表）。若遮蔽基类实现，PostPipelineStageCallback 永远不会被调用。
        base.Awake();

        // —— 运行期引用一次性解析（"改为引用"约定：运行期不做物体查询/路径查找）——
        stateDriven = GetComponent<CinemachineStateDrivenCamera>();
        outputCamera = Camera.main;
        if (brain == null && outputCamera != null)
            brain = outputCamera.GetComponent<CinemachineBrain>();

        // 解析失败即整体禁用：回调只做空判断返回，不做兜底查找（避免每帧潜在查询）
        if (stateDriven == null || brain == null || outputCamera == null)
        {
            Debug.LogWarning($"[StandbyOrientationSync] 引用解析不完整（stateDriven={stateDriven}、" +
                             $"brain={brain}、outputCamera={outputCamera}），相机前方同步已禁用（不会自动重试）", this);
            return;
        }

        // 一次性收集子相机形态 + 未知形态提醒（此后运行期不再遍历子物体）
        FindChildCameras();
    }

    /// <summary>启动时收集 stateDriven 下的子相机（FreeLook / 瞄准 POV），只执行一次。</summary>
    void FindChildCameras()
    {
        foreach (var vcam in stateDriven.GetComponentsInChildren<CinemachineVirtualCameraBase>(true))
        {
            if (vcam is CinemachineFreeLook fl)
            {
                freeLook = fl;
            }
            else if (vcam is CinemachineVirtualCamera vc)
            {
                var pov = vc.GetCinemachineComponent<CinemachinePOV>();
                if (pov != null && aimPov == null)
                {
                    aimVcam = vc;
                    aimPov = pov;
                }
            }
        }

        WarnUnknownCameras();
    }

    /// <summary>提醒（一次）：存在既非 FreeLook 也无 POV 的子相机，无法同步朝向（切换方向可能跳变）。</summary>
    void WarnUnknownCameras()
    {
        if (freeLook != null && aimPov != null) return;
        foreach (var vc in stateDriven.GetComponentsInChildren<CinemachineVirtualCameraBase>(true))
        {
            bool handled = vc is CinemachineFreeLook
                || (vc is CinemachineVirtualCamera v && v.GetCinemachineComponent<CinemachinePOV>() != null);
            if (!handled)
            {
                Debug.LogWarning($"[StandbyOrientationSync] {vc.name} 非 FreeLook/无 POV 形态，" +
                                 "无法同步朝向（切换方向可能跳变）", this);
                break;
            }
        }
    }

    protected override void PostPipelineStageCallback(
        CinemachineVirtualCameraBase vcam,
        CinemachineCore.Stage stage,
        ref CameraState state,
        float deltaTime)
    {
        if (stage != CinemachineCore.Stage.Finalize) return;

        // ★关键过滤：InvokePostPipelineStageCallback 会"向层级上方传播"——本扩展挂在 StateDrivenCamera 上，
        // 也会收到所有子相机（瞄准 vcam、FreeLook 的 rigs…）的管线回调（vcam = 子相机）。
        // 若在此改写子相机状态：混合期间它们的朝向被我顶替（状态被污染），混合一结束改写停止，
        // 子相机立刻回弹到自身自然朝向 → "退出后一瞬间改变朝向"。
        // 只处理 StateDrivenCamera 自身，子相机的状态保持原样。
        if (vcam != stateDriven) return;

        // 引用已在 Awake 一次性解析；运行期回调【不做任何物体/路径查询】（解析失败 = 已禁用，此处仅空判断）
        if (stateDriven == null || brain == null || !brain.IsLive(stateDriven))
        {
            blending = false;
            return;
        }

        CinemachineBlend activeBlend = stateDriven.ActiveBlend;

        // —— blend 期间：视框方向 = 切换前方向 → 新相机实际前方（曲线插值）——
        if (activeBlend != null)
        {
            if (!blending)
            {
                // 混合第一帧：捕获"切换前视框方向" = 主相机当前朝向（brain 尚未写本帧，即上一帧最终输出）
                fromOrientation = outputCamera != null
                    ? outputCamera.transform.rotation
                    : state.FinalOrientation;
                blending = true;
                renderedTraceActive = true;   // 开启渲染级轨迹（CameraUpdatedEvent 采样）
                renderedTraceCount = 0;

                if (logBlend)
                {
                    Debug.Log($"[CamSync] ★混合开始 camA={NameOf(activeBlend.CamA)} " +
                        $"posA={posOf(activeBlend.CamA)} | camB={NameOf(activeBlend.CamB)} " +
                        $"posB={posOf(activeBlend.CamB)} | LiveChild={NameOf(stateDriven.LiveChild)} | " +
                        $"主相机位置={PosStr(outputCamera)} 朝向={RotStr(outputCamera)} | " +
                        $"ActiveVirtualCamera={NameOf(brain.ActiveVirtualCamera)}");

                    // 记录"混合前侦查位置"锚点（预混合轨迹末帧）+ 视线 yaw + 末帧速度（判断首帧是否平滑续接）
                    if (preBlendSamples.Count > 0)
                    {
                        preBlendAnchorPos = preBlendSamples[preBlendSamples.Count - 1].camPos;
                        preBlendAnchorYaw = outputCamera != null ? outputCamera.transform.eulerAngles.y : 0f;
                    }
                    else
                    {
                        preBlendAnchorPos = state.RawPosition;
                        preBlendAnchorYaw = outputCamera != null ? outputCamera.transform.eulerAngles.y : 0f;
                    }
                    preBlendHasDelta = preBlendSamples.Count >= 2;
                    preBlendLastDelta = preBlendHasDelta
                        ? preBlendSamples[preBlendSamples.Count - 1].camPos - preBlendSamples[preBlendSamples.Count - 2].camPos
                        : Vector3.zero;
                    lateralWarningLogged = false;

                    DumpPreBlendSamples();
                }
            }

            Quaternion toOrientation = fromOrientation;
            var toCam = activeBlend.CamB;   // 新相机（incoming）
            if (toCam != null && toCam.IsValid)
                toOrientation = toCam.State.FinalOrientation;   // 含新相机自身的俯仰钳制等限制

            // 与位置混合使用同一曲线权重；方向从旧视框平滑插到新相机实际前方
            state.RawOrientation = DeRoll(Quaternion.Slerp(fromOrientation, toOrientation,
                                                           activeBlend.BlendWeight));
            state.OrientationCorrection = Quaternion.identity;

            // 首帧轨迹调试：对比"本帧混合结果（即将渲染）"与"主相机上一帧输出"，
            // 并把相对"混合前侦查位置"的位移分解为前向/侧向/垂直——侧向突变时醒目标记
            blendFrameIndex++;
            if (logBlend && blendFrameIndex <= 20)
            {
                Vector3 d = state.RawPosition - preBlendAnchorPos;
                float yawRad = preBlendAnchorYaw * Mathf.Deg2Rad;
                Vector3 fwd = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));
                Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
                float dFwd = Vector3.Dot(d, fwd);
                float dSide = Vector3.Dot(d, right);

                Debug.Log($"[CamSync]   f{blendFrameIndex:D2} w={activeBlend.BlendWeight:F3} " +
                    $"blendPos={VecStr(state.RawPosition)} mainPrevPos={(outputCamera != null ? VecStr(outputCamera.transform.position) : "(null)")} | " +
                    $"blendRot={EulerStr(state.RawOrientation)} mainPrevRot={(outputCamera != null ? EulerStr(outputCamera.transform.rotation) : "(null)")} | " +
                    $"Δ侦查位 前={dFwd * 100f:F1}cm 侧={dSide * 100f:F1}cm 上={d.y * 100f:F1}cm");

                // 混合首帧 vs 混合前末帧：首帧位移应平滑续接预混合速度——侧向偏差 >3cm 即"开始混合即突变"
                if (blendFrameIndex <= 2 && preBlendHasDelta)
                {
                    float vSide = Vector3.Dot(preBlendLastDelta, right);
                    float vFwd = Vector3.Dot(preBlendLastDelta, fwd);
                    Debug.Log($"[CamSync]   └首帧Δ 前={dFwd * 100f:F1}cm 侧={dSide * 100f:F1}cm 上={d.y * 100f:F1}cm | " +
                        $"预混合末帧速度 前={vFwd * 100f:F1} 侧={vSide * 100f:F1}（cm/帧）");
                    if (!lateralWarningLogged && (Mathf.Abs(dSide - vSide) > 0.03f || Mathf.Abs(dFwd - vFwd) > 0.06f))
                    {
                        lateralWarningLogged = true;
                        Debug.Log($"[CamSync] ⚠⚠开始混合即突变 f{blendFrameIndex}：首帧位移与预混合速度不连续——" +
                            $"侧向差={Mathf.Abs(dSide - vSide) * 100f:F1}cm（首帧侧={dSide * 100f:F1}cm vs 预混合侧速={vSide * 100f:F1}cm/帧）");
                    }
                }

                // 侧向突变标记：相对侦查位置的侧向偏移 > 5cm 且发生在混合前 ~1/3（f<=8）——
                // 合法混合的侧向 ≈ 肩位侧偏(45cm)×w，前 8 帧正常情况下 < ~2cm；>5cm 即异常"横向先跳"
                if (!lateralWarningLogged && Mathf.Abs(dSide) > 0.05f && blendFrameIndex <= 8)
                {
                    lateralWarningLogged = true;
                    Debug.Log($"[CamSync] ⚠⚠横向偏移 f{blendFrameIndex} 侧向偏移={dSide * 100f:F1}cm" +
                        $"（前向={dFwd * 100f:F1}cm 上={d.y * 100f:F1}cm）——先横向跳一截，再从偏移位置向目标混合");
                }
            }
            return;
        }

        // —— 无混合（纯 standby / live 单相机）：standby 子相机前方持续追随 live ——
        if (blending && logBlend)
        {
            Debug.Log($"[CamSync] ★混合结束（ActiveBlend=null）LiveChild={NameOf(stateDriven.LiveChild)} " +
                $"主相机位置={PosStr(outputCamera)} 朝向={RotStr(outputCamera)}");
        }

        blending = false;
        blendFrameIndex = 0;
        renderedTraceActive = false;   // 混合结束：关闭渲染级轨迹采样

        // 混合前轨迹探测：记录当前显示相机与玩家位置（logBlend 时；环形上限 12 帧，
        // 供混合开始时对照——移动中切瞄准时，混合首帧应平滑续接预混合运动轨迹）
        if (logBlend)
        {
            PushPreBlendSample(
                outputCamera != null ? outputCamera.transform.position : state.RawPosition,
                freeLook != null && freeLook.Follow != null ? freeLook.Follow.position : Vector3.zero);
        }

        Quaternion view = outputCamera != null
            ? outputCamera.transform.rotation
            : state.FinalOrientation;
        float yaw = Normalize180(view.eulerAngles.y);
        float pitch = Normalize180(view.eulerAngles.x);

        // FreeLook standby → 同步 X 轴（世界 yaw）；俯仰按选项回写（线性映射到 0..1 并钳制边界）
        if (freeLook != null && !brain.IsLive(freeLook))
        {
            freeLook.m_XAxis.Value = yaw;
            if (syncFreeLookPitch)
            {
                freeLook.m_YAxis.Value = Mathf.Clamp(
                    Mathf.InverseLerp(pitchAtYZero, pitchAtYOne, pitch),
                    freeLook.m_YAxis.m_MinValue, freeLook.m_YAxis.m_MaxValue);
            }
        }

        // 瞄准 vcam standby → 同步 POV H/V 轴（俯仰 clamp 到轴自身边界）
        if (aimPov != null && aimVcam != null && !brain.IsLive(aimVcam))
        {
            aimPov.m_HorizontalAxis.Value = yaw;
            aimPov.m_VerticalAxis.Value = Mathf.Clamp(
                pitch, aimPov.m_VerticalAxis.m_MinValue, aimPov.m_VerticalAxis.m_MaxValue);
        }
    }

    /// <summary>去除 roll：仅保留 yaw/pitch（Euler(pitch, yaw, 0)）。</summary>
    static Quaternion DeRoll(Quaternion rot)
    {
        Vector3 e = rot.eulerAngles;
        float pitch = e.x > 180f ? e.x - 360f : e.x;
        return Quaternion.Euler(pitch, e.y, 0f);
    }

    /// <summary>角度规范化到 [-180, 180)。</summary>
    static float Normalize180(float angle)
    {
        angle = Mathf.Repeat(angle, 360f);
        return angle > 180f ? angle - 360f : angle;
    }

    #region 调试辅助
    static string NameOf(ICinemachineCamera cam) => cam == null ? "(null)" : cam.Name;
    static string posOf(ICinemachineCamera cam) => cam == null ? "(null)"
        : $"({cam.State.RawPosition.x:F2}, {cam.State.RawPosition.y:F2}, {cam.State.RawPosition.z:F2})";
    static string PosStr(Camera cam) => cam == null ? "(null)"
        : $"({cam.transform.position.x:F2}, {cam.transform.position.y:F2}, {cam.transform.position.z:F2})";
    static string RotStr(Camera cam) => cam == null ? "(null)"
        : $"({cam.transform.eulerAngles.x:F1}, {cam.transform.eulerAngles.y:F1}, {cam.transform.eulerAngles.z:F1})";
    static string VecStr(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
    static string EulerStr(Quaternion q) => $"({q.eulerAngles.x:F1}, {q.eulerAngles.y:F1}, {q.eulerAngles.z:F1})";

    /// <summary>记录一帧预混合采样（相机显示位置 + 玩家位置），超出上限丢最旧。</summary>
    void PushPreBlendSample(Vector3 camPos, Vector3 playerPos)
    {
        if (preBlendSamples.Count >= PreBlendSampleCap) preBlendSamples.RemoveAt(0);
        preBlendSamples.Add(new PreBlendSample { camPos = camPos, playerPos = playerPos });
    }

    /// <summary>打印预混合轨迹（末 6 帧，含逐帧相机位移），并以本次为界清空。</summary>
    void DumpPreBlendSamples()
    {
        int n = preBlendSamples.Count;
        if (n < 2) return;
        int start = Mathf.Max(0, n - 6);
        for (int i = start; i < n; ++i)
        {
            var s = preBlendSamples[i];
            string delta = i > 0 ? VecStr(s.camPos - preBlendSamples[i - 1].camPos) : "     -";
            Debug.Log($"[CamSync]   ▲预混合 camPos={VecStr(s.camPos)} Δ={delta} playerPos={VecStr(s.playerPos)}");
        }
        preBlendSamples.Clear();
    }
    #endregion
}
