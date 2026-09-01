/// <summary>
/// 具体状态：空手（Unarmed）× 正常手部（Normal）× 着地（Ground）。
/// 跳跃/攀爬触发由信号边裁决（切换不发生在类内），本类专注地面移动。
/// </summary>
public class Unarmed_Normal_Ground_State : PlayerStateBase
{
    public override void Tick(PlayerContext ctx)
    {
        // Phase 2 后续：
        // - 走/跑速度平滑（空手状态实现奔跑档，本类内直接处理）
        // - WASD → 相机轴世界朝向旋转
        // - 建议：跳跃/攀爬的信号裁决在 PlayerStateMachine 中进行，先于本 Tick 执行
    }
}
