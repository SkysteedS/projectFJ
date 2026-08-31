---
name: unity-anim-edit
description: >-
  Edit and analyze Unity humanoid AnimationClip (.anim) files by rewriting their
  humanoid muscle curves. Use this whenever the user asks to change, re-pose, fix,
  symmetrize, or "make parallel" a Unity animation; says an animation "looks
  wrong", "is too bent", "is leaned", or "clamps"; references a .anim file, a
  humanoid bone/muscle name, or a task that needs understanding of bone hierarchy.
  It parses the .anim YAML, maps the desired pose to muscle-value deltas, applies
  them as a constant baseline shift (preserving breathing), and hands off visual
  verification to the user because it cannot render the pose itself.
  Make sure to use this skill whenever the user references Unity animations,
  .anim clips, rig poses, humanoid muscles, or wants a pose fixed/adjusted — even
  if they do not explicitly say "skill" or "animation".
---

# Unity Humanoid Animation (.anim) Muscle Editing

## Goal

Modify a Unity humanoid `.anim` by shifting its muscle-curve baselines while (a)
preserving the clip's existing breathing/oscillation, (b) respecting the bone
hierarchy, and (c) producing a clear change report. You **cannot render the
pose** — you verify structurally and numerically, then the user previews in
Unity and gives the final verdict.

## Pipeline at a glance

Every step is either a script command or the companion measurement tool; the
skill never hand-edits keyframes.

```
1 定位/确认  anim_tools.py info          —— 是否 humanoid、曲线段、帧数、循环
2 采集度量  Unity 工具(A/B/C) + bones    —— 世界方向/仰角/方位角/折叠平面
3 诊断+策略  数值 → 问题 → 单层估算 or 反解
4 计算增量  calibrate(响应率) + solve(θ/φ) —— 或直接按符号表估
5 安全写入  anim_tools.py apply          —— 常量偏移、EOL 安全、备份
6 结构验证  anim_tools.py diff           —— 逐行精确 diff + 孤立CR=0
7 用户确认  预览/复采 → 反馈 → 微量迭代
```

Scripts live in `scripts/` (pure stdlib, see `scripts/README.md`):
`info` `diff` `apply` `bones` `solve` `calibrate`.

## 1. Locate and confirm the file

- Paths nest; the Unity project root may differ from the working directory —
  verify with the filesystem, not assumptions. `anim_tools.py info <clip>`
  confirms the rest: humanoid clips have `m_RotationCurves`/`m_PositionCurves`
  as `[]` and pose data in `m_FloatCurves`. If bone transforms are in
  `m_RotationCurves` directly, it is NOT humanoid muscle curves — stop.
- Check which curve sections actually exist: `m_EditorCurves` can be `[]`
  (scripted clips) — then `m_FloatCurves` is the only section to edit.
- Note clip length: some clips are 1–2 frames (`m_StopTime ≈ 0.033`) static
  loops — a "breathing" to preserve may be just the 2-keyframe variation.

## 2. Measure (do this FIRST whenever the model + clip are available)

The project's `AnimComputeWindow.cs` (Tools → Animate Compute → 骨骼信息采集)
writes `Temp/anim_bone_info.txt`:

- **A** = muscle values read from curves (ground truth),
- **B** = per-bone world/local transforms of the applied pose,
- **C** = `GetHumanPose` read-back (C ≈ A ⇒ the pose applied, B trustworthy).

**Snapshot every run:** the tool overwrites the same file, and later
calibration needs the history — `anim_tools.py bones <file> --save <label>`
copies it to `<name>_<label>.txt`. **Do not archive under `Temp/`** — Unity
cleans/wipes it on exit (it disappeared mid-session here); use the repo root or
any persistent folder outside `Assets/`.

`anim_tools.py bones <file>` turns section B into the numbers you reason with:
segment directions, elevations (`asin(dy)`), azimuths (`atan2(dx, dz)`), elbow
bend angle, and the elbow fold plane `n`/`w`.

### Why "direction" is measured, never assumed

Muscle values are **normalized rotations about each bone's LOCAL axis, composed
with the parent chain** — there is no universal "value = world direction" table;
±signs are per-rig observations (see `references/humanoid-anim.md` §4). The tool
turns "guess → preview → adjust" into "measure → change → re-measure".

## 3. Diagnose and pick the strategy

Map the user's words to numbers first:

| user says | measure |
|---|---|
| "hands below/above shoulders" | upper-arm elevation sign |
| "arms too wide / toward the midline" | azimuth; driven by `Arm Front-Back`, not `Arm Down-Up` |
| "forearm horizontal while upper arm diagonal" | elbow fold plane `w`; elevation ceiling of the plane |
| "forearms parallel, forward-up" | same world target m for both sides; `m·n ≈ 0` ⇒ no twist needed |
| "raised but not enough / too much" | response rate (see step 4) |

Then choose the strategy:

- **Verbal direction, no measurements** → estimate ONE muscle, tell the user
  the exact name, iterate one dependency layer (settle the parent — shoulder —
  before the child — arm). Don't batch unknowns.
