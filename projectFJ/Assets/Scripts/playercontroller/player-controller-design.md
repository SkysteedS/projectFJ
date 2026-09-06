# 角色控制器架构设计文档（PlayerControllerScript）

> 面向 `Assets/playercontroller/PlayerControllerScript.cs` 的正式化设计，取代 `test.cs` 可行性原型。
> 进度标记：`[已确认]` = 已与需求方敲定；`[待确认]` = 方案已给出，等待拍板；`[已实现]` = 代码已落地。

---

## 1. 背景与目标

- **现状**：`test.cs`（1063 行）已验证移动/跳跃/攀爬/手部 IK 的可行性，但结构混乱难以维护：姿态分支分散在 `Rotate/Move/OnAnimatorMove/输入回调` 等 6+ 处；调试逻辑与正式逻辑混杂；单文件多类型。
- **目标**：以"状态模式 + 正交分层 + 数据驱动约束"重构为正式角色控制器，保证：
  1. 输入层与运动/姿态解耦（`[已确认]` 保留 test.cs 中的解耦思路并强化）
  2. 姿态管理权收回主体类（状态切换唯一入口）
  3. 各姿态按状态模式独立成类，类内读输入、实现本姿态的运动与操作管理
  4. 扩展一个"临时/走廊状态"（如登顶翻越）不改动既有状态类

## 2. 决策记录

| 决策点 | 结论 | 状态 |
|---|---|---|
| 输入/运动状态解耦关系 | 保留，输入层纯化为"无条件数据层" | [已确认] |
| 姿态分层 | 身体姿态 / 手持类别 / 手部操作 三个正交轴，**正交状态机组合**，不做大类嵌套 | [已确认] |
| 轴间交叉约束 | 建模为"能力许可表"（每武器声明允许的行为集）+ 正交规则表，主体类单点执行（输入守卫 / 转换条件 / 强制补救 三环节） | [已确认] |
| 临时姿态（登顶/翻越） | Body 轴新增"走廊状态"子类，插入 = 枚举+注册+转换表边+规则表行，不改旧状态类 | [已确认] |
| 攀爬触发方式 | **不设独立攀爬输入**：跳跃键为统一"移动动作"请求，先做可攀爬检测，命中→直接进攀爬，未命中→跳跃 | [已确认] |
| 能力许可表 | **随扁平化退役**：行为合法性由"状态/边的存在性"编码（无 Rifle×Climbing 状态即不可达）；状态内行为差异（如瞄准锁定行走档）在具体状态类内实现 | [已确认] |
| 转换表实现 | 字典委托表（数据驱动，扩展不动旧代码）；**已落地**为"转换边列表 + 信号驱动/请求驱动"（PlayerStateMachine.cs，注册顺序=裁决优先级，信号消费与裁决分离） | [已确认] |
| 走廊状态基类 | 暂不定义：`IsTransient` 预留缺口已随死代码清理移除，登顶/翻越实现时再引入走廊状态类与基类标记 | [已确认] |
| 手持类别建模 | 作为**装备数据**（枚举+能力声明），非状态机 | [待确认—已推荐] |
| 程序化 IK target 写入权威 | **脚本唯一权威**：武器地面态每帧写各自标定值；不做 Enter/Exit 还原（还原写回场景默认位 = 旧 Write Defaults 同类表现）；rifle grab/put 的中段轨迹由主体类按动画进度接管 | [已确认 2026-09-06] |

## 3. 总体架构

```
┌────────────────────────────────────────────────────────────┐
│ 主体层 PlayerControllerScript（组合器 + 唯一裁决者）          │
│  组件持有 · 输入守卫 · 状态切换裁决 · 全局动画参数同步        │
├────────────────────────────────────────────────────────────┤
│ 姿态层（正交双状态机）                                       │
│  BodyStateMachine: Ground / Jumping / Climbing (+走廊状态)   │
│  HandStateMachine: Normal / Aiming (+瞬时动作)               │
├────────────────────────────────────────────────────────────┤
│ 装备轴（数据）  Handing: Unarmed / Rifle / Pistol / Grenade   │
│  ├─ HandingCapabilities  能力许可表（轴间交叉约束唯一来源）   │
│  └─ 显隐 / 动画 / 未来武器数据                                │
├────────────────────────────────────────────────────────────┤
│ 数值层 MotionState（瘦身）  物理常量 · 速度平滑 · 重力 · 姿态混合 │
├────────────────────────────────────────────────────────────┤
│ 输入层 PlayerInputState（纯数据帧快照）                      │
└────────────────────────────────────────────────────────────┘
```

