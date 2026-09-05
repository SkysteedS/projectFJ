using UnityEngine;

/// <summary>
/// 墙面探测（自 早期原型 平移并按新版需求参数化）：
/// 从角色根部向前方射出【参数化射线阵列】——列数 × 行数 × 行间距 × 列间距，全部命中，
/// 且满足 竖直 / 面向 / 法线一致 判定才算可攀爬。法线拟合（命中点平均→归一化→朝向角色）与 早期原型 保持一致。
/// 两种模式：
/// - Evaluate（严格，进入攀爬用）：任一射线未命中即失败（表面不连续），全部命中后才做几何校验；
/// - EvaluateCoverage（覆盖式，攀爬中持续贴墙用）：全部射线都发射统计，允许部分缺失
///   （如头顶射线越过墙顶），是否“缺失过半”由调用方按 Values.climbWallExitMissRatio 判定。
/// 仅作探测工具：进入攀爬的裁决在状态机信号边（Jump + 速度朝墙），状态内每帧重测用于退出条件。
/// </summary>
[System.Serializable]
public class WallProbe
{
    [Tooltip("视为可攀爬的层（仅探测这些层）")]
    public LayerMask climbableLayer = ~0;

    [Tooltip("底部射线起点高度（自根部上抬，避免贴地误命中）")]
    public float startHeight = 0.1f;

    [Tooltip("列数（横向排布，沿角色左右轴对称；上限 8）")]
    public int columns = 2;

    [Tooltip("行数（纵向排布，自下而上；上限 8）")]
    public int rows = 5;

    [Tooltip("行间距（纵向射线间隔）")]
    public float rowSpacing = 0.4f;

    [Tooltip("列间距（横向射线间隔，列对称于角色中轴）")]
    public float columnSpacing = 0.2f;

    [Tooltip("单条射线长度")]
    public float rayLength = 0.3f;

    [Tooltip("可攀爬的最大朝向角：角色前向与墙面法线反方向的最大夹角")]
    public float maxFacingAngle = 30f;

    [Tooltip("墙面法线 |y| 上限（竖直判定，排除斜坡/天花板）")]
    public float maxSlope = 0.25f;

    [Tooltip("各命中点法线与平均法线的最大夹角（容忍凹凸，排除转角/分叉面）")]
    public float maxNormalSpread = 45f;

    [Tooltip("是否检测触发器碰撞体（攀爬面常做成 trigger）")]
    public bool includeTriggers = false;

    #region 预分配（上限 8 列 × 8 行，避免每帧 GC）
    const int MaxColumns = 8;
    const int MaxRows = 8;
    const int MaxRays = MaxColumns * MaxRows;
    /// <summary>方向向量平方长度下限（零向量防护）。</summary>
    const float MinDirectionSqr = 0.0001f;
    readonly Vector3[] hitPoints = new Vector3[MaxRays];   // 各命中点（预分配）
    readonly Vector3[] hitNormals = new Vector3[MaxRays];  // 各命中点表面法线（预分配）
    int lastHitCount;      // 最近一次检测收集到的命中数
    int lastTotalCount;    // 最近一次检测的总射线数（列数 × 行数）
    bool lastSucceeded;    // 最近一次检测是否通过
    Vector3 lastNormal;    // 最近一次通过的墙面法线
    Vector3 lastPoint;     // 最近一次通过的墙面锚点
    #endregion

    /// <summary>最近一次通过的墙面法线（朝向角色；仅 LastSucceeded 时有意义）。</summary>
    public Vector3 LastNormal => lastNormal;

    /// <summary>最近一次通过的墙面锚点（命中点平均）。</summary>
    public Vector3 LastPoint => lastPoint;

    /// <summary>最近一次检测是否通过。</summary>
    public bool LastSucceeded => lastSucceeded;

    /// <summary>最近一次检测收集到的命中射线数（全部射线均已发射统计，供覆盖式判定使用）。</summary>
    public int LastHitCount => lastHitCount;

    /// <summary>最近一次检测的总射线数（列数 × 行数）。</summary>
    public int LastTotalCount => lastTotalCount;

    /// <summary>
    /// 检测角色前方是否是可攀爬的连续墙面（结果缓存于 Last* 属性）。
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

        int cols = Mathf.Clamp(columns, 1, MaxColumns);
        int rowCount = Mathf.Clamp(rows, 1, MaxRows);
        int total = cols * rowCount;

        Vector3 dir = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (dir.sqrMagnitude < MinDirectionSqr) return false;
        dir.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
        Vector3 basePos = root.position + Vector3.up * startHeight;
        QueryTriggerInteraction triggerMode = includeTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;

        lastTotalCount = total;
        int count = CastGrid(root, dir, right, basePos, triggerMode, cols, rowCount);
        lastHitCount = count;

        // 严格语义（进入攀爬用）：任一射线未命中 → 表面不连续
        if (count < total) return false;

