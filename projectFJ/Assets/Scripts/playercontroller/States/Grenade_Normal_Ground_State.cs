/// <summary>
/// 具体状态：手雷（Grenade）× 正常手部（Normal）× 着地（Ground）。
/// 规划中：工厂已映射本组合，但控制器尚未注册、无转换边 → 运行期不可达；
/// 接入手雷槽位（SlotGrenade 信号边 + 注册 + 入边）时启用，其余零改动。
/// 预期行为：手雷为低速辅助武器（不可跑），能力差异在状态类内直接处理。
/// </summary>
public class Grenade_Normal_Ground_State : PlayerStateBase
{
    public override void Tick(PlayerContext ctx)
    {
        // 待实现（接入槽位时补齐，参考 Rifle_Normal_Ground_State 的完整实现）：
        // - 行走速度（不可跑，能力差异在状态类内处理）
        // - 转向
    }
}
