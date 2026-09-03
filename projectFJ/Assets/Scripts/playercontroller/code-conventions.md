# 角色控制器代码规范（code-conventions）

> 适用范围：`Assets/playercontroller/` 下全部 C# 代码与相关文档。
> 权威层级：架构语义以上位 [player-controller-design.md](player-controller-design.md) 与
> [PlayerStateMachine.cs](../playercontroller/PlayerStateMachine.cs) 类注释为准；本文件只规定
> "写法风格与工程约定"，与架构文档冲突时以架构文档为准并向用户报告。
> 本文件随仓库追踪；`progress-abstract/` 的记忆维护规则见仓库根 [AGENTS.md](../../AGENTS.md)。

---

## 1. 语言与注释

- 沟通与注释语言统一使用**中文**；标识符、代码关键字、枚举值使用英文。
- **公开类型与公开成员**（public class / public enum / public 方法 / public 字段）
  一律使用 `///` XML 注释（`<summary>` 摘要），注明职责、单位（m/s、°、s 等）、
  "只读/只写/谁写谁读"等关键语义。
- 类头 `<summary>` 采用既定格式：`具体状态/组件：xxx。` 换行列出行为要点（`- 名称：描述`），
  末行注明数值约定或引用（"可调参数见 PlayerMotionValues"）。
- 私有成员可用行尾 `// 注释`（如 `float currentSpeed; // 档位速度（…）`），但必须有说明，
  禁止无注释的魔法字段；结构常量集中定义在类顶 `#region 状态内结构常量`。
- 注释描述**现状**，不写"后续 Phase 接入"这类会过时的表述；规划中的改动写"规划中/待接入",
  并注明当前可运行性。

## 2. 命名规范

| 对象 | 规则 | 示例 |
|---|---|---|
| 类型 / 枚举值 / 公开字段 | PascalCase | `PlayerHandPosture`、`Unarmed_Normal_Jumping_State`、`Rifle` |
| 私有字段 / 局部变量 / 参数 | camelCase | `aimYawVelocity`、`currentSpeed` |
| Animator 参数哈希常量 | `Anim` + 参数名 Pascal | `AnimBodyPosture`、`AnimPlayerHanding` |
| 状态类 | `{Handing}_{Hand}_{Body}_State` | `Rifle_Aiming_Ground_State` |
| 枚举标签（中文说明） | 值后行内注释 | `Aiming = 1,  // 瞄准` |

- **序列化字段名锁定**：`[SerializeField]` / 公开序列化字段（`aimAxisPoint`、`aimAxisOffset`、
  各约束引用、`motionValues` 等）的字段名被场景/预制实例的 override 以
  `propertyPath: 字段名` 引用，**改名会静默丢失场景内的序列化值**（引用/数值失效）。
  因此此类字段保持初始命名（允许非 Pascal 风格），仅在确有迁移方案时经用户同意后改名。
- 枚举**值**以数字序列化，枚举名/成员名可安全重命名（不破坏场景数据）；
  但对外 API 引用需要全量同步修改（编译器会提示遗漏）。
- Animator **参数名字符串**（"player handing" 等）被动画控制器引用，改字符串即改控制器接口；
  常量名与字符串可以不同，但字符串本身不得随意改动。

## 3. 分层职责（引用性约定）

| 层 | 职责 | 禁止 |
|---|---|---|
| PlayerControllerScript（主体） | 组件持有、输入守卫、状态注册与边表、动画姿态参数同步、IK 权重回写 | 状态内运动计算 |
| 状态类（States/） | Enter/Exit/Tick/OnAnimatorMove，读输入→计算→写 Motion/动画参数→提议切换 | 直接改 constraint.weight（由主体回写）、直接构造 PlayerStateKey |
| PlayerStateMachine | 唯一裁决与执行：边评估、SwitchTo（旧.Exit→新.Enter）、HandingChanged 写入 | 业务计算 |
| PlayerContext | 共享引用 + 帧快照 + Motion 交接槽 + 状态只读 | 业务规则 |
| PlayerMotionValues | 物理常量 + 纯计算函数（无状态、无分支控制流） | 姿态判定 |
| PlayerInputState / InputActionBridge | 帧快照 + InputSystem 适配（唯一允许引用 InputSystem 的地方） | 业务规则 |

