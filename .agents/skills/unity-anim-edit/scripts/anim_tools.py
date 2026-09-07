# -*- coding: utf-8 -*-
"""anim_tools.py — CLI for the unity-anim-edit skill (pure stdlib).

Subcommands (see `python anim_tools.py <cmd> --help`):
  info       parse a humanoid .anim: sections, attributes, keyframe summary
  diff       byte-exact line diff of two .anim files (+ EOL corruption check)
  apply      apply constant muscle deltas (EOL-safe atomic write, verify)
  bones      analyze AnimComputeWindow output: segment directions, elevations,
             azimuths, elbow hinge frames (u/w/n), bend angles
  solve      reverse-solve elbow fold (theta/phi) + upper-arm inward swing for
             a target forearm world direction; prints suggested muscle deltas
  calibrate  per-muscle angle-rate from 2-3 measured states (linear/quadratic)

Run from PowerShell, e.g.:
  python anim_tools.py bones ..\\..\\..\\..\\..\\projectFJ\\Temp\\anim_bone_info.txt
  python anim_tools.py solve <bone_info> --upper-az-delta 18 --target-elev 25 --target-az 0
  python anim_tools.py apply <anim> --deltas "Left Arm Front-Back:-0.30,Left Forearm Stretch:-0.28"
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import animlib as A

SIDES = ("Left", "Right")
FMT = lambda v: repr(round(v, 7))


def cmd_info(args):
    raw, crlf, lines, entries, name = A.parse_anim(args.clip)
    print("clip: %s" % name)
    print("size: %d bytes, CRLF: %s, lines: %d" % (len(raw), crlf, len(lines)))
    sec_counts = {}
    for sec, attr, kfs in entries:
        sec_counts[sec] = sec_counts.get(sec, 0) + 1
    print("sections with curves:", sec_counts)
    mus = {}
    for sec, attr, kfs in entries:
        mus.setdefault(attr, []).extend((t, v) for t, v, _ in kfs)
    for attr in sorted(mus):
        vs = [v for _, v in mus[attr]]
        ts = sorted(t for t, _ in mus[attr])
        print("  %-30s n=%2d t=%s..%s  t0=%s min=%s max=%s" %
              (attr, len(vs), ts[0], ts[-1], vs[0], min(vs), max(vs)))
    for l in lines:
        m = re.match(r"^  m_StopTime: (\S+)", l)
        if m:
            print("m_StopTime:", m.group(1))
        m = re.match(r"^    m_LoopTime: (\S+)", l)
        if m:
            print("m_LoopTime:", m.group(1))


def cmd_diff(args):
    raw_a, crlf_a, la = A.read_anim(args.a)
    raw_b, crlf_b, lb = A.read_anim(args.b)
    n = max(len(la), len(lb))
    diffs = [(i, la[i] if i < len(la) else "<EOF>", lb[i] if i < len(lb) else "<EOF>")
             for i in range(n) if (i >= len(la) or i >= len(lb) or la[i] != lb[i])]
    print("lines: %d vs %d | CRLF: %s / %s" % (len(la), len(lb), crlf_a, crlf_b))
    print("exact differing lines: %d" % len(diffs))
    for i, x, y in diffs[:args.limit]:
        print("  L%4d  %-44s -> %s" % (i + 1, x.strip(), y.strip()))
    for label, raw in (("A", raw_a), ("B", raw_b)):
        t = raw.decode("utf-8")
        lone = len(re.findall(r"\r(?!\n)", t))
        print("  %s: lone CR=%d  CRLF=%d  LF=%d" %
              (label, lone, len(re.findall(r"\r\n", t)), t.count("\n")))


def apply_deltas(lines_in, entries, delta_map, new_name=None):
    """Return new lines after adding constant deltas to every keyframe of the
    named muscles, and a per-muscle report [(attr, old, new)]."""
    out = list(lines_in)
    report = {}
    for sec, attr, kfs in entries:
        if attr in delta_map:
            d = delta_map[attr]
            for t, v, li in kfs:
                nv = v + d
                out[li] = "        value: " + FMT(nv)
                report.setdefault(attr, []).append((v, nv))
    if new_name:
        for i, l in enumerate(out):
            m = re.match(r"^  m_Name:\s*(\S.*)$", l)
            if m:
                out[i] = "  m_Name: " + new_name
                break
    return out, report


def cmd_apply(args):
    raw, crlf, lines, entries, name = A.parse_anim(args.clip)
    delta_map = {}
    for part in args.deltas.split(","):
        k, v = part.split(":", 1)
        delta_map[k.strip()] = float(v)
    out, report = apply_deltas(lines, entries, delta_map, args.name)
    old = A.write_atomic(args.clip, out, crlf)
    print("applied to:", args.clip)
    if args.name:
        print("m_Name -> %s" % args.name)
    for attr, pairs in sorted(report.items()):
        olds = ", ".join("%.6f" % v for v, _ in pairs)
        news = ", ".join("%.6f" % v for _, v in pairs)
        print("  %-28s old=[%s]  new=[%s]  delta=%+.4f" % (attr, olds, news, delta_map[attr]))
    # verification
    t = open(args.clip, "rb").read().decode("utf-8")
    lone = len(re.findall(r"\r(?!\n)", t))
    bak = [l[:-1] if l.endswith("\r") else l for l in old.decode("utf-8").split("\n")]
    diffn = sum(1 for x, y in zip(bak, out) if x != y)
    touched = {sec for sec, attr, kfs in entries if attr in delta_map}
    print("verify: lone CR=%d (must be 0), changed lines=%d, sections touched=%s" %
          (lone, diffn, sorted(touched)))


def cmd_bones(args):
    muscles, bones, lines = A.parse_bone_info(args.info)
    req = [s + b for s in SIDES for b in ("UpperArm", "LowerArm", "Hand")]
    missing = [b for b in req if b not in bones]
    if missing:
        print("no usable bone positions in %s (missing: %s) - is this the tool's "
              "section B output?" % (args.info, ", ".join(missing)))
        return
    if args.save:
        import shutil
        base = os.path.splitext(os.path.basename(args.info))[0]
        dst = os.path.join(os.path.dirname(os.path.abspath(args.info)),
                           "%s_%s.txt" % (base, args.save))
        shutil.copyfile(args.info, dst)
        print("snapshot saved:", dst)
    for side in SIDES:
        u, f = A.upper_arm_forearm(bones, side)
        n, w = A.hinge_frame(u, f)
        ang = math_deg_acos(A.vdot(u, f))
        print("%s upper arm: elev=%+.1fdeg az=%+.1fdeg  dir=(%.3f,%.3f,%.3f)" %
              (side, A.elevation(u), A.azimuth(u), *u))
        print("%s forearm  : elev=%+.1fdeg az=%+.1fdeg  dir=(%.3f,%.3f,%.3f)" %
              (side, A.elevation(f), A.azimuth(f), *f))
        print("%s elbow bend=%.1fdeg | hinge n=(%.3f,%.3f,%.3f) fold w=(%.3f,%.3f,%.3f)" %
              (side, ang, *n, *w))
        print("%s muscle values: Stretch=%.4f Twist=%.4f FrontBack=%.4f DownUp=%.4f" %
              (side,
               muscles.get(side + " Forearm Stretch", float("nan")),
               muscles.get(side + " Arm Twist In-Out", float("nan")),
               muscles.get(side + " Arm Front-Back", float("nan")),
               muscles.get(side + " Arm Down-Up", float("nan"))))
        # legs for completeness
    for side in SIDES:
        ua = A.seg_dir(bones, side + "UpperLeg", side + "LowerLeg")
        lw = A.seg_dir(bones, side + "LowerLeg", side + "Foot")
        print("%s leg: thigh elev=%+.1f  shin elev=%+.1f" %
              (side, A.elevation(ua), A.elevation(lw)))


def math_deg_acos(c):
    import math
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


def cmd_solve(args):
    """Target: forearm world dir m (from --target-elev/--target-az), optionally
    after swinging the upper arm inward by --upper-az-delta deg. Prints per side
    theta (elbow bend), phi (plane twist) and suggested muscle deltas."""
    muscles, bones, lines = A.parse_bone_info(args.info)
    import math
    m = (0.0, math.sin(math.radians(args.target_elev)), math.cos(math.radians(args.target_elev)))
    az0 = args.target_az or 0.0
    if az0:
        # target azimuth: rotate m about Y (keep elevation)
        m = A.rot_world_y(m, az0)
    results = {}
    for side in SIDES:
        u, f = A.upper_arm_forearm(bones, side)
        n, w = A.hinge_frame(u, f)
        el0, az0v = A.elevation(u), A.azimuth(u)
        if args.upper_az_delta:
            # inward swing: left +Y, right -Y (both move toward the midline)
            rot = args.upper_az_delta * (1 if side == "Left" else -1)
            u = A.vnorm(A.rot_world_y(u, rot))
            w = A.vnorm(A.rot_world_y(w, rot))
            n = A.vnorm(A.rot_world_y(n, rot))
        theta, phi, achieved = A.solve_forearm(u, w, m)
        el1, az1 = A.elevation(u), A.azimuth(u)
        c = A.vdot(m, u)
        wp = A.vnorm(A.vsub(m, tuple(c * x for x in u)))
        ach = tuple(math.cos(math.radians(theta)) * u[i] + math.sin(math.radians(theta)) * wp[i]
                    for i in range(3))
        # muscle mapping (rates per side override via CLI)
        cur_stretch = muscles.get(side + " Forearm Stretch", A.DEFAULT_STRAIGHT_STRETCH)
        stretch_tgt = A.stretch_from_bend(theta,
                                          calib=args.stretch_calib, straight=args.straight)
        fb_delta = -abs(args.upper_az_delta or 0) / args.rate_frontback * args.fb_sign
        tw_delta = abs(phi) / args.rate_twist * args.twist_sign
        results[side] = dict(el0=el0, az0=az0v, el1=el1, az1=az1, theta=theta, phi=phi,
                             achieved=ach, fb=fb_delta, tw=tw_delta, st=stretch_tgt - cur_stretch,
                             calc=(cur_stretch, stretch_tgt))
    for side in SIDES:
        r = results[side]
        fs, ft = r["calc"]
        print("== %s" % side)
        print("  upper arm: az %+.1f -> %+.1f (delta %+.1f deg), elev %+.1f -> %+.1f" %
              (r["az0"], r["az1"], args.upper_az_delta or 0, r["el0"], r["el1"]))
        print("  forearm: theta=%.1f deg  phi=%.1f deg  achieved dir=(%.3f,%.3f,%.3f)"
              % (r["theta"], r["phi"], *r["achieved"]))
        print("  deltas: Front-Back %+.3f | Twist %+.3f | Stretch %+.3f (%.4f -> %.4f)"
              % (r["fb"], r["tw"], r["st"], fs, ft))
    print("rates: front-back=%.1f  twist=%.1f deg/unit (estimates; twist sign per rig) |"
          " stretch calib=%s" % (args.rate_frontback, args.rate_twist, args.stretch_calib))


def cmd_calibrate(args):
    """--state 'net:value,file' pairs (2 or 3); fits upper-arm elevation vs net.""" 
    import math
    pts = []
    for s in args.state:
        net_s, f = s.split(",", 1)
        muscles, bones, _ = A.parse_bone_info(f)
        side = args.side
        u, _ = A.upper_arm_forearm(bones, side)
        pts.append((float(net_s), A.elevation(u)))
    if len(pts) == 2:
        rate = A.lin_rate(*(tuple(p) for p in pts))
        print("points: %s" % [(round(x, 3), round(y, 2)) for x, y in pts])
        print("linear rate = %.2f deg/unit" % rate)
        if args.query is not None:
            print("elevation at net=%.3f : %+.1f deg" % (args.query, pts[0][1] + (args.query - pts[0][0]) * rate))
    elif len(pts) == 3:
        a, b, c = A.fit_quadratic([tuple(p) for p in pts])
        print("points: %s" % [(round(x, 3), round(y, 2)) for x, y in pts])
        print("quadratic: elev = %.3f*net^2 %+.3f*net %+.3f" % (a, b, c))
        if args.query is not None:
            print("elevation at net=%.3f : %+.1f deg" % (args.query, a * args.query**2 + b * args.query + c))
        x1, y1 = pts[1]
        x2, y2 = pts[2]
        print("local slope (last pair): %.2f deg/unit" % ((y2 - y1) / (x2 - x1)))
    else:
        print("need 2 or 3 --state pairs")


def main():
    p = argparse.ArgumentParser(description="unity-anim-edit tooling")
    sub = p.add_subparsers(dest="cmd", required=True)

    q = sub.add_parser("info", help="summarize a humanoid .anim")
    q.add_argument("clip")
    q.set_defaults(func=cmd_info)

    q = sub.add_parser("diff", help="byte-exact diff of two .anim files")
    q.add_argument("a")
    q.add_argument("b")
    q.add_argument("--limit", type=int, default=20)
    q.set_defaults(func=cmd_diff)

    q = sub.add_parser("apply", help="constant delta shift, EOL-safe atomic write")
    q.add_argument("clip")
    q.add_argument("--deltas", required=True,
                   help='"Left Arm Front-Back:-0.30,Left Forearm Stretch:-0.28,..."')
    q.add_argument("--name", help="new m_Name")
    q.set_defaults(func=cmd_apply)

    q = sub.add_parser("bones", help="analyze AnimComputeWindow bone info")
    q.add_argument("info")
    q.add_argument("--save",
                   help='snapshot the info file as <name>_<label>.txt (keep each '
                        'measured state - calibrate needs history)')
    q.set_defaults(func=cmd_bones)

    q = sub.add_parser("solve", help="reverse-solve forearm target + inward swing")
    q.add_argument("info")
    q.add_argument("--upper-az-delta", type=float, default=0,
                   help="inward azimuth swing, deg (both sides)")
    q.add_argument("--target-elev", type=float, default=25, help="forearm elevation target")
    q.add_argument("--target-az", type=float, default=0, help="forearm azimuth target")
    q.add_argument("--rate-frontback", type=float, default=60)
    q.add_argument("--rate-twist", type=float, default=60)
    q.add_argument("--fb-sign", type=int, default=1, help="1 = negative=forward (this rig)")
    q.add_argument("--twist-sign", type=int, default=1, help="1 = positive=folds up (per climb exp)")
    q.add_argument("--stretch-calib", default=None, help='"stretch:bend,stretch:bend" measured pts')
    q.add_argument("--straight", type=float, default=A.DEFAULT_STRAIGHT_STRETCH)
    q.set_defaults(func=cmd_solve)

    q = sub.add_parser("calibrate", help="fit upper-arm elevation vs muscle net")
    q.add_argument("--state", action="append", required=True,
                   help='"netValue,boneInfoFile" (repeat 2-3x)')
    q.add_argument("--side", default="Left", choices=["Left", "Right"])
    q.add_argument("--query", type=float, default=None)
    q.set_defaults(func=cmd_calibrate)

    args = p.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
