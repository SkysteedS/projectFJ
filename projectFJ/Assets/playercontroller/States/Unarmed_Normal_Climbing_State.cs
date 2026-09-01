/// <summary>
/// 具体状态：空手（Unarmed）× 正常手部（Normal）× 攀爬（Climbing）。
/// 进入入口：信号边（Jump + 可攀爬检测）；退出由本类提议（墙失效/到顶）。
/// </summary>
public class Unarmed_Normal_Climbing_State : PlayerStateBase
{
    public override void Tick(PlayerContext ctx)
    {
        // 攀爬 Phase 后续：
        // - WallProbe 每帧重探测（miss 帧容错 → 提议回 Ground）
        // - 输入映射到墙面切平面移动 + 贴墙收敛
        // - 攀爬手部 IK（Enter 停用 hand ik 层并初始化目标；Exit 恢复）
        // - 墙顶边缘探测 → 提议走廊状态（登顶/翻越 TopOut，未来枚举）
    }
}