三个正交轴明确为：**身体姿态**（位移/旋转/重力/根运动）、**手持类别**（装备与能力集）、**手部操作**（瞄准等动作模式）。轴间交叉（如"Rifle 不能攀爬"）**不是状态类之间的耦合**，而是能力表中的数据声明 + 主体类三环节单点执行。

## 4. 输入层设计（[已实现] Phase 1）

- 文件：`Assets/playercontroller/PlayerInputState.cs`
- 边界：**只记录"玩家按了什么"**，不含任何业务规则、不引用 InputSystem（纯数据，可脱离引擎单元测试）；InputSystem 适配集中在同文件的 `InputActionBridge` 桥接层。

### 4.1 两类输入模型

| 类型 | 语义 | 示例 |
|---|---|---|
| 连续输入 | 帧快照滚动值，逻辑可读当前值 | Move / Look / Run / Aim / FireHeld |
| 边沿信号（按下型） | Started 置位、跨帧保留、消费即清 | Jump / FirePressed / Reload / Interact / SlotRifle / SlotPistol / SlotGrenade |

> **Jump 信号语义**：统一的"移动动作"请求，**不设独立攀爬输入**。处理顺序：可攀爬检测优先（命中 → 进攀爬），未命中 → 跳跃。因此 Jump 会触发"攀爬优先、次选跳跃"的二选一转换，信号消费与裁决的约定见 §6.3。

> **攀爬检测时机（2026-09-04 增补）**：除地面 Jump 瞬间的"攀爬优先"判定外，滞空（Jumping）还注册一条
> 持久边 `(Unarmed, Normal, Jumping) → (Unarmed, Normal, Climbing)`——滞空期每帧用同一条件（墙面命中 +
> 水平速度朝墙，见 PlayerControllerScript.TryEnterClimb）重测，上升段与下降段均可抓墙进攀爬，无需再次按键；
> 地面 Jump 的"攀爬优先、跳跃兜底"二选一保留。

### 4.2 帧快照语义（关键设计）

Input System 回调在 Update 帧间任意时刻触发；若逻辑直接读实时值，帧内不同消费者可能看到不一致视图。因此：

```
输入系统回调 ──实时写入层──▶ Capture()（每帧帧首调用一次）──▶ 帧快照层（逻辑只读这里）
```

- 实时层：适配器随时写入（`SetMove/Press(信号)` 等）
- 帧层：`Capture()` 滚动，逻辑经 `Move/Aim/GetSignal/Consume` 等只读访问
- 信号在 `Capture` 后才被清空 → 一次完整点击（Started→Canceled）必然进入下一帧快照，不会丢失
- `Consume` 读并清：一次性事件（跳跃）被消费后不重复触发；`ClearSignals()` 供状态切换时丢弃残留（对应 test.cs 的 `CancelJump` 场景）
- `Look` 透传原始增量，语义由相机系统决定（相机模块归属另行确认）

### 4.3 与 PlayDefaultInputAction.inputactions 的映射（桥接层）

| Input Action | 类型 | → 写入 | 备注 |
|---|---|---|---|
| Move | Value Vector2 | SetMove | |
| Look | Value Vector2 | SetLook | 透传增量 |
| Run | Button | SetRun | 按住语义 |
| Aiming | Button | SetAim | 按住语义 |
| Fire | Button | SetFireHeld + Press(FirePressed) | 按住 + 按下边沿双语义 |
| Jump | Button | Press(Jump) | |
| Rifle / Pistol / HandGrenade | Button | Press(SlotRifle/Pistol/Grenade) | 槽位切换到"信号" |
| Reload | Button | Press(Reload) | |
| Interacct | Button | Press(Interact) | action 名拼写为 Interacct |

> 无独立 Climb 输入：攀爬由跳跃键触发（Jump → 可攀爬检测），inputactions 无需新增 action。

### 4.4 手持当前值归属

