using UnityEngine;

/// <summary>
/// 子弹数据（ScriptableObject 资产）：弹道计算所需的基础物理数据。
/// 与 BulletManage 同路径；后续子弹池/物理推进时按需扩展字段。
/// </summary>
[CreateAssetMenu(fileName = "BulletData", menuName = "Weapon/Bullet Data")]
public class BulletData : ScriptableObject
{
    [Tooltip("子弹初速（m/s）：出膛速度，瞄准落点/弹道计算使用")]
    public float bulletInitialSpeed = 800f;

    [Tooltip("子弹重量（kg）：重量/动量相关计算使用（当前无阻力模型，先存档）")]
    public float bulletWeight = 0.02f;
}
