# 项目级指导（AGENTS.md）

## 项目简介

- 项目类型：3D 第三人称尸潮生存游戏（PC / Windows，Unity 2022.3 LTS+，URP）。
- 整体目标：以“大规模单位性能优化”为核心展示点的射击 + 防御物 + 波次成长玩法，作为游戏客户端开发岗位面试展示项目，约 12 周完成。

## 会话开始：启动流程

每次进入新对话时，按以下顺序执行：

1. **（新克隆/新机器首次）初始化 git 钩子**：若未配置过，先执行 `git config core.hooksPath git-hooks` 与 `git lfs install`，否则 20MB 守卫与 LFS 推送钩子不会生效。
2. **检查并初始化记忆目录**：确认 `progress-abstract/` 下存在 `index.md`、`仍然需要留意的推进/`、`已过时的推进/`；若缺失，按 [progress-abstract/README.md](progress-abstract/README.md) 的结构说明初始化。
3. **读取近期修改索引**：读取 `progress-abstract/index.md`，掌握近期修改内容。

## 记忆维护规则（progress-abstract）

- 维护规则以 [progress-abstract/README.md](progress-abstract/README.md) 为唯一权威来源，本文件只保留要点。
- 要点：新增修改内容默认在 `仍然需要留意的推进/` 新建记录并更新 `index.md`；记录过时或无影响时移入 `已过时的推进/` 并更新 `index.md`；新建/调整记录或索引前先询问用户；判断模块/子任务已完成时，向用户提出载入 schedule 进度（由用户决定）。
- 除 `README.md` 外，`progress-abstract/` 内容不纳入 git 追踪、不随仓库共享；协作方以 `project-schedule.md` 中的模块进度为准。

## Git 管理规则

- **编译/生成内容禁止入库**：`Library/`、`Logs/`、`Temp/`、`Obj/`、`Build/`、`Builds/`、`UserSettings/` 以及 `*.sln`、`*.csproj` 等 IDE/编译产物一律不追踪（由 `.gitignore` 保证）。
- **大资产走 LFS**：超过 20MB 的资产必须由 Git LFS 管理；`.gitattributes` 已覆盖常见二进制类型，另有 pre-commit 钩子拦截未走 LFS 的超限文件。
- **提交规范**：提交保持小而完整、语义清晰，提交信息使用中文。
- **分支策略**：每次开发在独立分支上进行，分支命名 `feature/<用户名>-<功能>`；完成后推送远端分支并开 PR，由管理员 Squash and merge 合入 main；合并后回到 main 同步并删除本地分支。main 的保护规则由管理员在 GitHub 规则集中配置。

## Git 提交/推送时机

1. **先判断**：本次修改是否需要 git 管理，依据是修改内容的重要程度、距上一次 git 操作的时间间隔。
2. **默认阈值**：距上次 git 超过一天、且修改内容足以形成一次有意义的提交时，主动向用户提出提交/推送。
3. **再询问**：用文字询问用户是否执行 git 提交/推送，得到明确答复后再操作；不得直接申请提权或直接推送。
- 环境建议：Codex 权限模式请使用“请求批准”（Ask for approval）；自动审批（Auto-review）模式下，git 提权请求可能被阻塞。

## 工作约定

- 开发引擎：Unity 2022.3.62f3c1（URP）。
- 沟通语言：与用户沟通默认使用中文。

## 文档索引

- 游戏设计拆解案：[game-design.md](game-design.md)
- 模块进度与分工：[project-schedule.md](project-schedule.md)，由用户维护，仅在用户要求时协助更新。
- 记忆结构说明：[progress-abstract/README.md](progress-abstract/README.md)（随仓库追踪）。
