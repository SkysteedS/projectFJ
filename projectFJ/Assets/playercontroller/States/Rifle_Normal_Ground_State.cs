/// <summary>
/// 具体状态：步枪（Rifle）× 正常手部（Normal）× 着地（Ground）。
/// 瞄准进入由持久边驱动（按住右键）；持枪不可跑（能力表无 Run）。
/// </summary>
public class Rifle_Normal_Ground_State : PlayerStateBase
{
    public override void Tick(PlayerContext ctx)
    {
        // Phase 2 后续：
        // - 行走速度（步枪状态不实现奔跑档：按 Shift 不加速，本类内直接处理）
        // - 转向
    }
}