test.cs 中 `handing`（当前武器）从输入层**移出**：它是"游戏状态"而非"输入"。输入层只提供"切到槽位 X"的信号（SlotRifle 等）；当前手持值由装备轴（主体类/装备子系统）持有，并作为能力表查询的输入。

## 5. 数值层设计（[部分实现]）

拆成两件（命名避让 test.cs 的 `MotionState` 正式名采用 `PlayerMotion`）：

1. **共享运动描述 `PlayerMotion`（[已实现]）**：跨状态交接槽，挂在 PlayerContext.Motion——
   当前状态类每次计算完毕写入（速度向量），新状态 `Enter` 读取初值（滞空继承起跳水平速度、
   落地读取下落速度做姿态混合）。纯数据，不含计算。
2. **运动数值服务（[已实现] = `PlayerMotionValuesSO`）**：物理常量（重力/走跑速度/加速度/旋转速度/跳跃高度/
   攀爬速度/登顶参数/死区阈值）、速度平滑（MoveTowards）、重力累积、跳跃初速度反推、姿态混合值平滑、
   垂直速度参数换算（以姿态为参数的纯函数，无分支控制流）——状态类调用它计算，结果写回
   `PlayerContext.Motion`。SO 为**资产级单例**（场景组件只持引用，一处修改全局生效、Play Mode 实时调参），
   Inspector 由 `Editor/PlayerMotionValuesSOEditor.cs` 按 [Header] 分组折叠。

   Animator 参数写入统一经 `PlayerAnimatorParams`（挂在 PlayerContext.AnimParams）：**值缓存写入器**，
   同值跳过 Set*（vertical/horizontal/falling speed、body/hand/handing 为脚本权威参数；被动画曲线控制的
   IK 权重参数脚本不写，由动画层负责）。

- **移除**：所有姿态分支控制流（姿态检测/切换决策）→ 移交给状态类
- 运动交接的读写约定：**写入 = 当前状态类**（Tick/OnAnimatorMove 末尾），**读取 = 新状态**（Enter）

## 6. 状态机设计（[已实现] 扁平单机——见 PlayerStateMachine.cs）

### 6.1 结构：与无分层动画状态机同构

三个正交枚举保持为**状态描述**（手持 × 手部操作 × 身体姿态）。**内部**用结构体 `PlayerStateKey` 把三枚举绑定为可比较的状态键（字典键、边路由、当前状态），**外层调用一律只给三枚举参数**（控制脚本/状态类无需构造键类型）；状态机 = 一个 `Current`（状态类实例）+ 一个状态字典（Key → 状态类）+ 一张转换边表（Key 之间）。每个执行类落实到**完整组合状态**（`Unarmed_Normal_Ground_State` 式命名），创建走工厂：

```
三个枚举（保持正交，与 PlayerControllerScript 动画参数直接对齐）：
  PlayerBodyPosture(Ground/Jumping/Climbing/ClimbTopOut) · PlayerHandPosture(Normal/Aiming) · PlayerHanding(Unarmed/Rifle/Pistol/Grenade)

PlayerStateKey（内部键，外层不可见）：三枚举组合，当前合法组合（能力表推导）：
  Unarmed×Normal×Ground/Jumping/Climbing/ClimbTopOut
  Rifle×Normal×Ground/Jumping / Rifle×Aiming×Ground
  Pistol×Normal×Ground/Jumping / Pistol×Aiming×Ground
  Grenade×Normal×Ground/Jumping（Grenade 暂不接入 Aiming）
  └─ 非法组合（持枪攀爬、Grenade 瞄准…）没有状态类、没有转换边 → 天然不可达（无需规则表）

PlayerStateFactory（工厂）：Create(三枚举) → 状态类实例（创建清单唯一集中处，内部以 Key 映射）
PlayerStateMachine（单机）：
  Current: PlayerStateBase（实例）          ← 当前状态；Body/Hand/Handing 三枚举直接读
  states:  Dictionary<PlayerStateKey, PlayerStateBase>  ← 一个状态 = 一个组合类
  edges:   List<TransitionEdge>             ← 一张表（所有变化同机制）
```

- 跳跃、落地、攀爬、瞄准进入/退出、**切武器**——全部是同一张表里的转换边（不再有专门的"槽位处理逻辑"）
- 外层只给具体参数：注册/进入/请求/边构造均以三枚举传参（`new PlayerStateKey` 仅在机器/工厂内部出现）
- 状态机每次切换后 `Machine.Body/Hand/Handing` 直接给出三枚举，供动画参数同步（body posture / isAiming / player handing）对齐

