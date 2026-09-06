using UnityEngine;

/// <summary>
/// 子弹管理（架子）：当前仅持有子弹数据资产引用，供瞄准状态计算弹道落点；
/// 子弹池等后续模块接入时再扩展，本类暂不承载逻辑。
/// </summary>
public class BulletManage : MonoBehaviour
{
    [Tooltip("子弹数据资产（初速/重量），同路径 BulletData")]
    public BulletData bulletData;
}
