/// <summary>
/// 具体状态：空手（Unarmed）× 正常手部（Normal）× 滞空（Jumping）。
/// 落地检测由本类提议（RequestTransition 回 Ground），条件由请求边裁决。
/// </summary>
public class Unarmed_Normal_Jumping_State : PlayerStateBase
{
    public override void Tick(PlayerContext ctx)
    {
        // 骨架：每帧提议回到地面（Phase 2 补充垂直速度 <= 0 条件防起跳瞬间误判）
        ctx.RequestTransition(PlayerHanding.Unarmed, PlyaerHandPosture.Normal, PlayerBodyPosture.Ground);

        // Phase 2 后续：
        // - 水平操控（起跳方向速度接管，替换 animator 根运动水平分量）
        // - 垂直位移（手写重力，MotionState）
        // - 落地时的捕获/重置（Enter/Exit 副作用）
    }
}