### 6.2 状态契约与上下文

```csharp
abstract class PlayerStateBase
{
    virtual void Enter(PlayerContext ctx);
    virtual void Exit(PlayerContext ctx);
    abstract void Tick(PlayerContext ctx);
    virtual  void OnAnimatorMove(PlayerContext ctx); // 位移接管，默认为空
}

class PlayerContext   // 共享引用集合（状态类操作通道）：读共享信息 / 写运动状态 / 调用与改写组件
{
    Animator / CharacterController / Transform / Camera / 四肢 IK 约束;
    PlayerAnimatorParams AnimParams;    // Animator 参数值缓存写入器（同值跳过 Set*）
    PlayerInputState Input;                 // 帧快照（只消费）
    PlayerMotion Motion;                    // 共享运动描述（状态类写入，新状态 Enter 读取）
    PlayerBodyPosture Body;                 // 当前状态描述（有意只读；修改唯一通道 = 机器裁决）
    void RequestTransition(...);            // 提议切换（裁决与执行在机器）
}
```

### 6.3 转换表：三种边（[已实现]）

- **信号边**（`triggerSignal` 非空）：信号置位时按注册顺序评估（= 裁决优先级）。Jump"攀爬优先、跳跃兜底"、所有切武器互切都是信号边；
- **持久边**（`evaluateEveryFrame=true`）：每帧评估真值条件（瞄准按住/松开）；
- **请求边**（两者皆空）：状态类 Tick 内 `RequestTransition(to)` 提议（落地、攀爬退出）。

```csharp
// 信号边示例：Jump 二选一（注册顺序 = 优先级）；外层只给三枚举，内部转 PlayerStateKey
machine.RegisterEdge(new TransitionEdge(
    PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,     // from
    PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Climbing,   // to
    ctx => ctx.CanDo(PlayerAction.Climb) && ClimbWallDetected(ctx), Signal.Jump, label: "跳跃·攀爬检测优先"));
machine.RegisterEdge(new TransitionEdge(
    PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
    PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Jumping,
    ctx => ctx.CanDo(PlayerAction.Jump), Signal.Jump, label: "跳跃兜底"));

// 持久边示例：瞄准进入/退出（仅步枪存在入边 → 其他武器不可瞄准）
machine.RegisterEdge(new TransitionEdge(
    PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
    PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
    ctx => ctx.Input.Aim, evaluateEveryFrame: true, label: "按住瞄准"));
machine.RegisterEdge(new TransitionEdge(
    PlayerHanding.Rifle, PlayerHandPosture.Aiming, PlayerBodyPosture.Ground,
    PlayerHanding.Rifle, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
    ctx => !ctx.Input.Aim, evaluateEveryFrame: true, label: "松开瞄准"));

// 注册方式（工厂 + 显式组合；无需构造键类型）
machine.RegisterState(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground,
    PlayerStateFactory.Create(PlayerHanding.Unarmed, PlayerHandPosture.Normal, PlayerBodyPosture.Ground));
```

**信号消费与裁决分离（关键约定，已落实）**：边条件只用 `GetSignal` 探测、不消费；信号被评估后由机器统一 `Consume` 一次（命中切换 / 未命中丢弃）。状态类只**提议**，机内 `SwitchTo` 为唯一执行点（固定 旧.Exit → 新.Enter）。

### 6.4 行为许可的表达（无运行期查表）

扁平状态下，行为合法性不需要运行期查表，三处**声明性编码**即完整表达：

| 想表达 | 编码位置 |
|---|---|
| 持枪不能攀爬 | 状态枚举无 Rifle/Pistol/Grenade×Climbing 组合；工厂无映射；无对应边 |
| 瞄准仅限步枪/手枪 | 仅 Rifle/Pistol 的 `Normal×Ground → Aiming×Ground` 入边；无 Grenade×Aiming 组合 |
| 各武器可跑，瞄准锁定行走档 | 走/跑档位集中在 `PlayerMotionValuesSO`（rifle 1.5/3.5、pistol 2/4、grenade 2/4、unarmed 2/4）；瞄准状态不响应 Run |
| 瞄准中不能切武器 | Rifle/Pistol Aiming 组合无槽位信号出边 |
| 瞄准中可跳跃（自动取消瞄准） | Aiming→Normal×Jumping 信号边（⑥/⑯） |

