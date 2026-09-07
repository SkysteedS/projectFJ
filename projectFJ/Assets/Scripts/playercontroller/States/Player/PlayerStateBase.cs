/// <summary>
/// 状态契约（扁平状态机继承链的 Player 层）：所有状态类实现统一的进入/退出/每帧/位移接管四件事。
/// 状态类的"身份"由注册时给定的组合（三枚举）确定，状态类自身不声明；
/// 工厂（PlayerStateFactory）负责"组合 → 状态类"的映射。
///
/// - Enter/Exit：状态级副作用，切换顺序由 PlayerStateMachine 保证"旧.Exit → 新.Enter"串行化；
/// - Tick：本状态内的运动与操作管理（读输入 → 计算 → 写动画参数/发起提议）；
/// - OnAnimatorMove：本状态的位移接管策略，由主体类委托。
///
/// 继承链分层（见 player-controller-design.md §6.7）：
/// Player 层（本类）→ BodyPosture 族基类（Ground/Jumping/Climbing/TopOut）→
/// HandPosture 手部变体基类（Normal/Aiming）→ Handing 具体状态类（叶子）。
/// 归属原则：只被一个族/变体使用的内容下放到对应层；本层只保留契约与
/// 真正跨 ≥2 个 Body 族的纯工具（如 WriteNonAimSpeedParams）。
/// 相机轴/移动方向换算已下沉至 GroundStateBase，不再放本层。
/// </summary>
public abstract class PlayerStateBase
{
    public virtual void Enter(PlayerContext ctx) { }

    public virtual void Exit(PlayerContext ctx) { }

    /// <summary>本状态每帧逻辑：读输入 → 运动/操作管理 → 必要时请求转换。</summary>
    public abstract void Tick(PlayerContext ctx);

    /// <summary>本状态的位移接管（动画根运动/手写位移），默认不接管；由主体类委托。</summary>
    public virtual void OnAnimatorMove(PlayerContext ctx) { }

    #region 跨族计算辅助（protected static：仅供状态类继承链使用，不放入共享上下文）
    /// <summary>
    /// 非瞄准三参数写入（vertical/horizontal/falling）：Normal×Ground 与 Jumping 两族共用，
    /// 故保留在本层；horizontal 固定为 nonAimLateralSpeed（非瞄准左右轴无侧移分量）。
    /// </summary>
    protected static void WriteNonAimSpeedParams(PlayerContext ctx, float forwardSpeed, float fallingSpeed)
    {
        var anim = ctx.AnimParams;   // 值缓存写入器：同值跳过 SetFloat
        anim.SetFloat(PlayerControllerScript.AnimVerticalSpeed, forwardSpeed);
        anim.SetFloat(PlayerControllerScript.AnimHorizontalSpeed, ctx.Values.nonAimLateralSpeed);
        anim.SetFloat(PlayerControllerScript.AnimFallingSpeed, fallingSpeed);
    }
    #endregion
}
