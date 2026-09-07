# AnimComputeWindow — 数据化采集/验证工具

Reference for the `unity-anim-edit` skill. The tool is the companion
measurement instrument for this skill: it turns abstract normalized muscle values
into measured bone world orientations, and verifies pose application with data
instead of guesswork.

Tool script: `Assets/Scripts/animate compute/AnimComputeWindow.cs`
Menu: **Tools → Animate Compute → 骨骼信息采集**（窗口：模型 Prefab + 动画 Clip + 采样时间）
Output: `Temp/anim_bone_info.txt`（保存后自动打开所在目录）

## 1. Why the tool exists (意义)

A humanoid `.anim` stores **normalized muscle values** that are rotations about
each bone's LOCAL axis, relative to the parent bone. The values alone don't tell
you the actual world orientation of any bone — that depends on the rig's bind
pose. So plan changes were previously "guess a delta → user previews → adjust".

The tool closes that gap:

- **Section A** reads the clip's muscle curves **directly** — always the real
  values, never dependent on pose application.
- **Section B** shows each humanoid bone's local/world position + rotation for
  the clip's pose, so you can measure the CURRENT world geometry (e.g. is the
  forearm horizontal? compute `hand.pos − forearm.pos` and check the Y component).
- **Section C** re-derives muscles with `GetHumanPose` after application — if
  C ≈ A, the pose applied correctly and B is trustworthy.

With A + B + C you can: confirm a diagnosis in numbers, apply a change, and
re-measure the result — a closed data loop instead of eyeball iteration. (It does
not, by itself, compute the ideal delta: the change can still be an experience-
based estimate or a true reverse-solve; the tool makes both verifiable.)

## 2. How to use (如何使用)

1. Open the window (menu above).
2. Pick the **model Prefab** — must be Humanoid (`Animator.avatar.isHuman`); the
   avatar used is exactly this model's avatar (FBX-imported humanoid config).
3. Pick the **AnimationClip** to analyze (the one you're about to modify).
4. Set **采样时间** (seconds; 0 = first frame).
5. Click **采集骨骼信息并保存** → reads `Temp/anim_bone_info.txt`.

Output sections and what they mean:

| Section | Content | Trust |
|---|---|---|
| A | Clip muscle values, read directly from curves (+ `RootT`/`RootQ`) | Always correct; the ground truth |
| B | Per-bone `localPos/localRot` + `worldPos/worldRot` after applying the clip | Trustworthy only if C ≈ A |
| C | `GetHumanPose` read-back, printed beside A for each muscle | C≈A means the pose applied |

Quick geometric checks that make the loop data-driven:

- Forearm "parallel to ground"? `dir = hand.worldPos − lowerArm.worldPos`; if
  `|dir.y| ≈ 0` it is horizontal; if `≈ ±|dir|` it is vertical.
- Upper arm angle between the two arms / "raised to horizontal": compare the
  two upper arm direction vectors.
- "Parallel / mirror left-right": compare Left vs Right `worldRotEul` (mirrored
  Y/Z complements) or the direction vectors.

## 3. Implementation lessons (measured, important)

Getting a humanoid pose APPLIED in the editor is the hard part. In this
environment the only reliable recipe was:

```csharp
animator.enabled = true;
animator.Rebind();
var handler = new HumanPoseHandler(animator.avatar, animator.transform); // root = animator.transform, NOT hips
var pose = new HumanPose();
handler.GetHumanPose(ref pose);            // current (bind) as base
// overwrite muscles from clip curves + body from RootQ/RootT
handler.SetHumanPose(ref pose);            // writes straight to the bones
```

Things that did **NOT** work here (each produced a silent no-op / wrong pose):

- `new AnimatorController()` has **no layers** — `layers[0]` throws
  `IndexOutOfRangeException`; `AddLayer("Base")` returns `void`.
- In-memory controllers assigned to `runtimeAnimatorController` did not replace
  the prefab's own controller; `animator.Update` then drove the prefab's game
  state, not the clip.
- `AnimationMode.SampleAnimationClip` did not drive humanoid clips here.
- `HumanPoseHandler` root must be the transform owning the skeleton
  (`animator.transform`), **not** `GetBoneTransform(Hips)` — with the wrong root,
  `SetHumanPose` is a no-op and `GetHumanPose` keeps returning the bind pose.

Also: `HumanTrait.MuscleName` / `MuscleCount` give the 95 muscle names used in
the clip curves; `AnimationUtility.GetEditorCurve(clip,
EditorCurveBinding.FloatCurve("", typeof(Animator), name)).Evaluate(t)` reads a
muscle curve reliably. Twist muscles may not round-trip exactly (C differs from A
by a few percent on Twist bones; that is a solver approximation, not an error).

## 4. The final effect is ALWAYS confirmed by the user

The tool's data is measurement and verification — it is **not** the arbiter of
"looks good". Even when C ≈ A and B shows the intended geometry, the change must
be confirmed by previewing in Unity (the user judges; screenshot/short description
comes back). The loop is:

1. Analyze source (A/B) → diagnose in numbers.
2. Apply the change (estimate or reverse-solve) → produce a copy.
3. Verify the copy with the tool (C ≈ A, B geometry matches the intent).
4. **User previews and confirms** (or reports the direction to refine).

## 5. Full computation loop (realized in practice)

Two ways to get from "target world orientation" to exact muscle values:

1. **In-editor inverse solve** (the tool's own extension): set the target bones
   to the desired world orientations, then `GetHumanPose` returns the exact
   muscle values to write back as the new baseline.
2. **Offline reverse solve from measurements** (verified): with 2+ measured
   states you have everything needed to compute deltas without an editor —
   calibrate each muscle's °/unit rate (it is nonlinear: a quadratic fit through
   3 measured points worked), then solve the geometry (elbow fold plane etc.).
   Full math + worked example: `references/arm-chain-reverse-solve.md`.

The loop that converged on the jump-loop pose (5 measured iterations):
measure → diagnose in numbers (elevation/azimuth, fold plane) → calibrate rate
→ reverse-solve deltas → apply atomically → re-measure → refit with the newest
point. Each round's motor skill: the newest measurement is the only one that
matters locally; the user's visual verdict still closes the loop.

Practical details of the loop:

- The tool writes `Temp/anim_bone_info.txt` and OVERWRITES it — snapshot every
  run (`anim_tools.py bones <file> --save <label>`). **Do not rely on `Temp/`
  for long-term storage**: Unity wipes it (it disappeared mid-session once);
  keep snapshots in the repo root/workspace or any folder outside `Assets/`.
- After each write, click the Unity Editor to trigger re-import. A
  "main object name ''" warning or empty clip → Reimport once; if it persists,
  the bytes are corrupted (lone-CR check), not the importer.
- When committing: even a commit with no LFS files trips the repo's pre-commit
  hook, which spawns `sh.exe` — under a restricted sandbox that fails with
  `sh.exe: couldn't create signal pipe, Win32 error 5`. Escalate the exact
  same commit command once (wider permissions) and it succeeds; do not work
  around it with a different command.
