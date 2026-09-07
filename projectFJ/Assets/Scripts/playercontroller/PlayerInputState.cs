using UnityEngine;

/// <summary>
/// 输入状态（帧快照）——纯数据层：只记录"玩家按了什么"，不含任何业务规则。
///
/// 设计约定（详见 player-controller-design.md §4）：
/// 1. 实时写入层（live*）：由输入适配器（InputActionBridge / 主体类回调）随时写入，
///    触发时机与 Update 无关（Input System 回调在帧间任意时刻触发）。
/// 2. 帧快照层（frame*）：主体类每帧帧首调用一次 <see cref="Capture"/>，把实时层滚动
///    为本帧快照；逻辑只读帧层，保证帧内所有消费者看到一致的输入视图。
/// 3. 按下型输入以【边沿信号】提供（Signal 位掩码）：Started 置位、跨帧保留，直到被
///    Capture 滚动、再被 Consume 清除；抬起（Canceled）不清除信号，避免帧间快速点击丢失。
/// 4. 本类不引用 UnityEngine.InputSystem，可脱离引擎单元测试；
///    InputSystem 适配集中在同文件的 <see cref="InputActionBridge"/>。
/// 5. 仅帧快照层字段标 [SerializeField]，用于 Inspector 调试可视化（Play 模式观察
///    "本帧正在处理的数据"）；实时层为纯运行时数据不序列化。接口仍为
///    "私有字段 + 只读属性"，写入仅经 Set*/Press。
/// </summary>
[System.Serializable]
public class PlayerInputState
{
    /// <summary>按下型输入（边沿信号）。</summary>
    [System.Flags]
    public enum Signal : uint
    {
        None        = 0,
        Jump        = 1 << 0,  // 跳跃（Space）：统一的"移动动作"请求，可攀爬检测优先（命中→攀爬，未命中→跳跃），不设独立攀爬输入
        FirePressed = 1 << 1,  // 开火：扣下扳机的瞬间（配合 FireHeld 区分单发/连发）
        Reload      = 1 << 2,  // 换弹（R）
        Interact    = 1 << 3,  // 交互（F，action 名拼写为 Interacct）
        SlotRifle   = 1 << 4,  // 切换到步枪槽位（1）
        SlotPistol  = 1 << 5,  // 切换到手枪槽位（2）
        SlotGrenade = 1 << 6,  // 切换到手雷槽位（3）
        QuitClimb   = 1 << 7,  // 退出攀爬（X）：攀爬中按下退回默认姿态
        AimPressed  = 1 << 8,  // 瞄准按下边沿：供"切换瞄准"模式使用（按住模式由 frameAim 持久值驱动）
    }

    #region 实时层（适配器写入；纯运行时数据，不参与序列化，Inspector 不可见）
    Vector2 liveMove;      // WASD
    Vector2 liveLook;      // 鼠标增量（透传，语义由相机系统决定）
    bool    liveRun;       // 按住 Shift
    bool    liveAim;       // 按住右键
    bool    liveFireHeld;  // 按住左键
    uint    liveSignals;   // 按下型信号累积位
    #endregion

    #region 帧快照层（逻辑只读；[SerializeField] 仅用于调试可视化，Play 模式观察"本帧正在处理的数据"）
    [Header("本帧处理中的输入（帧快照）")]
    [SerializeField] Vector2 frameMove;
    [SerializeField] Vector2 frameLook;
    [SerializeField] bool frameRun;
    [SerializeField] bool frameAim;
    [SerializeField] bool frameFireHeld;
    [Tooltip("位掩码：1=Jump 2=FirePressed 4=Reload 8=Interact 16=SlotRifle 32=SlotPistol 64=SlotGrenade 128=QuitClimb 256=AimPressed")]
    [SerializeField] uint frameSignals;
    #endregion

    #region 帧层只读访问（逻辑消费，禁止写入）
    public Vector2 Move => frameMove;
    public Vector2 Look => frameLook;
    public bool Run => frameRun;
    public bool Aim => frameAim;
    public bool FireHeld => frameFireHeld;

