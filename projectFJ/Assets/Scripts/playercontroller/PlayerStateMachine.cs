using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 状态键（内部使用）：一个状态 = 三个正交枚举的组合（手持 × 手部操作 × 身体姿态）。
///
/// 注意：这是【内部实现】用的类型（状态机字典键、工厂映射键、转换边路由），
/// 外层调用（控制脚本、状态类）一律以三个枚举参数传参，无需构造本结构体；
/// 需要读取当前组合时用 PlayerStateMachine.CurrentKey（只读）。
/// </summary>
public struct PlayerStateKey : IEquatable<PlayerStateKey>
{
    public readonly PlayerHanding Handing;      // 手持装备
    public readonly PlayerHandPosture Hand;     // 手部操作
    public readonly PlayerBodyPosture Body;     // 身体姿态

    public PlayerStateKey(PlayerHanding handing, PlayerHandPosture hand, PlayerBodyPosture body)
    {
        Handing = handing;
        Hand = hand;
        Body = body;
    }

    public bool Equals(PlayerStateKey other)
        => Handing == other.Handing && Hand == other.Hand && Body == other.Body;

    public override bool Equals(object obj) => obj is PlayerStateKey other && Equals(other);

    public override int GetHashCode()
        => ((int)Handing << 8) | ((int)Hand << 4) | (int)Body;

    public override string ToString() => $"{Handing}_{Hand}_{Body}";
}

/// <summary>
/// 转换边（纯数据）：从 from 状态 → to 状态，满足 condition 时允许切换。
/// 公开构造直接给三枚举参数（from 三项 + to 三项），内部用 PlayerStateKey 存储与比较。
/// 触发方式三种（由 triggerSignal / evaluateEveryFrame 区分）：
/// - triggerSignal 非空 → 【信号边】：该信号置位时评估（注册顺序 = 裁决优先级，
///   例如 Jump 的"攀爬优先、跳跃兜底"就是同信号的两条边按序注册）；
/// - evaluateEveryFrame 为 true → 【持久边】：每帧评估（瞄准按住/松开这类"值条件"）；
/// - 两者皆空 → 【请求边】：由状态类在 Tick 中 RequestTransition 提议（物理条件，如落地）。
///
/// 信号消费与裁决分离约定（设计文档 §6.3）：条件只用 GetSignal 探测、不消费；
/// 信号被评估后由状态机统一消费一次（命中切换 / 未命中丢弃），避免残留或重复触发。
/// </summary>
public class TransitionEdge
{
    public readonly PlayerStateKey from;
    public readonly PlayerStateKey to;
    public readonly PlayerInputState.Signal? triggerSignal;   // 非空 = 信号边
    public readonly bool evaluateEveryFrame;                  // true = 持久边（每帧评估）
    public readonly Func<PlayerContext, bool> condition;      // null 视为恒通过
    public readonly string label;                             // 调试日志用

    /// <summary>公开构造：外层只需给三枚举（from 三项 + to 三项），无需构造键结构体。</summary>
    public TransitionEdge(
        PlayerHanding fromHanding, PlayerHandPosture fromHand, PlayerBodyPosture fromBody,
        PlayerHanding toHanding, PlayerHandPosture toHand, PlayerBodyPosture toBody,
        Func<PlayerContext, bool> condition = null,
        PlayerInputState.Signal? triggerSignal = null,
        bool evaluateEveryFrame = false,
        string label = null)
    {
        from = new PlayerStateKey(fromHanding, fromHand, fromBody);
        to = new PlayerStateKey(toHanding, toHand, toBody);
        this.condition = condition;
        this.triggerSignal = triggerSignal;
        this.evaluateEveryFrame = evaluateEveryFrame;
        this.label = label;
    }
}

/// <summary>
/// 角色状态机（扁平单机）：与无分层动画状态机同构——
/// 一个 Current（当前状态类实例）+ 一个状态字典（PlayerStateKey → 状态类）+ 一张转换边表。
///
/// 外层 API 全部以三枚举传参（handing / hand / body），不感知 PlayerStateKey 结构体；
/// PlayerStateKey 仅为内部键（字典、边路由、当前状态）。
///
/// 职责边界：
/// - 状态类：只管"本状态内的运动与操作"（Enter/Exit/Tick），经 PlayerContext 提议切换；
/// - 本机：唯一裁决与执行者——允许/禁止关系全部编码在"状态是否存在 + 边是否存在与条件"里；
/// - 非法组合（无注册状态/无边）天然不可达，无需规则表或一致性补救。
///
/// TODO(性能，暂缓)：转换边当前为单列表线性扫描（裁决 O(E)=边数；每帧固定开销 = 32 位信号
/// 扫描 + E 次边遍历）。当前 7 状态 / 15 边量级下可忽略，无需优化；若未来边数 > 50 或出现热点，
/// 按"触发类型分桶 → 按信号/from 状态索引候选"两档升级（对外接口不变，仅内部数据结构替换）。
/// </summary>
public class PlayerStateMachine
{
    #region 当前状态（三枚举可直接读取，供 PlayerControllerScript 与动画参数对齐）
    PlayerStateBase currentState;
    PlayerStateKey currentKey;

    public PlayerStateBase Current => currentState;