        return ValidateSurface(count, dir, out wallNormal, out wallPoint);
    }

    /// <summary>
    /// 覆盖式墙面检测（攀爬中持续贴墙用）：全部射线都发射并统计，不因个别射线缺失
    /// （例如头顶射线越过墙顶）立即判失败。存在命中且命中点满足几何校验即返回 true；
    /// 命中/总射线数经 out 与 Last* 暴露，是否“缺失过半”由调用方按需求判定
    /// （攀爬状态用 Values.climbWallExitMissRatio，默认缺失 ≥ 50% 才退出）。
    /// </summary>
    public bool EvaluateCoverage(Transform root, Vector3 forward, out Vector3 wallNormal, out Vector3 wallPoint,
                                 out int hitCount, out int totalCount)
    {
        bool ok = TryEvaluateCoverage(root, forward, out wallNormal, out wallPoint);
        lastSucceeded = ok;
        if (ok)
        {
            lastNormal = wallNormal;
            lastPoint = wallPoint;
        }
        hitCount = lastHitCount;
        totalCount = lastTotalCount;
        return ok;
    }

    bool TryEvaluateCoverage(Transform root, Vector3 forward, out Vector3 wallNormal, out Vector3 wallPoint)
    {
        wallNormal = Vector3.zero;
        wallPoint = Vector3.zero;

        int cols = Mathf.Clamp(columns, 1, MaxColumns);
        int rowCount = Mathf.Clamp(rows, 1, MaxRows);
        int total = cols * rowCount;

        Vector3 dir = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (dir.sqrMagnitude < MinDirectionSqr) return false;
        dir.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
        Vector3 basePos = root.position + Vector3.up * startHeight;
        QueryTriggerInteraction triggerMode = includeTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;

        lastTotalCount = total;
        int count = CastGrid(root, dir, right, basePos, triggerMode, cols, rowCount);
        lastHitCount = count;

        if (count == 0) return false;
        return ValidateSurface(count, dir, out wallNormal, out wallPoint);
    }

    /// <summary>发射全部网格射线并收集命中点（不因个别未命中提前退出）。</summary>
    int CastGrid(Transform root, Vector3 dir, Vector3 right, Vector3 basePos,
                 QueryTriggerInteraction triggerMode, int cols, int rowCount)
    {
        int count = 0;
        for (int c = 0; c < cols; c++)
        {
            // 列布局：沿左右轴对称（偶数列分布于中轴两侧；奇数列含中轴）→ (c - (cols-1)/2) × columnSpacing
            float lateral = (c - (cols - 1) * 0.5f) * columnSpacing;
            Vector3 columnOrigin = basePos + right * lateral;
            for (int i = 0; i < rowCount; i++)
            {
                Vector3 origin = columnOrigin + Vector3.up * (i * rowSpacing);
                if (Physics.Raycast(origin, dir, out RaycastHit hit, rayLength, climbableLayer, triggerMode)
                    && count < MaxRays)
                {
                    hitPoints[count] = hit.point;
                    hitNormals[count] = hit.normal;
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>用命中点做宏观法线拟合 + 竖直/面向/法线一致性校验；通过时输出墙面法线与锚点。</summary>
    bool ValidateSurface(int count, Vector3 dir, out Vector3 wallNormal, out Vector3 wallPoint)
    {
        wallNormal = Vector3.zero;
        wallPoint = Vector3.zero;
        if (count <= 0) return false;

        // 宏观法线 = 各命中点表面法线的平均：墙面视为宏观平整、局部可有凹凸（凹凸会被平均掉）
        Vector3 n = Vector3.zero;
        for (int i = 0; i < count; i++) n += hitNormals[i];
        if (n.sqrMagnitude < MinDirectionSqr) return false;
        n.Normalize();
        if (Vector3.Dot(n, dir) > 0f) n = -n; // 法线统一朝向角色

        wallNormal = n;

        // 竖直判定：墙面法线 y 接近 0
        if (Mathf.Abs(n.y) > maxSlope) return false;

        // 面向判定：墙面法线与角色前向反向夹角 ≤ maxFacingAngle
        if (Vector3.Angle(n, -dir) > maxFacingAngle) return false;

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
        if (dir.sqrMagnitude < MinDirectionSqr) return;
        dir.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
        Vector3 basePos = root.position + Vector3.up * startHeight;
        int cols = Mathf.Clamp(columns, 1, MaxColumns);
        int rowCount = Mathf.Clamp(rows, 1, MaxRows);

        // 探测点 + 射线（未通过黄色，通过绿色）
        Gizmos.color = lastSucceeded ? Color.green : Color.yellow;
        for (int c = 0; c < cols; c++)
        {
            float lateral = (c - (cols - 1) * 0.5f) * columnSpacing;
            Vector3 columnOrigin = basePos + right * lateral;
            for (int i = 0; i < rowCount; i++)
            {
                Vector3 origin = columnOrigin + Vector3.up * (i * rowSpacing);
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
