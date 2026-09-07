# 角色控制器架构设计文档（PlayerControllerScript）

> 面向 `Assets/Scripts/playercontroller/` 的正式设计。本文档只描述**当前生效**的架构与结构；
> 历史原型与中间方案不保留（git 历史可查）。状态标记：`[已实现]` = 已落地并完成行为检查；
> `[部分实现]` / `[规划中]` = 进行中或待启动。

---

## 1. 目标与职责边界

- 输入层与运动/姿态解耦：输入层是纯数据帧快照，业务规则在状态类与机器。
- 姿态组合按“状态模式 + 组合状态”管理：`PlayerStateMachine` 是切换裁决与执行的唯一入口。
- 各组合行为按继承链分层实现（Player → BodyPosture → HandPosture → Handing），
  Handing（手持）差异只落在叶子，见 §6.7。
- 行为合法性由“状态/边是否存在”编码：非法组合天然不可达，无运行期规则表。
- 工程约束：帧级零托管分配、数值管理走 ScriptableObject 资产（见 code-conventions.md）。

## 2. 决策记录（当前生效）

| 决策点 | 结论 |
|---|---|
| 正交轴与组合状态 | 身体姿态 / 手持类别 / 手部操作是正交“状态描述”，内部用 `PlayerStateKey` 绑定为组合状态；状态机为扁平单机，不做嵌套状态机 |
| 行为合法性 | 由“状态/边是否存在”编码；无运行期规则表、无能力许可表 |
| 转换表 | 一张转换边列表：信号边（注册顺序 = 裁决优先级）、持久边（每帧评估）、请求边（状态提议）；信号消费与裁决分离 |
| 攀爬触发 | 无独立攀爬输入：Jump 是统一“移动动作”，可攀爬检测优先、跳跃兜底；滞空期由持久边补充空中抓墙 |
| 走廊状态 | 登顶已按走廊族落地（`ClimbTopOutStateBase → NormalClimbTopOutStateBase → Handing 叶子`）；`IsTransient` 瞬时标记尚未引入，需要时再引入 |
| 状态类实现结构 | C# 单继承不支持类 DAG，采用**线性化四层继承**：Player / BodyPosture / HandPosture / Handing；所有使用中的组合状态类一律在 Handing 层（§6.7） |
| IK target 写入权威 | 脚本唯一权威。地面态不做“Enter 记录 / Exit 还原”（场景默认位无主，还原等于写回调试位）；瞄准态内部“枪根父级 + 腕-枪相对位姿”记录-恢复仍然有效（§6.8） |

## 3. 总体架构

```text
┌────────────────────────────────────────────────────────────┐
│ 主体层 PlayerControllerScript（组合器 + 唯一裁决者）          │
│  组件持有 · 输入桥接 · 状态/边注册 · 动画参数同步 · IK 权重回写 │
├────────────────────────────────────────────────────────────┤
│ 状态机 PlayerStateMachine（扁平单机）                        │
│  PlayerStateKey 组合键 + 状态字典 + 转换边表（裁决唯一执行点）  │
├────────────────────────────────────────────────────────────┤
│ 状态类实现（Player → BodyPosture → HandPosture → Handing     │
│  线性化继承链，仅内部实现复用，见 §6.7）                      │
├────────────────────────────────────────────────────────────┤
│ 数值层 PlayerMotionValuesSO（档位/重力/纯计算，资产级单例）   │
├────────────────────────────────────────────────────────────┤
│ 输入层 PlayerInputState（纯数据帧快照）+ InputActionBridge   │
└────────────────────────────────────────────────────────────┘
```

三个正交轴（身体姿态 / 手持类别 / 手部操作）保留为状态描述，组合键 `PlayerStateKey` 在状态机内部
绑定三者。轴间交叉约束（如持械不能攀爬、瞄准中不能切枪）由“状态/边是否存在”天然编码，
非法组合无节点/无边即不可达（见 §6.4）。

## 4. 输入层（[已实现]）

- 文件：`Assets/Scripts/playercontroller/PlayerInputState.cs`
- 边界：只记录“玩家按了什么”，不含业务规则、不引用 `UnityEngine.InputSystem`；
  InputSystem 适配集中在同文件 `InputActionBridge`。