    /// <summary>当前状态键（只读，内部实现类型；需要组合值时也可用 Body/Hand/Handing 三枚举）。</summary>
    public PlayerStateKey CurrentKey => currentKey;

    public PlayerBodyPosture Body => currentKey.Body;
    public PlayerHandPosture Hand => currentKey.Hand;
    public PlayerHanding Handing => currentKey.Handing;
    #endregion

    readonly Dictionary<PlayerStateKey, PlayerStateBase> states = new Dictionary<PlayerStateKey, PlayerStateBase>();
    readonly List<TransitionEdge> edges = new List<TransitionEdge>();

    PlayerContext ctx;   // 本帧上下文（Tick 传入，切换时可访问）

    #region 注册（外层给三枚举；内部转 PlayerStateKey）
    /// <summary>注册状态：组合（三枚举）→ 状态类实例。重复组合覆盖并告警。</summary>
    public void RegisterState(PlayerHanding handing, PlayerHandPosture hand, PlayerBodyPosture body, PlayerStateBase state)
        => RegisterState(new PlayerStateKey(handing, hand, body), state);

    void RegisterState(PlayerStateKey key, PlayerStateBase state)
    {
        if (states.ContainsKey(key))
        {
            Debug.LogWarning($"[StateMachine] 重复注册状态 {key}，已覆盖");
        }
        states[key] = state;
    }

    /// <summary>注册转换边。同 (from, to, 触发方式) 重复注册视为配置错误（告警并忽略）。</summary>
    public void RegisterEdge(TransitionEdge edge)
    {
        foreach (var e in edges)
        {
            if (e.from.Equals(edge.from) && e.to.Equals(edge.to) && e.triggerSignal == edge.triggerSignal)
            {
                Debug.LogWarning($"[StateMachine] 重复的转换边 {e.from}→{e.to}，已忽略");
                return;
            }
        }
        edges.Add(edge);
    }
    #endregion

    #region 生命周期
    /// <summary>进入初始状态：给组合（三枚举），Start 调用一次。</summary>
    public void Enter(PlayerHanding handing, PlayerHandPosture hand, PlayerBodyPosture body, PlayerContext context)
    {
        ctx = context;
        currentKey = new PlayerStateKey(handing, hand, body);
        states.TryGetValue(currentKey, out currentState);
        currentState?.Enter(ctx);
    }

    /// <summary>
    /// 每帧调度（在 input.Capture() 之后调用）：
    /// 信号裁决 → 持久边裁决 → 当前状态 Tick。
    /// </summary>
    public void Tick(PlayerContext context)
    {
        ctx = context;
        EvaluateSignalEdges();
        EvaluatePersistentEdges();
        currentState?.Tick(ctx);
    }

    #endregion

    #region 转换裁决（唯一执行点）
    /// <summary>请求驱动：状态类提议（物理条件），给组合（三枚举）；内部精确匹配请求边。</summary>
    public bool RequestTransition(PlayerHanding handing, PlayerHandPosture hand, PlayerBodyPosture body)
        => RequestTransition(new PlayerStateKey(handing, hand, body));

    bool RequestTransition(PlayerStateKey to)
    {
        foreach (var e in edges)
        {
            if (e.triggerSignal != null || e.evaluateEveryFrame) continue;   // 只处理请求边
            if (!e.from.Equals(currentKey)) continue;
            if (!e.to.Equals(to)) continue;
            if (e.condition == null || e.condition.Invoke(ctx))
            {
                SwitchTo(e.to, e.label);
                return true;
            }
            return false;                       // 边存在但条件不满足
        }
        return false;                           // 无此边 = 非法组合（状态不可达）
    }

    void SwitchTo(PlayerStateKey to, string label = null)
    {
        // 记录"本次切换是否改变了手持（武器切换）"到共享交接槽——由新状态 Enter 读取并清除，
        // 供其启用"武器切换速度插值"（旧武器档位 → 新武器档位的时间线性过渡）。
        ctx.Motion.HandingChanged = currentKey.Handing != to.Handing;

        currentState?.Exit(ctx);
        currentKey = to;
        states.TryGetValue(to, out currentState);
        currentState?.Enter(ctx);
    }
    #endregion

    #region 信号边裁决（边条件只探测、不消费；信号统一在此消费）
    void EvaluateSignalEdges()
    {
        for (uint bit = 1; bit != 0; bit <<= 1)
        {
            var signal = (PlayerInputState.Signal)bit;
            if (!ctx.Input.GetSignal(signal)) continue;

            foreach (var e in edges)
            {
                if (e.triggerSignal != signal) continue;
                if (!e.from.Equals(currentKey)) continue;
                if (e.condition == null || e.condition.Invoke(ctx))
                {
                    SwitchTo(e.to, e.label);
                    break;
                }
            }
            ctx.Input.Consume(signal);          // 评估即消费（命中切换 / 未命中丢弃）
        }
    }

    /// <summary>持久边裁决：每帧评估（真值条件，如瞄准按住/松开）。</summary>
    void EvaluatePersistentEdges()
    {
        foreach (var e in edges)
        {
            if (!e.evaluateEveryFrame) continue;
            if (!e.from.Equals(currentKey)) continue;
            if (e.condition == null || e.condition.Invoke(ctx)) SwitchTo(e.to, e.label);
        }
    }
    #endregion
}
