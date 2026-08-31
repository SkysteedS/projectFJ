# Unity Humanoid Animation Reference

Reference for the `unity-anim-edit` skill. Load this when you need the detailed
muscle naming, sign conventions, YAML structure, or rig caveats. The main workflow
is in `SKILL.md`.

## 1. YAML structure of a humanoid AnimationClip

The `.anim` is YAML with `%TAG !u! tag:unity3d.com,2011:` and the object header
`--- !u!74 &7400000 AnimationClip` (classid 74 = AnimationClip).

Top-level fields seen in the wild:

```
m_Name, serializedVersion, m_Legacy, m_Compressed, m_UseHighQualityCurve,
m_RotationCurves, m_CompressedRotationCurves, m_EulerCurves, m_PositionCurves,
m_ScaleCurves, m_FloatCurves, m_PPtrCurves, m_SampleRate, m_WrapMode, m_Bounds,
m_ClipBindingConstant, m_AnimationClipSettings, m_EditorCurves,
m_EulerEditorCurves, m_HasGenericRootTransform, m_HasMotionFloatCurves, m_Events
```

For a **humanoid** clip `m_RotationCurves`, `m_PositionCurves`,
`m_CompressedRotationCurves`, and `m_ScaleCurves` are typically `[]`; the pose
data lives in `m_FloatCurves` (runtime) and `m_EditorCurves` (editor preview).
**Caveat:** some clips have `m_EditorCurves: []` (scripted/legacy-saved clips) —
if so, only `m_FloatCurves` exists and that is the only section to edit. Check
the top-level fields before assuming two sections.

### Muscle-curve entry

```yaml
- serializedVersion: 2
  curve:
    serializedVersion: 2
    m_Curve:
    - serializedVersion: 3
      time: 0
      value: -0.0101185795
      inSlope: -0.00034525056
      outSlope: -0.00034525056
      tangentMode: 0
      weightedMode: 0
      inWeight: 0.33333334
      outWeight: 0.33333334
    m_PreInfinity: 2
    m_PostInfinity: 2
    m_RotationOrder: 4
  attribute: RootT.x
  path:
  classID: 95
  script: {fileID: 0}
```

- `attribute` is the humanoid muscle name (see below).
- `classID: 95` = Animator. `path` is empty for muscle curves (they bind to the
  Animator muscle data, not a bone path).

### Parsing gotcha (important)

The keyframe `value:` lines come **before** the `attribute:` line for the same
entry. A naive script that reads `attribute` then looks for values will find
nothing. Handle it by buffering per entry:

- On a new `  - serializedVersion: 2` (2-space indent) → flush the previous entry.
- Accumulate `time:`/`value:` pairs and their line indices for the current entry.
- When you hit `    attribute: X`, assign the buffered values to muscle `X`.
- At the end, flush the last entry.

## 2. Muscle value semantics (measured, rig-dependent)

- **Rotational muscles** (`Front-Back`, `Down-Up`, `Left-Right`, `Twist`,
  `Stretch`, `Nod`, etc.): values are **Unity-normalized muscle values**, not
  degrees. The neutral/should-be-straight pose is often non-zero (e.g. idle
  `Forearm Stretch` ≈ 0.98). Values can exceed `|1|` (walking thigh hit +1.09).
- `RootQ.*` = root bone rotation as **quaternion components** (x, y, z, w).
  - `RootQ.x` = pitch (positive = forward lean), `RootQ.y` ≈ yaw, `RootQ.z` ≈ roll.
- `RootT.*` = root translation in **meters**.
- `*FootT.*`, `*HandT.*` = meters. `*FootQ.*`, `*HandQ.*` = quaternion components.
- Unity normalizes quaternions when applying, so a small component edit is fine.

## 3. Bone hierarchy dependency (the core trap)

### Canonical model (per Unity docs)

There is **no universal "this value = up/down/forward" table** in Unity's
documentation. The authoritative framing is:

- A humanoid clip stores muscle values (see Unity Scripting API
  `HumanPose.muscles`, a `float[95]`) plus root motion (`RootT`/`RootQ`, see
  Unity Manual *Root Motion*). The muscle space and each muscle's min/max are
  defined by the Avatar's humanoid configuration (Unity Manual *Humanoid Avatar*).
- Each muscle value is a rotation about that bone's **local axis**, and it is
  **applied relative to the bone's parent in the hierarchy**. It is **not** an
  absolute world orientation.