### 4.1 两类输入模型

| 类型 | 语义 | 示例 |
|---|---|---|
| 连续输入 | 帧快照滚动值，逻辑读当前值 | Move / Look / Run / Aim / FireHeld |
| 边沿信号 | Started 置位、跨帧保留、消费即清 | Jump / FirePressed / Reload / Interact / SlotRifle / SlotPistol / SlotGrenade / QuitClimb / AimPressed |

> Jump 语义：统一“移动动作”请求，攀爬检测优先（命中 → 进攀爬），未命中 → 跳跃；
> 滞空期另有持久边做空中抓墙（上升/下降段均可），无需再次按键。

### 4.2 帧快照语义

```text
输入系统回调 ──实时写入层──▶ Capture()（每帧帧首一次）──▶ 帧快照层（逻辑只读）
```

- 实时层随时写入；`Capture()` 把实时层滚动为帧快照并清空实时信号。
- 信号在 Capture 后才被清空：一次完整点击必然进入下一帧快照，不丢输入。
- `Consume` 读并清；`ClearSignals` 供状态切换丢弃残留。
- `Look` 透传原始增量，语义由相机系统决定（见 §10）。

### 4.3 与 PlayDefaultInputAction.inputactions 的映射

| Input Action | 类型 | → 写入 |
|---|---|---|
| Move | Value Vector2 | SetMove |
| Look | Value Vector2 | SetLook |
| Run | Button | SetRun |
| Aiming | Button | SetAim + Press(AimPressed) |
| Fire | Button | SetFireHeld + Press(FirePressed) |
| Jump | Button | Press(Jump) |
| Rifle / Pistol / HandGrenade | Button | Press(SlotRifle/Pistol/Grenade) |
| Reload | Button | Press(Reload) |
| Interacct | Button | Press(Interact)（action 名拼写为 Interacct） |
| QuitClimb | Button | Press(QuitClimb) |

> 无独立 Climb 输入；inputactions 无需新增 action。

### 4.4 手持当前值归属

手持（handing）是“游戏状态”而非输入：输入层只提供切槽位信号；当前手持值由机器按当前组合
持有，并作为 Handing 轴数据查询的输入（数值层档位查询、IK 锚点归属等，见 §6.6/§6.7）。

## 5. 数值层与共享交接（[已实现]）

1. **共享运动描述 `PlayerMotion`**：跨状态交接槽（挂在 `PlayerContext.Motion`）。
   当前状态类每次计算完毕写入（速度向量），新状态 `Enter` 读取初值。纯数据，不含计算。
2. **运动数值服务 `PlayerMotionValuesSO`**（资产级单例，组件只持引用）：
   物理常量（重力/档位/加速度/旋转速度/跳跃高度/攀爬/登顶/死区）、速度平滑（MoveTowards）、
   重力累积、跳跃初速度反推、按 Handing 的档位查询（`GroundTargetSpeed` / `MoveGroundSpeed`，
   为唯一数据索引分支，非姿态判定）、`MoveAimSpeed`（瞄准锁定行走档）等纯计算。
3. **Animator 参数缓存写入器 `PlayerAnimatorParams`**：同值跳过 `Set*`
   （vertical/horizontal/falling speed、body/hand/handing 由脚本权威驱动；IK 权重参数由动画曲线
   控制，脚本只读不回写参数）。

- 运动交接约定：**写入 = 当前状态类**（Tick/OnAnimatorMove 末尾），**读取 = 新状态**（Enter）。
- 姿态检测/切换决策不在数值层；计算结果写回 `PlayerContext.Motion`。

## 6. 状态机与状态类

### 6.1 扁平单机结构

与无分层动画状态机同构：一个 `Current`（当前状态实例）+ 状态字典（组合键 → 状态实例）
+ 一张转换边表。`PlayerStateKey` 仅内部使用；外层调用一律给三枚举参数。

