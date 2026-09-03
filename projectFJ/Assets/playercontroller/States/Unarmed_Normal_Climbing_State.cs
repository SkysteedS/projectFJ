using UnityEngine;

/// <summary>
/// 具体状态：空手（Unarmed）× 正常手部（Normal）× 攀爬（Climbing）。
/// 行为取自 test.cs 攀爬段（保留项）：
/// - 进入：Jump 信号边（信号裁决先于本状态，条件 = 墙面命中 + 速度朝墙，见 PlayerControllerScript）；
/// - 每帧重测墙面：成功则更新墙面法线/锚点；连续失败 climbExitMissFrames 帧 → 提议回地面（物理驱逐）；
/// - 退出输入：QuitClimb 信号边（按 X 退回默认姿态）；
/// - 朝墙：身体平面垂直于墙面法线（面向墙），RotateTowards 插值；
/// - 位移：输入映射到墙面切平面（W=角色上方、S=下方、D=右、A=左）× climbSpeed，
///   沿法线贴墙收敛（保持 climbWallHugDistance），无重力——全部脚本接管（OnAnimatorMove）；
/// - 动画参数：本类只写速度分量（攀爬树不用，写 0）；body/hand/handing 由主体类同步（Body=Climbing）。
/// 手部 IK（test.cs 的头部相对偏移 + 墙面投影姿态算法）暂缓实现（需大改，见计划）。
/// </summary>
public class Unarmed_Normal_Climbing_State : PlayerStateBase
{
    #region 状态内结构常量（语义见注释；可调参数见 PlayerMotionValues）
    /// <summary>墙面法线平方长度下限（无效法线判定）。</summary>
    const float MinNormalSqr = 0.0001f;
    /// <summary>攀爬树不使用水平/垂直速度参数时的分量值。</summary>
    static readonly float IdleSpeedParam = 0f;
    #endregion

    Vector3 wallNormal;   // 最近一次检测到的墙面法线（朝向角色）
    Vector3 wallPoint;    // 最近一次检测到的墙面锚点（命中点平均）
    int wallMissFrames;   // 墙面检测连续失败帧数

    public override void Enter(PlayerContext ctx)
    {
        // 进入即重测一次（信号边刚用同参数探测通过，一般直接命中）；失败时按"立即计入退出"处理（防御）
        if (ctx.WallProbe.Evaluate(ctx.Transform, ctx.Transform.forward, out wallNormal, out wallPoint))
        {
            wallMissFrames = 0;
        }
        else
        {
            wallMissFrames = ctx.Values.climbExitMissFrames;
        }

        // 攀爬不应用重力：垂直速度清 0（水平速度语义在攀爬期间无效，一并清 0，退出后由地面状态接管）
        var velocity = ctx.Motion.Velocity;
        velocity.x = 0f;
        velocity.z = 0f;
        velocity.y = 0f;
        ctx.Motion.Velocity = velocity;
    }

    public override void Tick(PlayerContext ctx)
    {
        RotateTowardWall(ctx);

        // 每帧重测墙面：成功则更新墙面平面与去抖计数；失败保留最近有效墙面（瞬时断测不抖动），
        // 连续失败 climbExitMissFrames 帧后提议回地面（请求边裁决，见 PlayerControllerScript）
        if (ctx.WallProbe.Evaluate(ctx.Transform, ctx.Transform.forward, out Vector3 normal, out Vector3 point))
        {
            wallNormal = normal;
            wallPoint = point;
            wallMissFrames = 0;
        }
        else
        {
            wallMissFrames++;
            if (wallMissFrames >= ctx.Values.climbExitMissFrames)
            {
                ctx.RequestTransition(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground);
                return;
            }
        }

        WriteAnimatorParams(ctx);
    }

    public override void OnAnimatorMove(PlayerContext ctx)
    {
        // 攀爬位移全量脚本接管：输入映射到墙面切平面 + 贴墙收敛（test.cs 同款）
        if (wallNormal.sqrMagnitude < MinNormalSqr)
        {
            return;
        }

        Vector3 delta = ComputeClimbDelta(ctx);
        delta += ComputeWallHugDelta(ctx);
        ctx.CharacterController.Move(delta);
    }

    /// <summary>墙面切平面位移：输入映射到墙上坐标（W=上、S=下、D=右、A=左）。</summary>
    Vector3 ComputeClimbDelta(PlayerContext ctx)
    {
        Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, wallNormal).normalized;
        Vector3 forward = -wallNormal;
        Vector3 wallRight = Vector3.Cross(wallUp, forward).normalized;

        Vector3 moveDir = wallUp * ctx.Input.Move.y + wallRight * ctx.Input.Move.x;
        return moveDir * ctx.Values.climbSpeed * Time.deltaTime;
    }

    /// <summary>沿法线收敛到目标贴墙距离（让手部潜在 IK 目标落在臂展内）。</summary>
    Vector3 ComputeWallHugDelta(PlayerContext ctx)
    {
        float distance = Vector3.Dot(ctx.Transform.position - wallPoint, wallNormal);
        float correction = ctx.Values.climbWallHugDistance - distance;
        return wallNormal * correction * Mathf.Clamp01(ctx.Values.climbWallHugSpeed * Time.deltaTime);
    }

    /// <summary>朝向墙面：身体平面垂直于墙面法线（面向墙），up 沿墙面切平面。</summary>
    void RotateTowardWall(PlayerContext ctx)
    {
        if (wallNormal.sqrMagnitude < MinNormalSqr) return;

        Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, wallNormal).normalized;
        Quaternion target = Quaternion.LookRotation(-wallNormal, wallUp);
        ctx.Transform.rotation = Quaternion.RotateTowards(ctx.Transform.rotation, target,
                                                          ctx.Values.rotateSpeed * Time.deltaTime);
    }

    void WriteAnimatorParams(PlayerContext ctx)
    {
        var anim = ctx.Animator;
        // 攀爬动画分支不消费水平/垂直速度参数（body posture = 2 驱动），写 0
        anim.SetFloat(PlayerControllerScript.AnimVerticalSpeed, IdleSpeedParam);
        anim.SetFloat(PlayerControllerScript.AnimHorizontalSpeed, IdleSpeedParam);
        anim.SetFloat(PlayerControllerScript.AnimFallingSpeed, IdleSpeedParam);
    }
}
