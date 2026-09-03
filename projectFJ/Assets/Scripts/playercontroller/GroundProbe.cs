using UnityEngine;

/// <summary>
/// 着地探测：替代 CharacterController.isGrounded（该标志在台阶/边缘会帧级抽搐：
/// capsule 与地面接触瞬时丢失/恢复，直接用于落地/起跳判定会导致姿态抖动）。
/// 做法：从角色胶囊下沿球心向下 SphereCast + 双向去抖（连续命中 N 帧才确认着地、
/// 连续未命中 M 帧才确认离地），输出稳定的着地标志。
/// 仅探测 groundLayer；不含触发器（攀爬面等 trigger 不参与着地）。
/// </summary>
[System.Serializable]
public class GroundProbe
{
    [Tooltip("视为地面的层（仅探测这些层）")]
    public LayerMask groundLayer = ~0;

    [Tooltip("胶囊下沿球心的半径系数（0~1；越大探测越宽泛，越小越贴着脚底）")]
    public float radiusScale = 0.5f;

    [Tooltip("球心相对胶囊下沿的上抬量，避免与地面重叠导致的误命中")]
    public float skinOffset = 0.01f;

    [Tooltip("向下探测距离（自球心起，需略大于皮肤余量）")]
    public float probeDistance = 0.15f;

    [Tooltip("连续命中多少帧才确认着地（防边缘处一帧误报悬空）")]
    public int requireFrames = 2;

    [Tooltip("连续未命中多少帧才确认离地（防边缘处一帧误报告落地）")]
    public int missFrames = 2;

    int confirmFrames;
    int missCount;

    /// <summary>去抖后的着地标志（双向：需连续命中/未命中才翻转）。</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>
    /// 每帧调用一次，返回（并缓存）去抖后的着地标志。
    /// 结果写入 PlayerContext.IsGrounded 供状态类只读；状态机不直接依赖 CharacterController.isGrounded。
    /// </summary>
    public bool Evaluate(Transform root, CharacterController characterController)
    {
        if (root == null || characterController == null)
        {
            IsGrounded = false;
            return IsGrounded;
        }

        Vector3 sphereCenter = ComputeSphereCenter(root, characterController);
        float radius = characterController.radius * radiusScale;

        bool hit = DoCast(sphereCenter, radius, out _);

        if (hit)
        {
            missCount = 0;
            confirmFrames++;
            if (confirmFrames >= requireFrames) IsGrounded = true;
        }
        else
        {
            confirmFrames = 0;
            missCount++;
            if (missCount >= missFrames) IsGrounded = false;
        }
        return IsGrounded;
    }

    /// <summary>胶囊下沿球心 = 中心点 - 半身高 + 半径系数回退，再上抬皮肤余量防重叠。</summary>
    Vector3 ComputeSphereCenter(Transform root, CharacterController characterController)
        => root.position
           + Vector3.up * (characterController.center.y - characterController.height * 0.5f
                           + characterController.radius * radiusScale + skinOffset);

    /// <summary>实际探测（Evaluate 与 Gizmos 预览共用）。</summary>
    bool DoCast(Vector3 sphereCenter, float radius, out Vector3 hitPoint)
    {
        bool hit = Physics.SphereCast(sphereCenter, radius, Vector3.down, out var hitInfo,
                                      probeDistance, groundLayer, QueryTriggerInteraction.Ignore);
        hitPoint = hit ? hitInfo.point : Vector3.zero;
        return hit;
    }

    /// <summary>
    /// Gizmos 调试视图（选中角色时由 PlayerControllerScript.OnDrawGizmosSelected 调用）：
    /// - 结果球（实线框）：探测的【最终落点】——命中时球与地面相切（绿），未命中时推到探测尽头（红）；
    /// - 起点球（浅色半透明线框）：探测起始位置，示意"球从这里向下推"；
    /// - 连接线：球心 → 最终球心；命中时另有接触点小球。
    /// 编辑模式实时预览（不推进去抖计数——去抖状态仅运行时有意义）。
    /// </summary>
    public void DrawGizmos(Transform root, CharacterController characterController)
    {
        if (root == null || characterController == null) return;

        Vector3 sphereCenter = ComputeSphereCenter(root, characterController);
        float radius = characterController.radius * radiusScale;
        bool hit = DoCast(sphereCenter, radius, out Vector3 hitPoint);

        // 最终球心：命中 = 球被地面挡停（下沿与地面相切）；未命中 = 推到探测尽头
        Vector3 endCenter = hit
            ? hitPoint + Vector3.up * radius
            : sphereCenter + Vector3.down * probeDistance;

        // 结果球（探测点位置）：绿 = 命中，红 = 未命中
        Gizmos.color = hit ? Color.green : Color.red;
        Gizmos.DrawWireSphere(endCenter, radius);
        Gizmos.DrawLine(sphereCenter, endCenter);          // 探测方向（起始球心 → 最终球心）
        if (hit)
        {
            Gizmos.DrawSphere(hitPoint, 0.05f);            // 接触点
        }

        // 起点球（浅色半透明，仅示意探测从哪开始）
        Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
        Gizmos.DrawWireSphere(sphereCenter, radius);
    }
}