```text
PlayerBodyPosture(Ground/Jumping/Climbing/ClimbTopOut)
PlayerHandPosture(Normal/Aiming)
PlayerHanding(Unarmed/Rifle/Pistol/Grenade)

PlayerStateKey（内部键，外层不可见）：当前合法组合（状态/边注册推导）：
  Unarmed×Normal×Ground/Jumping/Climbing/ClimbTopOut
  Rifle×Normal×Ground/Jumping / Rifle×Aiming×Ground
  Pistol×Normal×Ground/Jumping / Pistol×Aiming×Ground
  Grenade×Normal×Ground/Jumping / Grenade×Aiming×Ground
  └─ 非法组合（持枪攀爬、瞄准滞空/攀爬…）无状态类/无转换边 → 天然不可达

PlayerStateFactory：Create(三枚举) → 状态实例（创建清单唯一集中处）
PlayerStateMachine：
  Current / states / edges —— 机器为裁决与执行的唯一执行点
```

### 6.2 状态契约与上下文

```csharp
abstract class PlayerStateBase
{
    public virtual  void Enter(PlayerContext ctx);       // 进入副作用
    public virtual  void Exit(PlayerContext ctx);        // 退出清理
    public abstract void Tick(PlayerContext ctx);        // 每帧运动/操作
    public virtual  void OnAnimatorMove(PlayerContext ctx); // 位移接管（默认空）
}

class PlayerContext   // 共享引用集合（状态类操作通道）
{
    Animator / CharacterController / Transform / Camera / IK 约束引用;
    PlayerAnimatorParams AnimParams;   // 值缓存写入器
    PlayerInputState Input;            // 帧快照（只消费）
    PlayerMotion Motion;               // 共享运动交接槽
    PlayerBodyPosture Body / Hand / Handing;  // 当前状态描述（只读）
    bool IsGrounded;                   // GroundProbe 去抖结果（主体类每帧写入）
    void RequestTransition(三枚举);     // 提议切换（裁决在执行）
}
```

有意只读：Input（帧快照）、当前状态描述、IsGrounded（物理事实）。

### 6.3 转换表：三种边与信号约定

- **信号边**（`triggerSignal` 非空）：信号置位时按注册顺序评估（注册顺序 = 优先级）。
- **持久边**（`evaluateEveryFrame = true`）：每帧评估真值条件（瞄准按住/松开、空中抓墙）。
- **请求边**（两者皆空）：状态类 Tick 内 `RequestTransition` 提议（落地、脱墙、到顶等物理条件）。

**信号消费与裁决分离**：边条件只用 `GetSignal` 探测、不消费；信号被评估后由机器统一
`Consume` 一次（命中切换 / 未命中丢弃）。状态类只提议；机器 `SwitchTo` 固定执行
“旧.Exit → 新.Enter”，切换前后处理 `Motion.HandingChanged / PreviousHanding` 交接。

状态/边注册集中在 `PlayerControllerScript.InitStateMachine`（新增状态/边的数据驱动变更点）。

### 6.4 行为合法性：状态/边存在性

合法组合见 §6.1；轴间约束由“无节点/无边”表达：

| 想表达 | 编码位置 |
|---|---|
| 持枪不能攀爬 | 无 Rifle/Pistol/Grenade×Climbing 组合；工厂无映射；无对应边 |
| 可瞄准武器 | Rifle/Pistol/Grenade 各有 `Normal×Ground → Aiming×Ground` 入边 |
| 各武器可跑，瞄准锁定行走档 | 档位集中在 `PlayerMotionValuesSO`；瞄准状态不响应 Run |
| 瞄准中不能切武器 | Rifle/Pistol/Grenade 的 Aiming 组合均无槽位信号出边 |
| 瞄准中可跳跃（自动取消瞄准） | Aiming→Normal×Jumping 信号边（⑥/⑯/㉖） |

| 想做的事情 | 为什么不可达 |
|---|---|
| 持枪攀爬 | 无 Rifle/Pistol/Grenade×Climbing 状态类与入边 |
| 瞄准中切武器 | Rifle/Pistol/Grenade 的 Aiming 组合均无槽位出边 |
| 跳跃/攀爬中切武器 | 对应状态无切枪出边 |
| 瞄准中滞空 | Aiming 无 Jumping 组合；瞄准中跳跃走取消瞄准边 |

### 6.5 武器行为差异与数据归属

同一 Body×Hand 组合下，武器差异收敛在两类位置：

