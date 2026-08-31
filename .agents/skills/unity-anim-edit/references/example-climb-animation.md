# Worked example: re-posing a standing idle into a climbing hold

A realistic end-to-end pass of `unity-anim-edit`. Use this as a template for how
to reason about a pose edit, where the sign/direction is **model-specific** and
must be verified, and how to iterate one dependency layer at a time.

## The task

Given `idle.anim` (a Humanoid standing idle, legs slightly apart, with subtle
breathing), author a **climbing hold**: spine leaning forward, upper body
inclined, **hands pressing a wall** (upper arms raised to ~horizontal, forearms
bent up ~90° vertical and perpendicular to the upper arm), knees bent with feet
on the wall, whole body tilted slightly forward, left/right symmetric.

Deliverable: a copy of the clip (not overwriting the source) that keeps the
breathing, with a change report.

## Step 0 — Locate and confirm

- The file was at a nested path; the Unity project root differed from the shell
  working directory. Verified with the filesystem.
- Confirmed Humanoid: `m_RotationCurves`/`m_PositionCurves` were `[]`; pose data
  lived in `m_FloatCurves` (and mirrored in `m_EditorCurves`).

## Step 1 — Parse

- Key gotcha: each curve's `value:` keyframes appear **before** its `attribute:`
  line, so values must be buffered per entry and paired at the `attribute` line.
- Rebuilt a map `attribute → [values, line_indexes]` covering **both** sections.

## Step 2 — Analyze baseline

Read the standing baseline, e.g. (time-0 values):

```
Spine Front-Back      -0.084     Chest Front-Back   -0.029     UpperChest Front-Back -0.057
Left Arm Front-Back    0.238     Left Arm Down-Up   -0.298     Left Forearm Stretch   0.982
Right Arm Front-Back   0.229     Right Arm Down-Up  -0.161     Right Forearm Stretch  0.938
Left Upper Leg Front-Back  0.437  Right Upper Leg Front-Back 0.615
Left Lower Leg Stretch      0.837 Right Lower Leg Stretch   0.907
RootQ.x 0.008  RootQ.y 0.040  RootQ.z 0.004  RootQ.w 0.999
```

Note the **left and right bases differ** (e.g. Arm Down-Up −0.298 vs −0.161), so
any pose change that aims for symmetry must account for that.

## Step 3 — Copy, don't mutate the source

- Copied `idle.anim` → new file, wrote a `.meta` with a fresh GUID, renamed
  `m_Name` to the new clip name.
- All later edits were **constant deltas added to every keyframe** of the chosen
  muscles — the whole baseline shifts while the breathing shape is preserved.

## Step 4 — Iterate, one dependency layer at a time

This is where the real reasoning happens. Each row is a real iteration and the
lesson learned. Directions below are **observations on this specific model** —
re-verify per rig, because muscle values are parent-relative local-axis rotations.

| Attempted | Observed result | Lesson |
|---|---|---|
| `Arm Front-Back` positive (said "forward") | arms swung BACK | positive = backward on this model; negative = forward. Not a universal rule. |
| strong negative `Arm Front-Back`, arm low | arms crossed / clamped inward | overshooting forward + low arm converges the hands. |
| raise via `Arm Down-Up` alone | arms splayed wide (~150°) | `Arm Down-Up` is **abduction** (out/up), not a sagittal raise — combine with `Front-Back`. |
| raise the upper arm to horizontal | wouldn't raise; still down | muscle value is **parent-relative**: upper arm's world pose = shoulder × arm local. Settle the **shoulder (parent)** first. |
| increase `Forearm Stretch` to bend elbow | forearm stayed straight | `Stretch` positive = **extend/straighten**; to bend you must **decrease** it (make negative). |
| forearm bent, but downward | folded DOWN | the elbow fold **plane** is set by the upper arm's `Arm Twist In-Out`; flip it to fold UP. |
| `Forearm Twist In-Out` to fix the fold | only the palm spun/pronated | forearm Twist is **axial (palm orientation)**; it can't change the fold direction. |
| palm pronated | palm faced up | `Forearm Twist In-Out` corrects the palm; rotate it the needed way. |
| left ≠ right arms/legs | "not parallel" | separate **absolute** values (bases differ). Set `side_abs = reference_side_abs`, per-side delta = `target_abs − base`. |
| whole body leaned back / tilted left | body fell over | `RootQ.x` = pitch (forward), `RootQ.y`/`RootQ.z` = yaw/roll — adjust these for whole-body orientation. |
| lower legs too together | shins pinched | `Upper Leg In-Out` too small; open both by the same amount to keep symmetry. |

## Step 5 — Script-assisted editing

Because the change was a uniform baseline shift over many muscles, the work was
done by a small parser that:

1. Read the `.anim` lines.
2. Buffered each entry's value lines and paired them to `attribute`.
3. For each muscle in a `delta` map, added the delta to **every** value line.
4. Applied the same absolute-target trick for left/right symmetry (reference
   side set, other side matched).
5. Wrote back with an invariant-culture float format, and updated **both**
   `m_FloatCurves` and `m_EditorCurves`.

Write the parsing script rather than hand-editing each keyframe — it's the only
sane way to preserve breathing and hit every keyframe consistently.

## Step 6 — Verify structurally (you can't render)

- Structural signature identical except `value` lines + `m_Name` → `True`.
- `value:` line count unchanged (4006 = 4006), top-level fields unchanged (26 = 26).
- Each changed muscle showed `min ≠ max` (breathing preserved).
- Left/Right absolute values matched (diff ≈ 0) after symmetrizing.

## Step 7 — Hand off to the user + report

Produced a change report (per muscle old baseline → new baseline, delta applied),
confirmed what was **not** changed (bones/lengths/rig are not stored in a humanoid
`.anim`; they live on the Avatar), and asked the user to preview in the editor and
report the specific direction. Then iterated on the exact reported issue.

## Final result (baselines, this model)

```
Spine Front-Back           -0.734   Chest Front-Back      -0.449   UpperChest Front-Back -0.477
Arm Front-Back             -0.621 (L=R)   Arm Down-Up  0.789 (L=R)   Arm Twist In-Out 0.637 (L=R)
Forearm Stretch             0.218   Forearm Twist In-Out -0.911 (L=R)
Shoulder Front-Back        -1.093
Upper Leg Front-Back        0.115   Lower Leg Stretch     0.457   Foot Up-Down 0.125
RootQ.x  0.108 (forward pitch)  RootQ.y 0.063  RootQ.z 0.009
```

Breathing preserved; structure intact; left/right symmetric. The clip was later
renamed/moved to `climb/climb.anim` in the project (metadata `m_Name` updated).