- **Precise geometric target + measurements** → calibration + reverse solve
  (step 4), which can solve several muscles in one pass; still flag every axis
  whose rate/sign is uncalibrated (typically `Arm Twist`) and expect one
  correction round.

**Shortcut: judge sign and neutral pose from sibling clips.** Same-rig clips
(e.g. `jump down` vs `jump loop`) show how muscle value 0 / a known value maps
to geometry — on the jump rig, `Shoulder+Arm Down-Up ≈ 0` meant "arms
horizontal", which pinned the first calibration anchor without any extra
measurement.

## 4. Compute the deltas

- **Baseline shift (the default tool):** add a constant delta to EVERY keyframe
  of the chosen muscles — the pose baseline moves, the breathing shape is kept.
  Never hand-edit individual keyframes.
- **Symmetrize:** L/R base values differ, so the same delta keeps asymmetry;
  to truly mirror, set the other side's ABSOLUTE value to the reference side's.
- **Calibrate each muscle's °/unit rate** (rig-specific, nonlinear — measured
  44 → 57.5°/unit as the arm on one rig; quadratic fit through 3 measured
  points worked): `anim_tools.py calibrate --state "net,file" ...`.
- **Prediction is only for the first pass.** A fitted formula is a model; the
  next measurement is the truth (predicted +17°, measured +13.6° on the jump
  rig). From the second iteration on, solve from the LATEST measured state —
  that converged in one round, while formula-based passes needed two.
- **Reverse solve for precise targets:** `anim_tools.py solve <bone_info>
  --upper-az-delta D --target-elev E --target-az A` returns the elbow bend θ,
  plane twist φ and the resulting `Front-Back`/`Twist`/`Stretch` deltas.
  Math + real-numbered worked example: `references/arm-chain-reverse-solve.md`.
- Whole-body pitch/roll: `RootQ.x` (pitch) / `RootQ.z` (roll), nudge `RootQ.w`.

Rates are per-rig defaults in the scripts' flags (`--rate-*`, `--*-sign`) —
when an iteration's measurement contradicts them, update the flags, not the code.

## 5. Apply

- **First deliverable:** copy the `.anim`, create a `.meta` with a fresh GUID,
  rename `m_Name`. **Later iterations:** edit the copy in place (same meta GUID
  — no reference breakage). Don't overwrite the source unless asked.
- `anim_tools.py apply <clip> --deltas "Muscle:delta,..." [--name X]` — constant
  offsets, EOL-safe **atomic write** (temp file + fsync + rename; write `.meta`
  last), automatic `<file>.bak` backup, and built-in verification.

### EOL safety (why files suddenly "don't import")

Unity's YAML parser fails silently on a stray `\r` — a `\r\r\n` file imports as
an **EMPTY clip** (no name, `Curves: 0`, "The main object name '' should match
the asset filename"). Cause: splitting on `\n` keeps each element's trailing
`\r`, and re-joining with `\r\n` doubles it. `apply` strips the `\r` before
reconstructing; if you ever write by hand, do the same and never `rstrip('\r')`
in verification (it masks this bug). Details: `references/humanoid-anim.md` §6b.

## 6. Verify structurally

- `anim_tools.py diff <new> <source>`: exact per-line diff (expect ONLY the
  planned value lines + `m_Name`), same `value:` count, and **lone-CR count = 0**.
- New baselines with `min ≠ max` per changed muscle (breathing kept).
- Re-run the measurement tool on the copy: C ≈ A and B shows the intended
  geometry. Keep iteration backups in the repo root / workspace — **never in
  `Temp/`** (Unity wipes it) and never as a new `.anim` inside `Assets/`.

### In Unity after each write

Click the Editor window to trigger the re-import (same path + same meta GUID
means assets refresh automatically). If the console shows the "main object
name ''" warning or the clip looks empty, right-click the asset → **Reimport**
once; if it persists, run the lone-CR check on the bytes first — corruption is
probable, the importer is not.

## 7. Hand off to the user

- Report: new file path, `.meta` GUID, per-muscle table (old baseline → new
  baseline, delta), what was NOT changed (bones/lengths/rig live on the Avatar,
  not in a humanoid `.anim`), and how it was verified.
- **The final effect is always the user's verdict** — tool data proves the
  change was applied and measured, not that it looks good. The user may accept
  approximations ("near-vertical is fine") or report the next direction
  (higher/lower, in/out, fold direction); refine, one layer at a time, and let
  measurements accumulate into the per-rig calibration.

## References & scripts

- `scripts/README.md` — anim_tools commands, per-rig flags, safety guarantees
- `references/humanoid-anim.md` — YAML structure, per-model sign observations,
  symptom → cause table, EOL/import safety, L/R twist-mirror observation
- `references/anim-compute-tool.md` — tool usage and the reliable pose-apply path
- `references/example-climb-animation.md` — worked walk-through of the
  estimate/iterate style (parent-relative pitfalls in context)
- `references/arm-chain-reverse-solve.md` — measured calibration loop and the
  elbow fold-plane reverse solve, with the real jump-loop numbers