1. **数值层**：档位目标/插值按 `PlayerHanding` 查询（`GroundTargetSpeed` / `MoveGroundSpeed`）；
2. **Handing 层叶子**：IK 写入差异（写哪些手、锚点数据、全 0 未标定守卫）、Aiming 解析缝
   （根/轴点/offset/localRotation/front 语义）、Grenade 弧线接线。

### 6.6 走廊状态扩展点（登顶已落地 / 翻越同流程）

登顶已按走廊族落地：`Unarmed_Normal_ClimbTopOut_State`（Handing 叶子）→
`NormalClimbTopOutStateBase`（HandPosture）→ `ClimbTopOutStateBase`（BodyPosture），
边注册在 `PlayerControllerScript.InitStateMachine`，攀爬族到顶请求在 `ClimbingStateBase`。
翻越类后续走廊同流程扩展：新增走廊族基类 → HandPosture 层 → Handing 层叶子 + 工厂/边表各行，
其余状态零改动。

### 6.7 状态类继承链（当前结构 · Player → BodyPosture → HandPosture → Handing）

**层级定义**：

| 层 | 目录 / 类 | 职责 |
|---|---|---|
| Player | `States/Player/PlayerStateBase.cs` | 状态契约 + 仅跨 ≥2 个 Body 族的纯工具（`WriteNonAimSpeedParams`） |
| BodyPosture | `States/BodyPosture/GroundStateBase.cs` / `JumpingStateBase.cs` / `ClimbingStateBase.cs` / `ClimbTopOutStateBase.cs` | 该身体姿态族的公共字段与流程（单例族 = 行为宿主） |
| HandPosture | `States/HandPosture/NormalGroundStateBase.cs` / `AimingGroundStateBase.cs` / `NormalJumpingStateBase.cs` / `NormalClimbingStateBase.cs` / `NormalClimbTopOutStateBase.cs` | 该族 × 手部操作的差异流程/字段 |
| Handing | `States/Handing/*_State.cs`（13 个具体组合状态类） | 身份 + 手持差异声明（IK/特例/进入语义） |

**继承规则**：

1. 所有使用中的具体组合状态类一律落在 Handing 层：`PlayerStateBase → BodyPosture 族基类 →
   HandPosture 变体基类 → 具体状态类`；单例族（Climbing/TopOut）同样遵守，不直接继承
   `PlayerStateBase`。
2. 归属原则：只被一个族使用的内容放到该族/其下；只被一个 HandPosture 变体使用的内容放到该变体；
   只有真正跨 ≥2 个 Body 族的方法留在 Player 层。
3. HandPosture/Handing 层允许暂时为空锚点（身份叶）：它们是手部变体与武器差异的扩展点，
   也是“所有状态同层”语义的占位，不得以“空类无用”删除。
4. 推/拉（后续）：在 `GroundStateBase`（BodyPosture）下新增 `Pulling/Pushing` 变体
   （HandPosture）及其 Handing 叶子即可，既有结构零改动。

**完整继承树**：

```text
PlayerStateBase（契约 + 跨族纯工具）
├── GroundStateBase                    // BodyPosture · 地面族
│   ├── NormalGroundStateBase          // HandPosture · Normal
│   │   ├── Unarmed/Rifle/Pistol/Grenade_Normal_Ground_State
│   ├── AimingGroundStateBase          // HandPosture · Aiming
│   │   └── Rifle/Pistol/Grenade_Aiming_Ground_State
│   └── （扩展预留）Pulling/PushingGroundStateBase
├── JumpingStateBase                   // BodyPosture · 滞空族
│   └── NormalJumpingStateBase
│       └── Unarmed/Rifle/Pistol/Grenade_Normal_Jumping_State
├── ClimbingStateBase                  // BodyPosture · 攀爬族（单例族 = 行为宿主）
│   └── NormalClimbingStateBase
│       └── Unarmed_Normal_Climbing_State
└── ClimbTopOutStateBase               // BodyPosture · 登顶走廊族（单例族 = 行为宿主）
    └── NormalClimbTopOutStateBase
        └── Unarmed_Normal_ClimbTopOut_State
```

**各层职责要点**：

- `PlayerStateBase`：契约（Enter/Exit/Tick/OnAnimatorMove）+ `WriteNonAimSpeedParams`；
  相机轴/移动方向换算归地面族。
