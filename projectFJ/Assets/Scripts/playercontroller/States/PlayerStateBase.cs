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
///   各具体状态类已实现完整运动逻辑（速度平滑/转向/重力），数值参数见 PlayerMotionValuesSO。
///
/// 共享原则：PlayerContext 是角色各系统的共享引用集合（状态类的操作通道）——状态类经它
/// 读共享信息、写运动状态、调用/改写组件（位移/旋转/IK target/动画参数）；
/// 仅少数成员有意只读（输入帧快照、当前状态描述、物理事实），其撰写通道在机器/主体。
/// 行为计算（相机轴换算等）由状态类（本基类的 protected 辅助）自行处理。
/// </summary>
public abstract class PlayerStateBase
{
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
            return f.sqrMagnitude > ctx.Values.minMoveSqrMagnitude ? f.normalized : Vector3.forward;
        }
        return Vector3.forward;
    }

    /// <summary>摄像机水平右轴（投影到 XZ 平面并归一化；缺主摄像机时回退世界 right）。</summary>
    protected static Vector3 CameraRight(PlayerContext ctx)
    {
        if (ctx.MainCamera != null)
        {
            var r = Vector3.ProjectOnPlane(ctx.MainCamera.transform.right, Vector3.up);
            return r.sqrMagnitude > ctx.Values.minMoveSqrMagnitude ? r.normalized : Vector3.right;
        }
        return Vector3.right;
    }

    /// <summary>WASD 输入换算为相机基准的世界方向（非瞄准移动与滞空操控共用）。</summary>
    protected static Vector3 CameraSpaceMoveDir(PlayerContext ctx)
        => (CameraForward(ctx) * ctx.Input.Move.y + CameraRight(ctx) * ctx.Input.Move.x).normalized;

    /// <summary>
    /// 移动方向（含"松开输入后的平滑衰减"语义）：
    /// 有输入时取相机轴输入方向并记录到 Motion.HorizontalDir；无输入时沿用最后有效方向。
    /// 这样速度标量（Motion.HorizontalSpeed）在松开输入后仍按加速率插值衰减，
    /// 不会被"方向归零 → 速度清零"打断——动画水平速度参数随插值平滑归零，而非一帧跳零。
    /// </summary>
    protected static Vector3 MoveDirection(PlayerContext ctx)
    {
        if (ctx.Input.Move.sqrMagnitude > ctx.Values.minMoveSqrMagnitude)
        {
            Vector3 dir = CameraSpaceMoveDir(ctx);
            ctx.Motion.HorizontalDir = dir;
            return dir;
        }
        return ctx.Motion.HorizontalDir;
    }
    #endregion
}