> 早期正交方案中的"能力许可表"（PlayerCapabilities）已随扁平化退役：它曾是轴间约束的唯一来源，但"状态/边存在性"已更直接地编码同一约束，保留反而造成"状态集合/边集合/能力表"三处信息需同步、易漂移。若未来需要面向 UI 的"当前不能做什么"提示，可从边/状态清单派生（或届时建立武器配置数据），届时再引入。

### 6.5 非法组合的天然表达（无规则表）

| 想做的事情 | 为什么不可达 |
|---|---|
| 持枪攀爬 | 不存在 Rifle/Pistol/Grenade×Climbing 状态类与入边 |
| 瞄准中切武器 | Rifle/Pistol Aiming 组合无槽位出边 |
| 跳跃/攀爬中切武器 | 对应状态无切枪出边 |
| Grenade 瞄准 | 无 Grenade×Aiming 组合与入边（暂未接入） |

所有"禁止"都是"无节点/无边"，与动画状态机里"没有连线"同构——这正是扁平化的最大收益。

### 6.6 走廊状态扩展点（登顶/翻越示例）

插入 `TopOut`：BodyPosture 加枚举行 + 新增状态类 + 工厂加一行映射 + 注册 `((Unarmed, Normal, Climbing) → (Unarmed, Normal, TopOut))` 与 `((Unarmed, Normal, TopOut) → (Unarmed, Normal, Ground))` 边 + Climbing 状态类到顶处一行 `RequestTransition`。其余状态零改动。

### 6.7 地面/滞空复用提炼计划（Pistol 与 Grenade 已落地，提炼尚未实施）

**动机**：`Unarmed/Rifle × Normal × Ground/Jumping` 目前是"同一套地面/滞空逻辑 + 武器差异"的
复制实现：武器切换速度插值、落地过渡、转向、WriteAnimatorParams 在 Unarmed 与 Rifle 各存一份；
滞空（惯性 + 手写重力 + 落地判定）两把武器几乎逐行相同。直接新增 Pistol/Grenade 会把同样代码
再复制两遍。因此在 Pistol/Grenade 落地【之后】统一做一次提炼，避免先抽象、后实现时两处返工。

**目标结构（保持"一个组合 = 一个状态类 + 工厂映射"不变）**：

1. `GroundStateBase`（States/ 下）：收走两份地面状态共有的状态字段与流程——落地过渡
   （landingFallBlend/landingBlendTimer/active）、武器切换速度插值（weaponSwitchBlend*）、
   旋转（RotateTowardMoveDirection）、常规 WriteAnimatorParams；差异点向下暴露：
   - 速度档位：数值层提供按 Handing 的档位查询（走/跑目标速度 + 是否可跑），或子类覆写
     `WalkSpeed/RunSpeed/CanRun`（Pistol/Grenade 可跑，Unarmed/Rifle 各档位不变；Grenade 另声明
     左手不写 IK，瞄准暂不接入）；
   - 目标组合：请求边/信号边仍由机器注册，状态类自身不持有切换表。
2. `NormalJumpingStateBase`：收走滞空惯性/重力/落地判定/WriteAnimatorParams；
   具体子类（Unarmed/Rifle/Pistol）只提供"落回哪个 Handing 组合"。
3. `RotateTowardMoveDirection` 与常规参数写入若已收进基类，具体状态类只剩差异声明，
   单文件理解成本下降；地面/滞空类预期均可降到 100~150 行内。

**边界（不进入模板）**：Aiming（2D 轴速度/轴点旋转/双手 ChainIK）、Climbing/TopOut（走廊状态），
保持各自独立；拔/收枪切换的 target 轨迹协调继续留在主体类。

**约束与验收**：只做机械上提，不改变行为；完成后 Unarmed/Rifle 跑、跳、落地、切换速度插值
回归一致（对照现行为）；提炼完成后 Pistol/Grenade 不再复制移动逻辑，仅声明差异。

### 6.8 程序化 IK 的写入权威与 target 生命周期（2026-09-06 定稿）