- `GroundStateBase`：CameraForward/Right、CameraSpaceMoveDir、MoveDirection（自上层回收，
  供地面族 Normal 与 Aiming 及未来 Pull/Push 复用）、`NextGroundVertical`、
  `TryRequestAirborneExit`（离地越界守卫 + 提议，与机器请求边条件同源）、地面 `OnAnimatorMove`
  （根运动水平 + 手写垂直）；不写 Enter/Tick 模板（两分支阶段顺序不同）。
- `NormalGroundStateBase`：落地过渡与切枪速度插值字段、Enter 公共三段、Tick 骨架、
  `RotateTowardMoveDirection`、地面参数写入、右手标定/左手贴握 IK 写入辅助；
  虚缝 `WriteHands`（Unarmed 空实现）。
- `AimingGroundStateBase`：两轴速度/yaw 平滑、程序化 IK 接管链（切父/过渡/还原、腕位捕获与
  一次性重锁、双手 ChainIK、确定性轴点旋转）、`AimFrontTarget`、`RotateTowardCameraForward`；
  差异缝：`AimWeaponRoot / AimPivot / AimAxisOffset / AimLocalRotation / AlwaysAimToFront /
  OnPostAimTargetsUpdated`。
- `JumpingStateBase`：惯性捕获、JumpSpeed 注入、落地提议、重力、Motion 写回、全量位移。
- `ClimbingStateBase` / `ClimbTopOutStateBase`：以当前唯一实现为基线承载族字段与流程
  （方案 A：族基类 = 行为宿主）。

Handing 叶子差异缝：

| 叶子 | 覆写内容 |
|---|---|
| Unarmed_Normal_Ground | 不覆写（空身份叶） |
| Rifle/Pistol_Normal_Ground | `WriteHands`：左贴握 + 右手标定（各自锚点） |
| Grenade_Normal_Ground | `WriteHands`：仅右手标定 + 全 0 未标定守卫 |
| Rifle/Pistol_Aiming_Ground | Aim 解析缝（根/轴点/offset） |
| Grenade_Aiming_Ground | Aim 解析缝 + `AimLocalRotation` + `AlwaysAimToFront` + `OnPostAimTargetsUpdated`（弧线） |
| 4× Normal_Jumping / Climbing / TopOut 叶子 | 纯身份声明（结构一致性占位） |

### 6.8 程序化 IK 的写入权威与 target 生命周期

动画 clip 不再绑定任何 IK target 曲线，脚本是 IK target 的唯一权威写入者。

| 阶段 | 左手 TwoBoneIK target | 右手 TwoBoneIK target | 双手 ChainIK target |
|---|---|---|---|
| Unarmed Ground 常态 | 不写 | 不写 | 不写（权重 0） |
| Rifle / Pistol Ground 常态 | 每帧写“武器根 × 锚点”世界位姿 | 每帧写各自 SO 标定值（Chest 子级 local） | 不写 |
| Grenade Ground 常态 | 不写（左臂动画驱动） | 每帧写 grenade 标定值（未标定全 0 时跳过并告警） | 不写 |
| Rifle grab/put 动画帧 | 状态写入后由主体类覆盖为“标定位 → 中段 → 标定位”轨迹 | 同左 | 不写 |
| Pistol / Grenade grab/put 动画帧 | pistol 写标定值；grenade 不写左 | 每帧写标定值（不采用中段轨迹） | 不写 |
| Aiming（Rifle/Pistol/Grenade） | 不写 | 交叉淡化期由主体类对齐 TwoBoneIK 与 ChainIK target | 每帧由瞄准态写“腕-枪相对位姿 × 当前枪根位姿” |

- 地面态不做 Enter 记录 / Exit 还原：场景默认位无主，还原会把 target 写回调试位。
- 瞄准态内部保留“记录-恢复”：进入记录枪根原父级与 local TRS、捕获腕-枪相对位姿常量，
  退出 `RestoreAimRig` 原样恢复。
- 收枪识别：机器 `SwitchTo` 在手持变化时写 `Motion.PreviousHanding`（不随 HandingChanged 清除），
  主体类据此区分步枪收枪（中段轨迹接管）与 pistol/grenade 收枪。
