using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.InputSystem;

public enum PlayerHanding
{
    Unarmed = 0,
    Rifle = 1,
    Sword = 2,
    Grenade = 3
}

public enum PlyaerHandPosture
{
    Normal = 0,
    Aiming = 1
}

public enum PlayerBodyPosture
{
    Ground = 0,
    Jumping = 1,
    Climbing = 2
}

public class PlayerControllerScript : MonoBehaviour
{
    #region Animator哈希参数
    // 垂直速度
    static readonly int AnimVerticalSpeed = Animator.StringToHash("vertical speed");
    // 水平速度
    static readonly int AnimHorizontalSpeed = Animator.StringToHash("horizontal speed");
    // 竖直速度
    static readonly int AnimFallingSpeed = Animator.StringToHash("falling speed");
    // 姿态参数
    static readonly int AnimBodyPosture = Animator.StringToHash("body posture");
    // 瞄准参数
    static readonly int AnimIsAiming = Animator.StringToHash("isAiming");
    // 手持参数
    static readonly int AnimPlyaerHanding = Animator.StringToHash("player handing");

    static readonly int AnimRightHandIKWeight = Animator.StringToHash("right hand ik weight");

    static readonly int AnimLeftHandIKWeight = Animator.StringToHash("left hand ik weight");
    static readonly int AnimRightLegIKWeight = Animator.StringToHash("right leg ik weight");
    static readonly int AnimLeftLegIKWeight = Animator.StringToHash("left leg ik weight");

    #endregion

    #region 组件引用
    Animator animator;
    CharacterController characterController;
    Transform playerTransform;
    Camera mainCamera;

    public TwoBoneIKConstraint rightHandConstraint;
    public TwoBoneIKConstraint leftHandConstraint;
    public TwoBoneIKConstraint rightLegConstraint;
    public TwoBoneIKConstraint leftLegConstraint;
    #endregion

    #region IK
    void SetIKTarget()
    {

    }

    void SetIKweight()
    {
        rightHandConstraint.weight = animator.GetFloat(AnimRightHandIKWeight);
        leftHandConstraint.weight = animator.GetFloat(AnimLeftHandIKWeight);
        rightLegConstraint.weight = animator.GetFloat(AnimRightLegIKWeight);
        leftLegConstraint.weight = animator.GetFloat(AnimLeftLegIKWeight);
    }
    #endregion

    // Start is called before the first frame update
    void Start()
    {
        animator = GetComponent<Animator>();
        characterController = GetComponent<CharacterController>();
        playerTransform = transform;
        mainCamera = Camera.main;
    }

    // Update is called once per frame
    void Update()
    {
        Move();
        Rotate();
        SetIKTarget();
        SetIKweight();
    }

    #region 位移及旋转
    void Move()
    {

    }

    void Rotate()
    {

    }
    #endregion

    #region 输入回调
    public void GetMoveInput(InputAction.CallbackContext ctx)
    {

    }
    public void GetRunInput(InputAction.CallbackContext ctx)
    {

    }
    public void GetJumpInput(InputAction.CallbackContext ctx)
    {
    }
    public void GetRifleInput(InputAction.CallbackContext ctx)
    {

    }
    public void GetSwordInput(InputAction.CallbackContext ctx)
    {
    }
    public void GetGrenadeInput(InputAction.CallbackContext ctx)
    {
    }
    public void GetFireInput(InputAction.CallbackContext ctx)
    {

    }
    public void GetAimingInput(InputAction.CallbackContext ctx)
    {
    }
    public void GetReloadInput(InputAction.CallbackContext ctx)
    {
    }

    public void GetInteractInput(InputAction.CallbackContext ctx)
    {

    }

    #endregion
}
