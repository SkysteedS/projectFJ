# Arm-chain reverse solve: calibrated rates + elbow fold-plane math

Reference for the `unity-anim-edit` skill. This is the realized "measure →
calibrate → reverse-solve → apply → re-measure" path for arm-chain pose targets
(e.g. "upper arms diagonal up, forearms parallel pointing forward-up"). The math
below was verified in practice on the jump-loop falling pose (this rig); the
numbers in the worked example are the real measurements from that session.

It complements the "estimate + verify" loop: use it when the user gives a
**precise geometric target** (a direction, a "parallel to X" constraint), not
just "higher/lower". With measurements, multiple muscles can be solved in one
pass; without them, keep to one dependency layer per round.

## 0. Geometry primitives from section B (world positions)

Bone world positions are printed by the tool. Direction of a bone segment:

```
u = normalize(elbow.pos − shoulderJoint.pos)   # upper arm
f = normalize(hand.pos − elbow.pos)            # forearm
elevation = asin(dy)          # deg, + = up
azimuth   = atan2(dx, dz)     # deg, 0 = world forward (+Z), ± = lateral
```

"Hands below shoulders" ⇔ elevation < 0 (dy < 0). This mirrors the user's visual
vocabulary one-to-one and gives measurable targets.

## 0. Scripted version

Every step below is implemented in `scripts/anim_tools.py` (pure stdlib):

```
anim_tools.py bones  <bone_info.txt> --save <label>   # u/f/n/w, elevations, azimuths, bend
                                                # --save snapshots each measured state
anim_tools.py solve  <bone_info.txt> --upper-az-delta 18 --target-elev 25 --target-az 0
    # per side: theta / phi / achieved direction / suggested FB+Twist+Stretch deltas
anim_tools.py calibrate --state "-0.25,a.txt" --state "0.30,b.txt" --state "0.98,c.txt" --query 0.63
anim_tools.py apply  <anim> --deltas "Left Arm Twist In-Out:0.48,Left Forearm Stretch:-0.28"
anim_tools.py diff   <anim> <backup>     # exact line diff + lone-CR check
```

Keep every measured state (`--save`) — `calibrate` is only as good as the
history; archive outside `Temp/` (Unity wipes it).

Rates/signs are CLI flags (`--rate-frontback`, `--rate-twist`, `--stretch-calib`,
`--fb-sign`, `--twist-sign`) — see `scripts/README.md`.

## 1. Per-muscle response rate = degrees per muscle unit (2+ measurements)

Muscle values are normalized units; the °/unit rate is **rig-specific and
nonlinear**. Get it from successive measurements while iterating. Real example
(Left Arm/Shoulder Down-Up NET on the jump rig):

| net (Shoulder+Arm Down-Up) | measured elevation (L) | step slope |
|---|---|---|
| −0.25 | −24.7° | — |
| +0.30 | −0.5° | 44.0°/unit |
| +0.98 | +38.6° | 57.5°/unit |

The slope **grew** with elevation — linear extrapolation from the first two
points under-predicted the third (predicted +29°, actual +38.6°). Fit a quadratic
through the points instead (for this data: `elev = 10.98·net² + 43.45·net − 14.52`)
and, once the change is applied, **re-measure and refit** — the newest point is
the only one that matters locally.

Same idea for every axis you use: `Forearm Stretch` was calibrated from two
measured bends (Stretch 0.142 ↔ 71.7°, 0.892 ↔ 11.6° → ≈80.3°/unit, linear).
Axes never changed across measurements (e.g. `Arm Front-Back`, `Arm Twist`) have
**no calibration** — treat their rate as an estimate (≈50–90°/unit), flag it in
the report, and let the next measurement pin it down.

## 2. Elbow fold plane (the reason "bend the elbow more" is ambiguous)

The forearm is the upper arm rotated about a **hinge** (the elbow). The hinge
plane is fixed relative to the upper arm; bending the elbow sweeps the forearm
WITHIN that plane. From one measured state compute the frame:

```
n = normalize(u × f)     # hinge axis (plane normal)
w = normalize(n × u)     # fold direction: where the forearm goes as it bends away from straight
```