- 遗留观察项：grenade 右手锚点待编辑器标定（标定前跳过写入）；左手 TwoBoneIK target 只写不还，
  跨武器会话残留值取决于 put/grab 动画左手 IK weight。

## 7. 文件组织（当前结构）

```text
Assets/playercontroller/
├── PlayerControllerScript.cs   主体层（输入桥接·状态/边注册·参数同步·Gizmos 调试区）   [已实现]
├── PlayerInputState.cs         输入层 + InputActionBridge                              [已实现]
├── PlayerAnimatorParams.cs     Animator 参数值缓存写入器（同值跳过 Set*）               [已实现]
├── PlayerStateMachine.cs       扁平单机状态机（组合键 + 状态字典 + 转换边表）            [已实现]
├── PlayerContext.cs            状态上下文（共享引用集合）                               [已实现]
├── PlayerMotion.cs             共享运动描述（跨状态交接槽，纯数据）                     [已实现]
├── PlayerMotionValuesSO.cs     运动数值资产（物理常量 + 纯计算；资产级单例）            [已实现]
├── WallProbe.cs                墙面探测工具（参数化射线阵列）                           [已实现]
├── GroundProbe.cs              着地去抖探测工具                                         [已实现]
├── GrenadeArcPreview.cs        手雷弧线预览工具（抛物线/地形采样/淡入；独立于状态类）    [已实现]
├── Editor/PlayerMotionValuesSOEditor.cs   SO 分组折叠 Inspector                        [已实现]
├── code-conventions.md         角色控制器代码规范（写法风格与工程约定）                 [已实现]
└── player-controller-design.md 本文档

States/
├── PlayerStateFactory.cs        状态工厂（组合键 → Handing 层叶子实例）                 [已实现]
├── Player/PlayerStateBase.cs                                                          [已实现]
├── BodyPosture/
│   ├── GroundStateBase.cs / JumpingStateBase.cs
│   ├── ClimbingStateBase.cs / ClimbTopOutStateBase.cs                                 [已实现]
├── HandPosture/
│   ├── NormalGroundStateBase.cs / AimingGroundStateBase.cs
│   ├── NormalJumpingStateBase.cs / NormalClimbingStateBase.cs
│   └── NormalClimbTopOutStateBase.cs                                                  [已实现]
└── Handing/（13 个具体组合状态类，见 §6.1 合法组合）                                   [已实现]
```

## 8. 实施状态（当前）

| 模块 | 状态 |
|---|---|
| 输入层（PlayerInputState + 桥接） | [已实现] |
| 数值层（PlayerMotionValuesSO + PlayerMotion + PlayerAnimatorParams） | [已实现] |
| 状态机（组合注册/三种边/信号裁决/交接槽） | [已实现]：13 个合法组合状态类与边表全部注册 |
| 状态类继承链（Player/BodyPosture/HandPosture/Handing）与 States 分层目录 | [已实现]（2026-09-07 一次性迁移完成） |
| 程序化 IK（轴点旋转/双手 ChainIK/拔收枪轨迹）与手雷弧线预览 | [已实现]（弧线为独立工具类） |
| 场景装配与标定（武器挂点/IK target/轴点/锚点） | [部分实现]：由编辑器侧完成 |
| 回归验证 | [已实现]：主要行为检查通过；提交与最终回归待确认 |

## 9. 后续扩展方向（规划中，实施前再确认）

- 推/拉：在 `GroundStateBase` 下新增 `Pulling/Pushing` 变体（HandPosture）与叶子。

## 10. 待确认清单

1. 手持类别建模：作为**装备数据**（枚举 + 能力声明）而非状态机——待最终拍板（已推荐）。
2. 切回空手的按键（如 0 键收枪）：inputactions 现无对应 action，装备互切边暂不含回空手路径。
3. `Look` 输入与相机模块的归属（本控制器只管透传？）。
4. 瞄准锁定行走档数值语义：`MoveAimSpeed` 当前以步枪行走档（1.5）跨武器生效；
   手枪/手雷瞄准是否改走各自行走档（2.0）——确认后单独调整。
5. 推/拉走廊：走廊语义与挂接时机待该功能启动时确认。
6. `IsTransient` 瞬时标记：是否需要、何时引入。
