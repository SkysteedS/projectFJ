# anim_tools — programmatic helpers for the unity-anim-edit skill

Pure-stdlib Python 3.7+ tools (no numpy, no pip installs). Everything a pose
edit needs is scripted so the skill never hand-edits keyframes again:

```
anim_tools.py info      .anim 结构解析（属性/关键帧/时长/循环/段）
anim_tools.py diff      a b   逐行精确 diff + EOL 损坏检查
anim_tools.py apply     .anim --deltas "Mus:delta,..." [--name X]
                           常量基线偏移（呼吸保留）、EOL 安全原子写入、自动备份
anim_tools.py bones     anim_bone_info.txt
                           工具输出解析：方向/仰角/方位角/肘部折叠平面 (n,w)/肌值
anim_tools.py solve     anim_bone_info.txt [--upper-az-delta D] [--target-elev E]
                           [--target-az A] [--rate-* ] [--*-sign]
                           反解：目标小臂世界方向 → 弯角θ/平面扭转φ → 肌肉增量
anim_tools.py calibrate --state "net,file" ... [--side Left] [--query net]
                           2-3 个实测状态拟合 仰角(net) 响应率（线性/二次）
```

## The loop it supports (each step is a command)

1. 采集：Unity 内 Tools → Animate Compute → 骨骼信息采集 → `Temp/anim_bone_info.txt`
2. 诊断：`python anim_tools.py bones <file>` — 看仰角/方位角/折叠平面
3. 标定：`calibrate --state "-0.25,stateA" --state "0.30,stateB" --state "0.98,stateC" --query 0.63`
   — 响应率（本 rig 实测 44→57.5°/单位，非线性，3 点用二次拟合）
4. 反解：`solve <file> --upper-az-delta 18 --target-elev 25 --target-az 0`
   — 输出每侧 Front-Back / Twist / Stretch 增量
5. 应用：`apply <anim> --deltas "Left Arm Front-Back:-0.30,Left Forearm Stretch:-0.28,..."`
   — 原子写入 + 备份 + 自校验（lone CR 必须为 0）
6. 复核：`diff <anim> <备份>` — 只应有预期数量的 value 行差异

## Per-rig parameters (measured rates beat defaults)

- `--rate-frontback` / `--rate-twist` (default 60 °/unit) — ESTIMATES for axes
  never calibrated on the rig; the next measurement pins them down.
- `--stretch-calib "stretch:bend,stretch:bend"` — measured (Stretch, elbow-bend°)
  pairs; defaults to the jump-rig calibration [(0.892, 11.6), (0.142, 71.7)].
- `--fb-sign 1` (default; negative = forward on the jump rig — flip if the
  swing goes the wrong way) and `--twist-sign 1` (positive = folds up per the
  climb experience — flip if the fold lands down/outward; note L/R twist axes
  are mirrored, so both sides use the SAME sign).
- `--side Left` for calibrate (Left/Right differ by a few degrees on asymmetric
  rigs: same muscles gave +13.6° vs +18.4° on this one).

## Safety guarantees

- `apply` writes via temp file + fsync + atomic rename (Unity's watcher can
  never read a half-written file), strips stray `\r` before EOL reconstruction
  (never emit `\r\r\n` — that silently breaks Unity's YAML parser and imports
  an EMPTY clip), and keeps a `<file>.bak` backup (`.bak` is not imported).
- `diff` compares lines EXACTLY and reports lone-CR/CRLF/LF counts — never
  `rstrip('\r')` before comparing (it masks EOL corruption).