Then any elbow angle θ gives `forearm(θ) = cosθ·u + sinθ·w` (θ = 0 → straight,
along the upper arm). Consequences:

- The maximum elevation a bent forearm can reach is the plane's own ceiling —
  no more than elevation(u) itself (the straight direction). If the target is
  higher than that, **the plane must be re-pointed, not just bent** — that is
  what `Arm Twist In-Out` does (rotates the plane about u by the twist value).
- Cost: the fold may currently point "forward-slightly-down" (w has negative y),
  which is why a bent elbow with an uplifted upper arm still gives a horizontal
  forearm. Same twist, more bend → only forward/slightly-down sweep. Symptom
  "forearm horizontal while upper arm is diagonal up" ⇒ twist change needed.

## 3. Reverse solve for a target world direction m (per side)

Want forearm to point at world direction m (same m for both sides = "parallel"):

```
theta = acos(m · u)                       # needed elbow bend
w'    = normalize(m − (m·u)·u)            # needed fold direction (in-plane part)
phi   = atan2(u · (w × w'), w · w')       # required plane rotation about u (deg)
```

Then:
- `Forearm Stretch` := calibrated Stretch(θ) (interpolate/extrapolate the rate).
- `Arm Twist In-Out` delta := phi / twist-rate (uncalibrated → estimate; the
  SIGN is the main risk — in the worked example L needed +29° and R −23.8° world,
  yet both muscle deltas were POSITIVE, so left/right twist axes are mirrored in
  muscle space. If the fold lands down/outward, flip the sign, keep magnitude).
- Quick sanity checks: `m·n ≈ 0` means the target lies in the CURRENT plane —
  no twist change needed at all; `|m − (m·u)u| ≤ 1` always true for unit vectors.

If the upper arm azimuth/elevation is ALSO being changed (e.g. swing inward with
`Arm Front-Back`), rotate the whole frame (u, w, n) rigidly by that change first
(about world Y for azimuth), THEN solve θ/φ. The composition is an approximation:
Unity composes muscles about fixed local axes in a fixed order, so residuals of a
few degrees are expected — always re-measure and nudge.

## 4. Worked example (jump loop falling pose, real numbers)

User target: upper arms diagonal up (not below shoulder), then iterate to:
forearms **parallel, pointing up-forward** (+25° elevation, azimuth 0), upper
arms swung inward ("toward the midline").

1. Measure (tool, time=0): upper arm L elev +13.6°, az −51.8°; forearm L elev
   +12.5° (bend 11.6° — nearly straight, twist −0.07). R: +18.4°, az +63.6°;
   forearm +16.7°.
2. Hinge frame from (u, f): L w ≈ (0.605, −0.069, 0.795) → fold sweeps
   forward/slightly-down; ceiling = +13.6°. Target m = (0, 0.423, 0.906) is NOT
   in the plane (m·n ≈ 0.33) → twist change required. Confirms diagnosis.
3. Swing upper arms inward 18° (az L −51.8° → −33.8°) → `Arm Front-Back` delta
   −0.30 (rate estimated ≈60°/unit; flagged).
4. Solve at the swung orientation: θ = 33.7° (L) / 42.7° (R); φ = +29.0° (L) /
   −23.8° (R) → Stretch deltas −0.28 (L) / −0.39 (R) via the 80.3°/unit
   calibration; Twist deltas +0.48 (L) / +0.40 (R) at an estimated 60°/unit.
5. Verification identity used in the solve:
   `cosθ·u + sinθ·w′ = (0.000, 0.423, 0.906)` exactly.
6. User preview: approved. Subsequent re-measurements refine the estimated
   rates into real ones.

## 5. Pitfalls recap

- Rates are not 100°/unit by default; measure. Nonlinear → quadratic fit with
  ≥3 points; prefer the local slope near the current pose.
- Twist sign is the top risk on a new rig — flag it, expect one flip.
- Per-side targets are fine (rigs are never perfectly mirror-symmetric; same
  muscle values still gave L +38.6° vs R +43.3° in this example).
- `Arm Down-Up` = elevation in the coronal plane; `Arm Front-Back` = azimuth
  (swing inward/forward). "Too wide" ⇒ Front-Back, not Down-Up.
