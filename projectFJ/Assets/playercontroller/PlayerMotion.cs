using UnityEngine;

/// <summary>
/// 共享运动描述（跨状态交接槽）：当前状态类在每次 Tick/OnAnimatorMove 末尾写入，
/// 新状态在 Enter 中读取——否则切换后新状态无从得知当前运动信息。
///
/// 使用约定：
/// - 写入者：当前状态类（计算完毕即写入，保证切换后读取到的是最新值）；
/// - 读取者：进入的新状态（Enter 中读取初值，如滞空继承起跳水平速度、
///   落地处理读取下落速度做姿态混合）；
/// - 纯数据，不含计算：速度平滑/重力等计算属于数值层（Phase 2 接入），
///   本类只存"结果描述"。
/// </summary>
public class PlayerMotion
{
    /// <summary>世界速度向量（含垂直分量，单位 m/s）。水平分量 = 走/跑速度方向与大小，垂直分量 = 手写重力累积。</summary>
    public Vector3 Velocity;

    /// <summary>水平速度大小（只读便捷：走/跑档位、滞空水平操控用）。</summary>
    public float HorizontalSpeed => Mathf.Sqrt(Velocity.x * Velocity.x + Velocity.z * Velocity.z);

    /// <summary>垂直速度分量（只读便捷：下落姿态混合、落地捕获用）。</summary>
    public float VerticalVelocity => Velocity.y;
}
