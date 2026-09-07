# -*- coding: utf-8 -*-
"""animlib — pure-stdlib helpers for humanoid .anim editing and arm-chain
geometry. No third-party dependencies (works on plain Python 3.7+).

Covers the full loop used for the jump-loop pose edits:
  parse .anim  ->  diagnose from tool bone info  ->  calibrate muscle rates
  ->  reverse-solve elbow fold (theta/phi)  ->  EOL-safe atomic apply  ->  verify.
"""
import math
import os
import re

# --------------------------------------------------------------------------
# 3D vector math (tuples, pure python)
# --------------------------------------------------------------------------

def vsub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])

def vlen(v):
    return math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])

def vnorm(v):
    L = vlen(v)
    return (v[0] / L, v[1] / L, v[2] / L)

def vcross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])

def vdot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]

def vrot_axis(v, k, deg):
    """Rodrigues: rotate v about unit axis k by deg (right-hand rule)."""
    k = vnorm(k)
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    kxv = vcross(k, v)
    return tuple(c * v[i] + s * kxv[i] + (1 - c) * k[i] * vdot(k, v) for i in range(3))

def elevation(v):
    """+ = up. v should be a unit-ish direction."""
    return math.degrees(math.asin(max(-1.0, min(1.0, v[1] / vlen(v)))))

def azimuth(v):
    """deg from world forward (+Z); + = toward +X."""
    return math.degrees(math.atan2(v[0], v[2]))

def rot_world_y(v, deg):
    """Rotate about world +Y by deg (used for azimuth swings)."""
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return (c * v[0] + s * v[2], v[1], -s * v[0] + c * v[2])

# --------------------------------------------------------------------------
# .anim parsing / EOL-safe writing
# --------------------------------------------------------------------------

def read_anim(path):
    """Return (raw_bytes, crlf_flag, lines_without_trailing_CR)."""
    raw = open(path, "rb").read()
    crlf = b"\r\n" in raw
    text = raw.decode("utf-8")
    lines = [l[:-1] if l.endswith("\r") else l for l in text.split("\n")]
    return raw, crlf, lines


def write_atomic(path, lines, crlf, backup=True):
    """Strip-free lines are joined with the detected EOL (never re-add \r to
    the elements - that would produce \r\r\n and break Unity's YAML parser).
    Writes to a temp file, fsyncs, then atomically replaces. Returns old bytes
    for verification."""
    old = open(path, "rb").read()
    if backup and os.path.exists(path):
        with open(path + ".bak", "wb") as f:
            f.write(old)
    eol = "\r\n" if crlf else "\n"
    data = eol.join(lines).encode("utf-8")
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)
    return old


def parse_anim(path):
    """Return list of (section, attr, [(time, value, line_idx)]).
    Sections = top-level '  m_FloatCurves:' / '  m_EditorCurves:' blocks.
    Empty sections ('...: []') are skipped. Also returns meta info."""
    raw, crlf, lines = read_anim(path)
    top = []
    for i, l in enumerate(lines):
        m = re.match(r"^  m_FloatCurves:\s*(\[\]|$)", l)
        if m and m.group(1) == "":
            top.append(("m_FloatCurves", i))
        m = re.match(r"^  m_EditorCurves:\s*(\[\]|$)", l)
        if m and m.group(1) == "":
            top.append(("m_EditorCurves", i))
    entries = []
    for si, (sec, start) in enumerate(top):
        end = len(lines)
        for i in range(start + 1, len(lines)):
            if re.match(r"^  m_[A-Za-z]+:", lines[i]):
                end = i
                break
        attr, kfs = None, []
        pend_t = None
        for i in range(start, end):
            l = lines[i]
            if l == "  - serializedVersion: 2":
                if attr is not None and kfs:
                    entries.append((sec, attr, kfs))
                attr, kfs = None, []
                continue
            m = re.match(r"^        time: (\S+)", l)
            if m:
                pend_t = float(m.group(1))
                continue
            m = re.match(r"^        value: (\S+)", l)
            if m:
                kfs.append((pend_t if pend_t is not None else 0.0, float(m.group(1)), i))
                pend_t = None
                continue
            m = re.match(r"^    attribute: (.+)$", l)
            if m:
                attr = m.group(1).strip()
                continue
        if attr is not None and kfs:
            entries.append((sec, attr, kfs))
    name = None
    for l in lines:
        m = re.match(r"^  m_Name:\s*(\S.*)$", l)
        if m:
            name = m.group(1).strip()
            break
    return raw, crlf, lines, entries, name