**背景**：动画 clip 已不绑定任何 IK target 变换曲线（原绑定会造成 Animator Write Defaults 在切换帧
把 target 写回场景默认值，全部移除，见提交 `899fbdf` 记录）。此后脚本是 IK target 的唯一权威写入者，
任何“把 target 交还动画/还原原值”的写入都必须重新审视其原值是否仍被系统维护。

**写入分工**：

| 阶段 | 左手 TwoBoneIK target | 右手 TwoBoneIK target | 双手 ChainIK target |
|---|---|---|---|
| Unarmed Ground 常态 | 不写 | 不写 | 不写（权重为 0） |
| Rifle / Pistol Ground 常态 | 每帧写“武器根 × 锚点”世界位姿 | 每帧写各自 SO 标定值（Chest 子级 local） | 不写 |
| Grenade Ground 常态 | **不写**（手雷以右手动作为主，左臂由动画驱动） | 每帧写 grenade 标定值（未标定全 0 时跳过并告警） | 不写 |
| Rifle grab/put 动画帧 | 状态写入后由主体类 `UpdateRightHandSwitchIkTarget` 覆盖为“标定位 → rifleSwitchIkMid → 标定位”轨迹 | 同左（中段位 = rifle 的“另一个 IK 位置”） | 不写 |
| Pistol / Grenade grab/put 动画帧 | pistol 每帧写标定值；grenade 不写左 | 每帧写标定值（掏/收动作简单，**不采用** rifle 的中段轨迹方案） | 不写 |
| Aiming（Rifle / Pistol） | 不写（瞄准姿态由动画/肩链表现） | 与 ChainIK 的 crossfade 争抢由主体类 `UpdateRightHandAimBlendTargets` 统一写到同一混合位 | 每帧由瞄准状态写“腕-枪相对位姿 × 当前枪根位姿” |

**明确不再使用的机制**：地面状态曾做“Enter 记录 target 原 TRS → Exit 还原”（Capture/Restore，随
`899fbdf` 一并加入，注释为“交还动画系统/下一状态”）。target 曲线全部移除后该“原值”是无主的
场景序列化默认位——Exit 还原等于把 target 写回调试位，在 put 动画开头的 IK 权重混合期可见（与旧
Write Defaults 同表现）。2026-09-06 已从 Rifle / Pistol / Grenade 地面态删除：退出即保留末帧标定值，
rifle put 仍由主体类轨迹接管，pistol/grenade put 直接交给动画/权重淡出。

**仍然有效的“记录-恢复”仅限瞄准态内部**：瞄准进入时记录枪根原父级与 local TRS、捕获“腕-枪相对
位姿”常量，退出时 `RestoreAimRig` 恢复（见 code-conventions §6）——对象是枪根与腕位常量，与
TwoBoneIK target 的生命周期无关，不要混为一谈。

**收枪识别**：机器 `SwitchTo` 在手持变化时写入 `PlayerMotion.PreviousHanding`（不随 HandingChanged
清除），供主体类区分“步枪收枪”（走中段轨迹）与 pistol/grenade 收枪（不接管）。

**遗留观察项**：grenade 右手锚点待编辑器标定（标定前 grenade 态跳过右手写入）；左手 TwoBoneIK
target 目前只写不还（Pistol/Rifle 均无 Enter/Exit 存还，Grenade 不写左），跨武器会话的残留值是否
可见取决于对应 put/grab 动画的左手 IK weight——若再出现串味，按本节“脚本唯一权威”原则处理。

## 7. 文件组织

