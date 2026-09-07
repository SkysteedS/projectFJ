using UnityEngine;

/// <summary>
/// 单实体武器切换（极简版）：武器本体只有【一个实例】，在【两个挂点】之间切换：
/// 使用中挂点（inUseMount，如手持） ↔ 未使用挂点（storedMount，如背上/腰间）。
/// 每个挂点 = 一个 GameObject 父级引用 + 脚本内部持有的【基准 local 变换数据】
/// （Position/Rotation/Scale 直接由 Inspector 输入，无需创建/拖入外部 Transform 物体）：
/// - 武器的新父级 = 挂点的 parent；
/// - 武器的 local TRS = 挂点内部的基准值（你设定它，武器就贴成什么样）。
///
/// 切换动作：
/// 1. 标记 isUsing：切到使用中挂点 = true；切到未使用挂点 = false（与挂点语义自动联动）；
/// 2. 武器父级 = 挂点 parent（不保持世界位姿——直接按基准 local 放置）；
/// 3. 武器 localPosition/localRotation/localScale = 挂点基准值。
/// 4. 收纳（stored）时直接关闭武器 GameObject（SetActive(false)）、切回使用中恢复（SetActive(true)）——
///    收纳武器不再渲染、不参与更新/物理；武器上各组件的 Awake/Start 初始化由"开局先保持 active、
///    第一帧再按状态收纳"保证至少完整跑过一遍（不在 Start 内直接关闭）。
///
/// 与瞄准程序化 IK 的关系：本脚本只负责"非瞄准的持/收纳切换"；瞄准中的轴点切父
/// 由 Rifle_Aiming_Ground_State 单独处理（两者勿在同一武器上同时驱动父级）。
/// </summary>
public class WeaponSwitch : MonoBehaviour
{
    /// <summary>挂点：父级引用 + 内部基准 local 变换数据（值由 Inspector 设定）。</summary>
    [System.Serializable]
    public class MountPoint
    {
        [Tooltip("挂点父级（GameObject）：武器切换时作为它的子级；空 = 挂到场景根")]
        public GameObject parent;

        [Tooltip("武器挂在父级下的 localPosition 基准")]
        public Vector3 localPosition = Vector3.zero;

        [Tooltip("武器挂在父级下的 localRotation 基准（Euler 角度，输入即所得；不用 Quaternion 字段——Inspector 对 Quaternion 的欧拉显示存在多值往返，非主值域角度编辑时会跳成等价表示）")]
        public Vector3 localEulerAngles = Vector3.zero;

        [Tooltip("武器挂在父级下的 localScale 基准")]
        public Vector3 localScale = Vector3.one;
    }

    [Header("武器本体（单实体）")]
    [Tooltip("武器根：切换时它的父级与 local TRS 会被改写")]
    public Transform weapon;

    [Header("挂点（父级引用 + 内部基准值）")]
    [Tooltip("使用中挂点（如手持挂点）：切到它时 isUsing = true")]
    public MountPoint inUseMount = new MountPoint();

    [Tooltip("未使用/收纳挂点（如背上或腰间挂点）：切到它时 isUsing = false")]
    public MountPoint storedMount = new MountPoint();

    [Header("状态")]
    [Tooltip("当前武器是否处于使用中（与挂点联动：使用挂点=true / 收纳挂点=false；可经 SetUsing 覆盖）")]
    [SerializeField] bool isUsing;

    /// <summary>武器当前是否处于使用中（与所选挂点联动，见 SetUsing）。</summary>
    public bool IsUsing => isUsing;

    /// <summary>切到使用中挂点：父级 = inUseMount.parent，local TRS = inUseMount 基准值，isUsing = true，并 SetActive(true)。</summary>
    public void SwitchToInUse() => ApplyMount(inUseMount, true);

    /// <summary>切到未使用挂点：父级 = storedMount.parent，local TRS = storedMount 基准值，isUsing = false，并 SetActive(false) 收纳隐藏。</summary>
    public void SwitchToStored() => ApplyMount(storedMount, false);

    /// <summary>按语义切换：true = 使用中挂点（激活），false = 未使用挂点（收纳隐藏）。</summary>
    public void SwitchTo(bool inUse) => ApplyMount(inUse ? inUseMount : storedMount, inUse);

    /// <summary>直接设置使用中标记（不改变父级/位姿）。</summary>
    public void SetUsing(bool value) => isUsing = value;

    void ApplyMount(MountPoint mount, bool inUse)
    {
        if (weapon == null)
        {
            Debug.LogWarning("[WeaponSwitch] weapon 未指派，无法切换挂点", this);
            return;
        }
        if (mount == null) mount = new MountPoint();

        // ① 标记使用中（与挂点语义联动）
        isUsing = inUse;

        // ② 父级 = 挂点 parent（不保持世界位姿：基准 local 由挂点值决定）
        if (mount.parent != null)
        {
            weapon.SetParent(mount.parent.transform, false);
        }
        else
        {
            weapon.SetParent(null, false);
        }

        // ③ local TRS = 挂点基准值（Inspector 输入；旋转按 Euler 应用）
        weapon.localPosition = mount.localPosition;
        weapon.localRotation = Quaternion.Euler(mount.localEulerAngles);
        weapon.localScale = mount.localScale;

        // ④ 收纳直接关闭武器 GameObject / 使用中恢复激活（避免背后枪械遮挡视野；也停止其更新/物理）
        weapon.gameObject.SetActive(inUse);
    }

    bool pendingStoredSync;   // 开局待同步标记：第一帧再按当前状态收纳（先让武器组件初始化完整跑一遍）

    void Start()
    {
        // 不在 Start 内直接 SetActive(false)：开局先保持 active，让武器上各组件的
        // Awake/Start 初始化完整执行；若当前为收纳态，延迟到第一帧 Update 再关闭。
        pendingStoredSync = !isUsing;
    }

    void Update()
    {
        if (!pendingStoredSync) return;
        pendingStoredSync = false;

        // 第一帧同步收纳态（武器此刻仍 active，Update 可执行；本帧结束后武器被关闭，后续状态由切换事件驱动）
        if (!isUsing && weapon != null) weapon.gameObject.SetActive(false);
    }
}