    /// <summary>查询本帧是否存在该信号（不消费）。</summary>
    public bool GetSignal(Signal signal) => (frameSignals & (uint)signal) != 0;

    /// <summary>查询并消费：读本帧信号并清除，一次性事件（如跳跃）只响应一次。</summary>
    public bool Consume(Signal signal)
    {
        uint bit = (uint)signal;
        if ((frameSignals & bit) == 0) return false;
        frameSignals &= ~bit;
        return true;
    }
    #endregion

    #region 实时写入（仅 InputActionBridge / 主体类回调调用）
    public void SetMove(Vector2 value) => liveMove = value;
    public void SetLook(Vector2 value) => liveLook = value;
    public void SetRun(bool value) => liveRun = value;
    public void SetAim(bool value) => liveAim = value;
    public void SetFireHeld(bool value) => liveFireHeld = value;

    /// <summary>置位信号（Started 时调用；同帧重复调用幂等）。</summary>
    public void Press(Signal signal) => liveSignals |= (uint)signal;
    #endregion

    #region 帧快照
    /// <summary>
    /// 每帧帧首调用一次：把实时层滚动为帧快照，并清空实时信号。
    /// 调用点必须在所有输入消费逻辑之前（主体类 Update 首行）。
    /// </summary>
    public void Capture()
    {
        frameMove     = liveMove;
        frameLook     = liveLook;
        frameRun      = liveRun;
        frameAim      = liveAim;
        frameFireHeld = liveFireHeld;
        frameSignals  = liveSignals;
        liveSignals   = 0;
    }
    #endregion
}

/// <summary>
/// Input System ↔ 输入状态的桥接适配层：把 CallbackContext 翻译为域值写入
/// <see cref="PlayerInputState"/>。这是全部输入代码中唯一允许使用
/// UnityEngine.InputSystem 的地方；主体类 PlayerControllerScript 的公开回调
/// 转发到此处即可。动作名与 PlayDefaultInputAction.inputactions 一一对应。
/// </summary>
public static class InputActionBridge
{
    public static void OnMove(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => input.SetMove(ctx.ReadValue<Vector2>());

    public static void OnLook(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => input.SetLook(ctx.ReadValue<Vector2>());

    public static void OnRun(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => input.SetRun(ReadButton(ctx));

    /// <summary>瞄准：按住状态 + 按下边沿信号（按住模式用持续值；切换模式消费按下边沿切换瞄准）。</summary>
    public static void OnAim(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
    {
        input.SetAim(ReadButton(ctx));
        PressOnStarted(ctx, input, PlayerInputState.Signal.AimPressed);
    }

    /// <summary>开火：按住状态 + 按下边沿信号（单发武器消费信号，连发武器读 FireHeld）。</summary>
    public static void OnFire(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
    {
        input.SetFireHeld(ReadButton(ctx));
        PressOnStarted(ctx, input, PlayerInputState.Signal.FirePressed);
    }

    public static void OnJump(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => PressOnStarted(ctx, input, PlayerInputState.Signal.Jump);

    public static void OnRifle(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => PressOnStarted(ctx, input, PlayerInputState.Signal.SlotRifle);

    public static void OnPistol(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => PressOnStarted(ctx, input, PlayerInputState.Signal.SlotPistol);

    public static void OnGrenade(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => PressOnStarted(ctx, input, PlayerInputState.Signal.SlotGrenade);

    public static void OnReload(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => PressOnStarted(ctx, input, PlayerInputState.Signal.Reload);

    public static void OnInteract(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => PressOnStarted(ctx, input, PlayerInputState.Signal.Interact);

    public static void OnQuitClimb(PlayerInputState input, UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => PressOnStarted(ctx, input, PlayerInputState.Signal.QuitClimb);

    static bool ReadButton(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        => ctx.ReadValue<float>() > 0f;

    static void PressOnStarted(UnityEngine.InputSystem.InputAction.CallbackContext ctx, PlayerInputState input, PlayerInputState.Signal signal)
    {
        if (ctx.action.phase == UnityEngine.InputSystem.InputActionPhase.Started)
        {
            input.Press(signal);
        }
    }
}
