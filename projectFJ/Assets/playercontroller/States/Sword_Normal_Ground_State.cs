/// <summary>
/// 具体状态：剑（Sword）× 正常手部（Normal）× 着地（Ground）。
/// 持剑可奔跑（能力表含 Run）。
/// </summary>
public class Sword_Normal_Ground_State : PlayerStateBase
{
    public override void Tick(PlayerContext ctx)
    {
        // Phase 2 后续：
        // - 走/跑速度平滑（剑状态实现奔跑档，本类内直接处理）
        // - 转向
    }
}
