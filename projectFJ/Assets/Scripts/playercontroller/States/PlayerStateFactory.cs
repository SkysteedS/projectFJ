using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 状态工厂：按"状态组合（三枚举：手持 × 手部操作 × 身体姿态）"创建对应的状态类实例。
///
/// 外层调用（控制脚本）直接给三个枚举参数，无需构造任何键类型；
/// PlayerStateKey 仅为内部映射键（本工厂与状态机内部使用）。
/// 给一个组合，工厂返回对应状态类实例（或 null = 该组合没有对应状态，即不可达）。
///
/// 新增状态 = 建状态类 + 本工厂加一行映射；其余零改动。
/// </summary>
public static class PlayerStateFactory
{
    /// <summary>合法状态组合 → 创建函数（延迟构造：只有创建时才 new，不预分配）。内部键用 PlayerStateKey。</summary>
    static readonly Dictionary<PlayerStateKey, Func<PlayerStateBase>> Creators =
        new Dictionary<PlayerStateKey, Func<PlayerStateBase>>
        {
            // 地面 × 正常手部 × 四装备
            { new PlayerStateKey(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground), () => new Unarmed_Normal_Ground_State() },
            { new PlayerStateKey(PlayerHanding.Rifle,   PlayerHandPosture.Normal, PlayerBodyPosture.Ground), () => new Rifle_Normal_Ground_State() },
            { new PlayerStateKey(PlayerHanding.Sword,   PlayerHandPosture.Normal, PlayerBodyPosture.Ground), () => new Sword_Normal_Ground_State() },
            { new PlayerStateKey(PlayerHanding.Grenade, PlayerHandPosture.Normal, PlayerBodyPosture.Ground), () => new Grenade_Normal_Ground_State() },
            // 地面 × 瞄准 × 步枪
            { new PlayerStateKey(PlayerHanding.Rifle,   PlayerHandPosture.Aiming, PlayerBodyPosture.Ground), () => new Rifle_Aiming_Ground_State() },
            // 滞空 / 攀爬 × 正常手部 × 空手
            { new PlayerStateKey(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping), () => new Unarmed_Normal_Jumping_State() },
            { new PlayerStateKey(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing), () => new Unarmed_Normal_Climbing_State() },
            // 滞空 × 正常手部 × 步枪
            { new PlayerStateKey(PlayerHanding.Rifle,   PlayerHandPosture.Normal, PlayerBodyPosture.Jumping), () => new Rifle_Normal_Jumping_State() },
        };

    /// <summary>按组合（三枚举）创建状态类实例；未注册的组合（非法/未实现）返回 null 并告警。</summary>
    public static PlayerStateBase Create(PlayerHanding handing, PlayerHandPosture hand, PlayerBodyPosture body)
    {
        var key = new PlayerStateKey(handing, hand, body);
        if (Creators.TryGetValue(key, out var creator))
        {
            return creator.Invoke();
        }
        Debug.LogWarning($"[StateFactory] 未注册的状态组合 {key}，返回 null（该组合不可达）");
        return null;
    }
}