# --------------------------------------------------------------------------
# Tool bone-info output (Temp/anim_bone_info.txt) parsing
# --------------------------------------------------------------------------

def parse_bone_info(path):
    """Return (muscles dict, bones dict name->worldPos vec, raw lines)."""
    lines = open(path, encoding="utf-8").read().split("\n")
    muscles, bones = {}, {}
    for i, l in enumerate(lines):
        m = re.match(r"^muscles\[\s*\d+\]\s+(.+?)\s*=\s*([-\d.eE+]+)", l)
        if m:
            muscles[m.group(1).strip()] = float(m.group(2))
            continue
        m = re.match(r"^(\w+)\s+parent=", l)
        if m:
            name = m.group(1)
            for j in (i, i + 1):
                w = re.search(r"worldPos=\(([^)]*)\)", lines[j])
                if w:
                    bones[name] = tuple(float(x) for x in w.group(1).split(","))
                    break
    return muscles, bones, lines


def seg_dir(bones, bone_a, bone_b):
    return vnorm(vsub(bones[bone_b], bones[bone_a]))


def upper_arm_forearm(bones, side):
    ua = side + "UpperArm"
    la = side + "LowerArm"
    h = side + "Hand"
    u = seg_dir(bones, ua, la)
    f = seg_dir(bones, la, h)
    return u, f

# --------------------------------------------------------------------------
# Elbow fold plane + reverse solve
# --------------------------------------------------------------------------

def hinge_frame(u, f):
    """n = plane normal (hinge axis), w = fold direction (forearm dir at 90deg)."""
    n = vnorm(vcross(u, f))
    w = vnorm(vcross(n, u))
    return n, w


def solve_forearm(u, w, m):
    """Target world direction m for the forearm. Returns (theta_deg, phi_deg,
    achieved_dir) where theta = required elbow bend and phi = required plane
    rotation about u (right-hand)."""
    m = vnorm(m)
    cu = vdot(m, u)
    theta = math.degrees(math.acos(max(-1.0, min(1.0, cu))))
    wp = vsub(m, tuple(cu * x for x in u))
    s = vlen(wp)
    wp = vnorm(wp)
    phi = math.degrees(math.atan2(vdot(u, vcross(w, wp)), vdot(w, wp)))
    achieved = tuple(cu * u[i] + s * wp[i] for i in range(3))
    return theta, phi, achieved

# --------------------------------------------------------------------------
# Calibration: muscle rate / degree mappings (per-rig, overridable)
# --------------------------------------------------------------------------

def fit_quadratic(points):
    """points = [(x, y), x3] -> (a, b, c) for y = a*x^2 + b*x + c (3 pts)."""
    (x1, y1), (x2, y2), (x3, y3) = points
    den = (x1 - x2) * (x1 - x3) * (x2 - x3)
    a = (x3 * (y2 - y1) + x2 * (y1 - y3) + x1 * (y3 - y2)) / den
    b = (x3 * x3 * (y1 - y2) + x2 * x2 * (y3 - y1) + x1 * x1 * (y2 - y3)) / den
    c = (x2 * x3 * (x2 - x3) * y1 + x1 * x3 * (x3 - x1) * y2 + x1 * x2 * (x1 - x2) * y3) / den
    return a, b, c


def lin_rate(p1, p2):
    d1, r1 = p1
    d2, r2 = p2
    return (r2 - r1) / (d2 - d1)


DEFAULT_STRETCH_CALIB = [(0.892, 11.6), (0.142, 71.7)]  # this rig: (Stretch, bend deg)
DEFAULT_STRAIGHT_STRETCH = 0.98


def stretch_from_bend(bend_deg, calib=None, straight=None):
    """Stretch value that yields the given elbow bend angle (deg).
    calib = [(stretch, bend_deg), ...] measured on this rig; 1 pt -> rate
    guessed from DEFAULT pair's slope; 2+ -> linear fit."""
    calib = calib or DEFAULT_STRETCH_CALIB
    straight = straight or DEFAULT_STRAIGHT_STRETCH
    if len(calib) >= 2:
        (s1, b1), (s2, b2) = calib[0], calib[1]
        rate = (b2 - b1) / (s2 - s1)  # deg per unit (negative)
        return s2 + (bend_deg - b2) / rate
    (s1, b1) = calib[0]
    fallback = DEFAULT_STRETCH_CALIB[1]
    rate = (fallback[1] - b1) / (fallback[0] - s1)
    return s1 + (bend_deg - b1) / rate
