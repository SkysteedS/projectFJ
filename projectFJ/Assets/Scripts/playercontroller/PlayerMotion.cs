using UnityEngine;

/// <summary>
/// 共享运动描述（跨状态交接槽）：当前状态类在每次 Tick/OnAnimatorMove 末尾写入，
/// 新状态在 Enter 中读取——否则切换后新状态无从得知当前运动信息。
///
/// 使用约定：
/// - 写入者：当前状态类（计算完毕即写入，保证切换后读取到的是最新值）；
/// - 读取者：进入的新状态（Enter 中读取初值，如滞空继承起跳水平速度、
///   落地处理读取下落速度做姿态混合）；
/// - 纯数据，不含计算：速度平滑/重力等计算在数值层（PlayerMotionValuesSO），
///   本类只存"结果描述"，由状态类经 PlayerContext.Values 调用计算函数后写入。
/// </summary>
public class PlayerMotion
{
    /// <summary>世界速度向量（含垂直分量，单位 m/s）。水平分量 = 走/跑速度方向与大小，垂直分量 = 手写重力累积。</summary>
    public Vector3 Velocity;

    /// <summary>
    /// 最近一次有效移动方向（水平面单位向量，默认世界 forward）。
    /// 松开输入时继续持有（速度标量按加速率衰减、动画速度参数平滑归零），
    /// 不再用"方向 × 速度"的一根筋表示——否则方向归零会连带摧毁速度标量的平滑状态。
    /// </summary>
    public Vector3 HorizontalDir = Vector3.forward;

    /// <summary>水平速度大小（只读便捷：走/跑档位、滞空水平操控用）。</summary>
    public float HorizontalSpeed => Mathf.Sqrt(Velocity.x * Velocity.x + Velocity.z * Velocity.z);

    /// <summary>垂直速度分量（只读便捷：下落姿态混合、落地捕获用）。</summary>
    public float VerticalVelocity => Velocity.y;

    /// <summary>
    /// 最近一次状态切换是否改变了手持（武器切换）——由状态机 SwitchTo 裁决时写入，
    /// 新状态 Enter 读取后清除（用后即毁）。供地面移动状态启用"武器切换速度插值"。
    /// </summary>
    public bool HandingChanged;

    /// <summary>
    /// 切换前的手持——仅武器切换（HandingChanged）时由状态机 SwitchTo 写入，不在状态层清除。
    /// 用途：主体类拔/收枪 IK 接管需要识别"收的是哪把武器"；收枪后当前 Handing 已回到 Unarmed，
    /// 单凭当前状态无法区分（当前仅步枪启用切换中段轨迹接管，手枪/手雷一律不接管）。
    /// </summary>
    public PlayerHanding PreviousHanding;
}
