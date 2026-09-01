using UnityEngine;

/// <summary>
/// 状态契约（扁平状态机的状态基类）：所有状态类实现统一的进入/退出/每帧/位移接管四件事。
/// 状态类的"身份"由注册时给定的组合（三枚举）确定，状态类自身不声明；
/// 工厂（PlayerStateFactory）负责"组合 → 状态类"的映射。
///
/// - Enter/Exit：状态级副作用（如攀爬停用 hand ik 层、落地捕获下落速度），
///   切换顺序由 PlayerStateMachine 保证"旧.Exit → 新.Enter"串行化；
/// - Tick：本状态内的运动与操作管理（读输入 → 计算 → 写动画参数/发起提议）；
/// - OnAnimatorMove：本状态的位移接管策略（根运动 / 脚本接管），由主体类委托。
///   当前为骨架：真实运动逻辑在后续 Phase 中各状态类内实现。
///
/// 共享原则：PlayerContext 只放"跨状态共享的信息"（组件/输入/当前状态/物理标志），
/// 行为与计算（相机轴换算等）由状态类（本基类的 protected 辅助）自行处理。
/// </summary>
public abstract class PlayerStateBase
{
    /// <summary>走廊状态标记（有明确终点的临时姿态，如登顶/翻越）；预留缺口，本阶段不启用。</summary>
    public virtual bool IsTransient => false;

    public virtual void Enter(PlayerContext ctx) { }

    public virtual void Exit(PlayerContext ctx) { }

    /// <summary>本状态每帧逻辑：读输入 → 运动/操作管理 → 必要时请求转换。</summary>
    public abstract void Tick(PlayerContext ctx);

    /// <summary>本状态的位移接管（动画根运动/手写位移），默认不接管；由主体类委托。</summary>
    public virtual void OnAnimatorMove(PlayerContext ctx) { }

    #region 状态层计算辅助（protected：仅供状态类使用，不放入共享上下文）
    /// <summary>摄像机水平前轴（投影到 XZ 平面并归一化；缺主摄像机时回退世界 forward）。</summary>
    protected static Vector3 CameraForward(PlayerContext ctx)
    {
        if (ctx.MainCamera != null)
        {
            var f = Vector3.ProjectOnPlane(ctx.MainCamera.transform.forward, Vector3.up);
            return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
        }
        return Vector3.forward;
    }

    /// <summary>摄像机水平右轴（投影到 XZ 平面并归一化；缺主摄像机时回退世界 right）。</summary>
    protected static Vector3 CameraRight(PlayerContext ctx)
    {
        if (ctx.MainCamera != null)
        {
            var r = Vector3.ProjectOnPlane(ctx.MainCamera.transform.right, Vector3.up);
            return r.sqrMagnitude > 0.0001f ? r.normalized : Vector3.right;
        }
        return Vector3.right;
    }

    /// <summary>WASD 输入换算为相机基准的世界方向（非瞄准移动与滞空操控共用）。</summary>
    protected static Vector3 CameraSpaceMoveDir(PlayerContext ctx)
        => (CameraForward(ctx) * ctx.Input.Move.y + CameraRight(ctx) * ctx.Input.Move.x).normalized;
    #endregion
}
