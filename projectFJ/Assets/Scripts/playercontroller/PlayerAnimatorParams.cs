using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Animator 参数的【值缓存写入器】：只把"发生变化"的值写进 Animator，
/// 相同值直接跳过 Set*（避免每帧对常量参数（如 0）重复触发 Animator 参数更新开销）。
///
/// 语义约定：
/// - 这些参数（vertical/horizontal/falling speed、body/hand/handing 等）由脚本权威驱动，
///   与动画层曲线控制的 IK 权重参数无关（曲线控制的参数脚本一律不写，见架构约定）；
/// - 缓存为每角色实例持有（挂在 PlayerContext），同一参数在脚本不同写入点间共享
///   最近值——状态切换后首次写入若与上次同值也会跳过（同值无副作用，跳过正确）。
/// - 供状态类与主体类的 Animator 参数写入统一走这里，避免各状态类各自维护缓存。
/// </summary>
public class PlayerAnimatorParams
{
    readonly Animator animator;
    readonly Dictionary<int, float> lastFloat = new Dictionary<int, float>();
    readonly Dictionary<int, int> lastInt = new Dictionary<int, int>();

    public PlayerAnimatorParams(Animator animator)
    {
        this.animator = animator;
    }

    /// <summary>仅在值与上次写入不同时 SetFloat（含首次必写）。</summary>
    public void SetFloat(int id, float value)
    {
        if (lastFloat.TryGetValue(id, out float last) && last == value) return;
        animator.SetFloat(id, value);
        lastFloat[id] = value;
    }

    /// <summary>仅在值与上次写入不同时 SetInteger（含首次必写）。</summary>
    public void SetInteger(int id, int value)
    {
        if (lastInt.TryGetValue(id, out int last) && last == value) return;
        animator.SetInteger(id, value);
        lastInt[id] = value;
    }
}