```
Assets/playercontroller/
├── PlayerControllerScript.cs   主体层（输入桥接·状态机集成·状态与转换边注册·Gizmos 调试区）[已实现]
├── PlayerInputState.cs         输入层 + InputActionBridge                [已实现]
├── PlayerAnimatorParams.cs     Animator 参数值缓存写入器（同值跳过 Set*）   [已实现]
├── PlayerStateMachine.cs       扁平单机状态机：PlayerStateKey 组合键 + 状态字典 + 转换边表 [已实现]
├── PlayerContext.cs            状态上下文（共享引用集合）                    [已实现]
├── PlayerMotion.cs             共享运动描述（跨状态交接槽，纯数据）          [已实现]
├── PlayerMotionValuesSO.cs     运动数值资产（物理常量 + 纯计算函数；资产级单例）[已实现]
├── Editor/PlayerMotionValuesSOEditor.cs   SO 分组折叠 Inspector          [已实现]
├── States/  （一个状态 = 一个完整组合类）
│   ├── PlayerStateBase.cs              [已实现]
│   ├── Unarmed_Normal_Ground_State.cs  [已实现]
│   ├── Rifle_Normal_Ground_State.cs    [已实现]
│   ├── Pistol_Normal_Ground_State.cs   [已实现]
│   ├── Grenade_Normal_Ground_State.cs  [已实现]
│   ├── Rifle_Aiming_Ground_State.cs    [已实现]
│   ├── Pistol_Aiming_Ground_State.cs   [已实现]
│   ├── Rifle_Normal_Jumping_State.cs   [已实现]
│   ├── Pistol_Normal_Jumping_State.cs  [已实现]
│   ├── Grenade_Normal_Jumping_State.cs [已实现]
│   ├── Unarmed_Normal_Jumping_State.cs [已实现]
│   ├── Unarmed_Normal_Climbing_State.cs [已实现]
│   └── Unarmed_Normal_ClimbTopOut_State.cs [已实现·走廊状态]
├── WallProbe.cs                墙面探测工具（自 test.cs 平移，零依赖）
├── GroundProbe.cs              着地去抖探测工具（替代 CharacterController.isGrounded）
└── （动画参数哈希）            集中于 PlayerControllerScript 顶部静态常量（Anim*）
```

## 8. test.cs 迁移映射（保留 / 改造 / 移除）

| 内容 | 处置 |
|---|---|
| WallProbe 全部 | 平移为独立工具类 |
| 攀爬手部 IK（姿态计算/臂展钳制/墙面投影） | 改造进 ClimbingState |
| 输入信号机制（Started/Canceled/Consume） | 保留并强化（帧快照） |
| 速度/重力数值 | 保留进 MotionState |
| 贴墙收敛 / OnAnimatorMove 分支 | 改造：位移接管归属各状态类 |
| "攀爬再按一次退出"测试逻辑 | 移除（物理条件驱逐） |
| 调试日志协程 / IK Gizmos | 仅保留开关或移入调试区 |
| 中文动画参数名 | Hash 集中定义后迁移，与正式英文参数共存 |

**命名冲突处理**：test.cs 中的类名让位给正式类（`PlayerInputState → TestPlayerInputState` 已完成；`MotionState / WallProbe` 在正式化时同样加 `Test` 前缀）。

## 9. 实施计划

| 阶段 | 内容 | 状态 |
|---|---|---|
| Phase 0 | 设计文档 | 完成 |
| Phase 1 | 输入层解耦（PlayerInputState + 桥接 + test.cs 改名） | 完成 |
| Phase 2 | 数值层落地（PlayerMotionValuesSO + PlayerMotion 交接槽 + PlayerAnimatorParams 缓存写入） | 完成 |
| Phase 3 | 状态机（扁平单机：组合状态枚举 + 单字典单边表 + 三种转换边） | **架构已完成**：合法组合状态类全部落地并注册（含 Unarmed/Rifle/Pistol/Grenade 的地面、滞空与瞄准组合）；状态内运动逻辑已完成，非法组合天然不可达 |
| Phase 4 | 主体类重组、回调桥接、场景接入 | 输入桥接已完成；场景接入待 Unity 侧 |
| Phase 5 | 回归验证（对照 test.cs 行为） | 待 Phase 3/4 |

## 10. 待确认清单

1. 走廊状态（登顶/翻越）：暂缓——`IsTransient` 预留缺口已随死代码清理移除；实现时新增走廊状态类 + 基类标记
2. 切回空手的按键（如 0 键收枪）：inputactions 现无对应 action，装备互切边暂不含回空手路径
3. `Look` 输入与相机模块的归属（本控制器只管透传？）
4. ~~状态内运动逻辑（Phase 2 数值层接入）的优先级~~ —— 已完成：速度/旋转/重力已落地（各状态类 + PlayerMotionValuesSO）
5. 地面/滞空复用提炼（`GroundStateBase` / `NormalJumpingStateBase`）：Pistol 与 Grenade 完成后统一处理，见 §6.7
