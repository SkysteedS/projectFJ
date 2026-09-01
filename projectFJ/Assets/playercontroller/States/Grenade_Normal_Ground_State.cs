/// <summary>
/// 具体状态：手雷（Grenade）× 正常手部（Normal）× 着地（Ground）。
/// 持手雷不可跑（能力表无 Run）。
/// </summary>
public class Grenade_Normal_Ground_State : PlayerStateBase
{
    public override void Tick(PlayerContext ctx)
    {
        // Phase 2 后续：
        // - 行走速度（不可跑）
        // - 转向
    }
}