- 状态间交接只经 `PlayerContext.Motion`（写入=当前状态、读取=新状态 Enter，用后即毁的标记
  如 `HandingChanged` 读后必须清除）。
- 配置开关（数值、动画参数名、行为开关）一律集中在 `PlayerMotionValues`
  （Inspector 可调 + [Tooltip]）；若需呈现给场景级调参，由场景 override `motionValues.*`，
  不在代码里写死第二套数值。

## 4. 代码组织

- 一个文件一个主要职责类型；同族小类型（如桥接层）可同文件，但须有清晰 region 分区。
- `#region` 用于逻辑分区（组件引用 / 输入回调 / 生命周期等），region 标题说明用途；
  不使用 region 包裹单行代码。
- 删除代码而非注释掉代码（git 历史可查）；删除前按 §7 核查反射绑定。
- 不引入无用的 `using`；`UnityEngine.InputSystem` 只允许出现在输入适配层。
- 事件回调命名 `Get*Input`（InputSystem 按方法名绑定，见 §7），方法体只转发到 `InputActionBridge`。

## 5. 状态类内部约定

- 可调参数（速度/时长/阈值）进 `PlayerMotionValues`；状态内语义常量（"计时结束=0"、
  "无下落=0"）以命名常量/静态只读字段定义在类顶，禁止散落裸字面值。
- 状态类 Tick 内提议切换后立即 `return`（本帧剩余写入跳过），避免切换后继续写旧状态数据。
- 状态类不对 Animator 做姿态三枚举的写入（body/hand/handing 由主体统一同步）；
  仅写运动速度/IK 参数等状态内数据。

## 6. 程序化 IK / 瞄准相关工程约定（本期重点）

- **轴点（aimAxisPoint）只读**：代码绝不写它的 transform（父级/位置/旋转）；
  枪根切父/恢复父级只改枪根自身。
- 约束装配 = 场景定稿：脚本只移动 `constraint.data.sourceObjects[0].transform.position`
  或 `ChainIKConstraint.data.target`；不改约束引用、不改 constraint.weight
  （权重由主体经 Animator 参数回写）。
- 捕获的"腕-枪相对位姿"是进入瞄准瞬间的常量，瞄准期间不更新；退出必须原样恢复
  进入前父级与 local TRS（交还动画系统），无跳变是验收标准。
- 需要调试约束行为时使用现有 Gizmos（品红射线 vs 落点球），新增调试输出以开关注入。

## 7. 删除代码前的核查清单（本项目的"无引用≠可删"陷阱）

以下调用者无法用静态引用检索发现：

1. **InputSystem 回调**：`Get*Input(InputAction.CallbackContext)`——由场景中 PlayerInput 组件
   按方法名(`m_Event` 绑定)调用；删方法会断输入。
2. **动画事件**：公开的 `AnimationEvent(string)` 式方法——由 AnimationClip 的事件表按名调用；
   删除前需确认全部 .anim/.controller 无对应事件配置（本项目已确认 0 处，已删）。
3. **Unity 生命周期/调用约定**：`OnAnimatorMove`、`OnDrawGizmos`、`OnDrawGizmosSelected`、
   `OnEnable/OnDisable/OnDestroy`、MonoBehaviour 固有回调——框架反射调用，不得按"无引用"删除。
4. **序列化依赖**：字段名被场景/预制 override（`propertyPath`）引用；删除字段会静默丢数据。
5. **预留扩展点**：确属"规划中/待接入"（如剑、手雷状态类）在工厂中有映射或文档有记录时，
   删除需用户确认；确认保留的须在注释中标注"规划中：…运行期不可达"。

## 8. 工程流程

- 开发在 `feature/<用户名>-<功能>` 分支进行（见根 AGENTS.md）；提交信息使用中文，
  一次提交只做一件事（注释清理与规范整理可合并为一次，但须与功能改动分开）。
- 记忆维护：涉及行为/架构变化时同步更新 `progress-abstract/`（维护规则按 README.md，
  新建/调整记录前询问用户）。
