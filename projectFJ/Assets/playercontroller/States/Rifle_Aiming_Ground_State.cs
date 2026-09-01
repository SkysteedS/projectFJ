/// <summary>
/// 具体状态：步枪（Rifle）× 瞄准（Aiming）× 着地（Ground）。
/// 进入/退出由持久边驱动（按住右键进入、松开退出——状态机统一处理）；
/// 本状态专注瞄准下的移动：锁定旋转、2D 混合树、瞄准速度。
/// </summary>
public class Rifle_Aiming_Ground_State : PlayerStateBase
{
    public override void Tick(PlayerContext ctx)
    {
        // Phase 2 后续：
        // - 瞄准移动（WASD 投影到相机前/右轴的 2D 速度，无转向）
        // - 禁跳 / 禁切武器已由"无相应边"自然保证（Rifle_Aiming_Ground 没有 Jump/切枪出边）
    }
}