- World orientation of a bone = the rotation accumulated through its parent chain
  × this bone's local muscle rotation. So changing a limb's world pose requires
  reasoning about the parent (e.g. shoulder) first.

Cite: [`HumanPose.muscles`](https://docs.unity3d.com/Documentation/ScriptReference/HumanPose-muscles.html),
[Root Motion](https://docs.unity3d.com/Manual/RootMotion.html),
[Humanoid Avatar](https://docs.unity3d.com/6/Documentation/Manual/AvatarCreationandSetup.html).

Because the direction depends on the model's bone orientations/bind pose, any
direction you observe on one model is an **observation to verify**, never a rule.

Humanoid muscle values are **local rotations relative to the parent bone**, not
absolute world orientations.

```
Arm chain:   shoulder  →  upper arm (Arm)  →  forearm  →  hand
Leg chain:   root      →  upper leg        →  lower leg →  foot
```

The world orientation of the upper arm = shoulder pose × upper-arm local rotation.
So to raise the upper arm to horizontal in world space you must settle the parent
(shoulder) first; otherwise the arm either won't raise or will clamp inward.

The **elbow bend direction** (up/down/in/out) is governed by the upper arm's
`Arm Twist In-Out` (it sets the elbow hinge plane). The forearm's own
`Forearm Twist In-Out` only spins the hand/palm axially — it changes the palm
orientation but **cannot** change which way the elbow folds.

`Arm Down-Up` is **abduction** (leading the arm out/up laterally); combining it
with `Arm Front-Back` (forward/back swing) is what raises the arm forward to
horizontal. Treat these as coupled, not independent.

## 4. Sign behavior — model-specific observations (NOT authoritative)

> ⚠️ Unity has **no universal sign table**. The rows below are behaviors observed
> on ONE specific model; because muscle values are parent-relative local-axis
> rotations, the actual world direction depends on that model's bone orientations
> and bind pose. **Re-verify each per rig** by previewing in the editor (or asking
> the user) rather than trusting these as rules.

| Muscle | Positive means | Note |
|---|---|---|
| `Arm Front-Back` | swing BACK | negative = forward |
| `Arm Down-Up` | raise arm OUT/up (abduction, widens) | not sagittal raise |
| `Spine/Chest/UpperChest Front-Back` | lean BACK | negative = lean forward |
| `Upper Leg Front-Back` | thigh forward (hip flex) | — |
| `Lower Leg Stretch` | extend/straighten | **negative = bend/flex** |
| `Forearm Stretch` | extend/straighten | **negative = bend/flex** |
| `Foot Up-Down` | dorsiflex (toes up) | — |
| `Arm Twist In-Out` | rotates elbow bend plane | up/down/in/out of forearm |
| `RootQ.x` | pitch (forward) | whole-body forward lean |

Signs can differ per rig. Where you cannot render, treat any value you choose as
an approximation and let the user confirm the direction in the editor.

## 5. Reusable editing patterns

- **Preserve breathing**: add a constant delta to every keyframe of a muscle.
  This shifts the pose baseline while keeping the curve shape (breathing).
  Never hand-edit each keyframe for a pose change.
- **Left/right symmetry**: applying the SAME delta to Left and Right is wrong
  because their base values differ. To make limbs parallel, choose a reference
  side, and set the other side's absolute value equal to the reference side's:
  `per_side_delta = target_abs − that_side_base`.
- **Whole-body tilt**: adjust `RootQ.x` (pitch) / `RootQ.z` (roll); nudge
  `RootQ.w` to keep the quaternion near-normalized.
- **Update both sections** (`m_FloatCurves` and `m_EditorCurves`) identically.
- **Rename `m_Name`** to the new clip name.

## 6. Common "looks wrong" symptoms → likely cause

| Symptom | Likely cause |
|---|---|
| arms swing backward when told to go forward | `Arm Front-Back` sign is inverted (use negative for forward) |
| arm not raised to horizontal (still down) | parent shoulder not set; `Arm Down-Up` is abduction, combine with `Arm Front-Back` |
| arms clamp / cross inward | overshooting `Arm Front-Back` or forgetting the shoulder parent |
| forearm folds DOWN instead of UP | `Arm Twist In-Out` sets the fold direction — flip it |
| forearm not perpendicular (still straight) | `Forearm Stretch` still positive (must go negative to bend) |
| palm rotated / pronated | `Forearm Twist In-Out` (axial palm spin) |
| left/right not parallel | base values differ; set absolute values equal |
| whole body leaned/tilted | `RootQ` pitch (x) / roll (z) |
| arm raised but still horizontal / "raised a bit too much" | the °/unit response rate was guessed (nonlinear on this rig, 44→57.5°/unit); calibrate from 2+ measurements, fit quadratic |
| arms spread too wide / need to come toward the midline | azimuth is `Arm Front-Back` (negative = forward swing), NOT `Arm Down-Up`; Down-Up only changes coronal elevation |
| forearm horizontal although upper arm already diagonal up | the elbow fold plane points forward/slightly-down (hinge `w` has −y); needs `Arm Twist In-Out` re-pointing, not just more bend |
| two forearms should be parallel & forward | set the **same world target direction** for both sides and solve per side (`references/arm-chain-reverse-solve.md`) |
| clip imports as EMPTY (Inspector: main object name '' / Curves: 0 / "should match the asset filename" warning) | the file has stray `\r` line breaks (e.g. `\r\r\n` from bad EOL handling) — Unity's YAML parser fails silently and produces an empty AnimationClip; see "Writing .anim safely" below |

## 6b. Writing .anim safely (EOL damage is the classic "it won't import" bug)

A clip whose YAML fails to parse imports as an **empty AnimationClip**: no name,
`Curves: 0`, and the warning "The main object name "" should match the asset
filename". The usual cause is line-ending corruption, not the muscle content:

- Reading `split("\n")` keeps each element's trailing `\r`; joining again with
  `\r\n` produces `\r\r\n` on **every** line. Unity's YAML parser rejects the
  stray `\r` → whole object fails to deserialize.
- **Safe recipe:** strip the trailing `\r` from every line after the split and
  only then join with the detected EOL (`"\r\n"` if the source had CRLF);
  build the whole file in memory, write to a temp file, `flush`+`fsync`, then
  `os.replace` onto the target (atomic — Unity's file watcher can never read a
  half-written file); write the `.meta` LAST. (`scripts/anim_tools.py apply`
  does all of this.)
- **Verify, and don't be fooled:** compare lines EXACTLY (no `rstrip('\r')` —
  that masks `\r\r\n` vs `\r\n`!) and assert the written bytes contain **zero**
  lone `\r` (`re.findall(r"\r(?!\n)", text)` must be empty, and CRLF count must
  equal the total line count). Keep backups OUTSIDE `Assets/` (e.g. in `Temp/`),
  or Unity will import them.
- Editing a clip **in place** (same path, same meta GUID) is safe and avoids
  GUID churn; the skill's "copy + fresh GUID" rule applies to the first
  deliverable, later iterations can keep the identity.

## 6c. Twist sign for left/right (observed on this rig)

Mirrored rotations of L/R elbow planes corresponded to **same-sign** `Arm Twist
In-Out` deltas (left needed +29°, right −23.8° world about their own axes; both
got +0.48/+0.40). Treatment: solve each side independently, express both
magnitudes from the geometry, apply the chosen sign to both, and expect one
sign-flip if the fold lands down/outward. Direction observations stay per-rig.

## 7. Git / LFS caveat (when committing)

`.anim` files are typically **Git-LFS tracked** (`.gitattributes`:
`*.anim filter=lfs diff=lfs merge=lfs -text`), and the repo may configure git
hooks (`core.hooksPath`, `pre-commit`/`pre-push`) that spawn `sh.exe`. In a
restricted sandbox, `git add`/`git commit` on `.anim` files can fail because the
LFS clean filter and hooks need subprocess + named-pipe access (error like
`sh.exe: couldn't create signal pipe ... Win32 error 5`). Non-LFS files stage
fine. If you must commit from a sandbox, escalate permissions to run the LFS
filter and hooks; otherwise ask the user to run the commit in their own terminal.

## 8. Verification limits

You cannot render a humanoid pose from a `.anim` here. Verify by:
- structural signature (identical except `value` lines + `m_Name`),
- value counts (unchanged),
- `min ≠ max` on changed muscles (breathing preserved),
- Left/Right equality for symmetry.

Then hand off to the user to preview in Unity and report back with the specific
issue (screenshot / direction), and iterate one dependency layer at a time.
