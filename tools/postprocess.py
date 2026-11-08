#!/usr/bin/env python3
"""Post-process GEE AutoShot screenshots.

- shots/small/NAME.jpg   (max 1280 px wide)
- shots/contact_sheet.jpg (labelled grid)
- shots/diff_NAME.jpg    (amplified abs difference vs baseline/NAME.png) + mean-abs-diff score
- results.json assertion summary, recent game-log errors
- --promote copies the current shots to the baseline folder

Needs Pillow. Without Pillow only a fallback runs (exact byte compare with the
baseline, no JPGs/contact sheet/diff images) and a notice is printed.
"""
import argparse, hashlib, json, os, re, shutil, sys, time
from datetime import datetime

try:
    from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageStat
    HAVE_PIL = True
except ImportError:
    HAVE_PIL = False


def shot_names(shots):
    out = []
    for f in sorted(os.listdir(shots)):
        n, e = os.path.splitext(f)
        if e.lower() == ".png" and not n.startswith("diff_") and n != "contact_sheet":
            out.append(n)
    return out


def small(img, width=1280):
    if img.width > width:
        img = img.resize((width, round(img.height * width / img.width)), Image.LANCZOS)
    return img


def load_rgb(path):
    return Image.open(path).convert("RGB")


def diff_score(a, b):
    """Mean abs diff (0-255) on a 640 px version; returns (score, amplified diff image)."""
    w = 640
    a = small(a, w)
    b = b.resize(a.size, Image.BILINEAR) if b.size != a.size else b
    b = small(b, w) if b.width != a.width else b
    d = ImageChops.difference(a, b)
    score = sum(ImageStat.Stat(d).mean) / 3.0
    amp = d.point(lambda v: min(255, v * 4))
    return score, amp


def contact_sheet(shots, names, out):
    tw = 480
    imgs = []
    for n in names:
        im = small(load_rgb(os.path.join(shots, n + ".png")), tw)
        imgs.append((n, im))
    if not imgs:
        return
    th = max(i.height for _, i in imgs)
    cols = min(3, len(imgs))
    rows = (len(imgs) + cols - 1) // cols
    lab = 22
    sheet = Image.new("RGB", (cols * tw, rows * (th + lab)), (20, 20, 24))
    dr = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("arial.ttf", 15)
    except Exception:
        font = ImageFont.load_default()
    for k, (n, im) in enumerate(imgs):
        x, y = (k % cols) * tw, (k // cols) * (th + lab)
        sheet.paste(im, (x, y + lab))
        dr.text((x + 6, y + 3), n, fill=(235, 235, 235), font=font)
    sheet.save(out, quality=85)


SEQ_RE = re.compile(r"^(.+)_(\d{3})$")


def split_sequences(names):
    """Names NAME_001..NAME_NNN (3+ frames from `record NAME N`) -> ({prefix: [frame names]}, [single names])."""
    groups = {}
    for n in names:
        m = SEQ_RE.match(n)
        if m:
            groups.setdefault(m.group(1), []).append(n)
    seqs = {k: v for k, v in groups.items() if len(v) >= 3}
    in_seq = {n for v in seqs.values() for n in v}
    return seqs, [n for n in names if n not in in_seq]


def process_sequence(shots, prefix, frames):
    """Filmstrip (12 evenly sampled frames), GIF, mp4 if ffmpeg exists, and a per-step motion score.
    Full-size frame PNGs are moved to shots/frames/ so shots/ stays readable."""
    ims = [small(load_rgb(os.path.join(shots, n + ".png")), 640) for n in frames]
    pick = sorted({round(i * (len(ims) - 1) / 11) for i in range(12)}) if len(ims) > 12 else list(range(len(ims)))
    tw = 480
    cells = [(frames[i], small(ims[i], tw)) for i in pick]
    th = max(c.height for _, c in cells)
    cols = min(4, len(cells))
    rows = (len(cells) + cols - 1) // cols
    lab = 20
    sheet = Image.new("RGB", (cols * tw, rows * (th + lab)), (20, 20, 24))
    dr = ImageDraw.Draw(sheet)
    for k, (n, im) in enumerate(cells):
        x, y = (k % cols) * tw, (k // cols) * (th + lab)
        sheet.paste(im, (x, y + lab))
        dr.text((x + 6, y + 3), n, fill=(235, 235, 235))
    film = os.path.join(shots, "film_%s.jpg" % prefix)
    sheet.save(film, quality=85)
    gif = os.path.join(shots, "%s.gif" % prefix)
    step = max(1, len(ims) // 60)
    ims[::step][0].save(gif, save_all=True, append_images=ims[::step][1:], duration=400 * step, loop=0)
    # motion between consecutive frames: ~0 for many steps in a row means the picture froze
    motion = [diff_score(ims[i], ims[i + 1])[0] for i in range(len(ims) - 1)]
    still = sum(1 for m in motion if m < 0.05)
    print("  sequence %-18s %d frames, motion avg %.2f max %.2f, %d near-identical steps%s" % (
        prefix, len(frames), sum(motion) / max(1, len(motion)), max(motion, default=0), still,
        "  <-- FROZEN?" if motion and still >= len(motion) * 0.8 else ""))
    print("    filmstrip: %s\n    gif: %s" % (film, gif))
    ff = shutil.which("ffmpeg")
    if ff:
        import subprocess, tempfile
        tmp = tempfile.mkdtemp()
        for i, n in enumerate(frames):
            shutil.copy2(os.path.join(shots, n + ".jpg") if os.path.exists(os.path.join(shots, n + ".jpg")) else os.path.join(shots, n + ".png"),
                         os.path.join(tmp, "f%04d.jpg" % i))
        mp4 = os.path.join(shots, "%s.mp4" % prefix)
        subprocess.run([ff, "-y", "-loglevel", "error", "-framerate", "2", "-i", os.path.join(tmp, "f%04d.jpg"),
                        "-vf", "scale=1280:-2", "-pix_fmt", "yuv420p", mp4], check=False)
        shutil.rmtree(tmp, ignore_errors=True)
        print("    mp4: %s" % mp4)
    else:
        print("    (no ffmpeg on PATH: no mp4, GIF and filmstrip only)")
    os.makedirs(os.path.join(shots, "frames"), exist_ok=True)
    for n in frames:
        for ext in (".png", ".jpg"):
            p = os.path.join(shots, n + ext)
            if os.path.exists(p):
                shutil.move(p, os.path.join(shots, "frames", n + ext))


def summarize_results(shots):
    p = os.path.join(shots, "results.json")
    if not os.path.exists(p):
        print("assertions: no results.json")
        return
    try:
        data = json.load(open(p, encoding="utf-8-sig"))
    except Exception as e:
        print("assertions: results.json unreadable (%s)" % e)
        return
    items = data
    if isinstance(data, dict):
        errs = data.get("errors")
        if isinstance(errs, list) and errs:
            print("exceptions in game log: %d" % len(errs))
            for e in errs[:5]:
                print("  " + str(e)[:200])
        for k in ("asserts", "results", "assertions", "checks"):
            if isinstance(data.get(k), list):
                items = data[k]
                break
    if not isinstance(items, list):
        print("assertions: " + json.dumps(data)[:400])
        return
    fails = []
    for it in items:
        if isinstance(it, dict):
            ok = None
            for k in ("pass", "passed", "ok", "success"):
                if k in it:
                    ok = bool(it[k])
            if ok is None and "status" in it:
                ok = str(it["status"]).lower() in ("pass", "passed", "ok")
            if ok is False:
                fails.append(it)
    print("assertions: %d total, %s" % (len(items), "ALL PASS" if not fails else "%d FAIL" % len(fails)))
    for f in fails[:10]:
        print("  FAIL " + json.dumps(f)[:200])
    if isinstance(data, dict):
        extra = {k: v for k, v in data.items() if not isinstance(v, (list, dict))}
        if extra:
            print("  " + json.dumps(extra)[:200])


def log_errors(logdir, since):
    if not logdir or not os.path.isdir(logdir):
        return
    logs = [os.path.join(logdir, f) for f in os.listdir(logdir) if f.lower().endswith(".log")]
    if not logs:
        return
    newest = max(logs, key=os.path.getmtime)
    if since and os.path.getmtime(newest) < since:
        print("log: newest log (%s) is older than this run, skipped" % os.path.basename(newest))
        return
    pat = re.compile(r"exception|error|\bfail", re.I)
    hits = []
    with open(newest, encoding="utf-8", errors="replace") as f:
        for line in f:
            if " INFO " in line or " DEBUG " in line and "Exception" not in line:
                continue
            if pat.search(line) and ("GEE" in line or "Exception" in line):
                hits.append(line.strip())
    print("log %s: %d error-like lines" % (os.path.basename(newest), len(hits)))
    seen = set()
    for h in hits:
        k = h[:90]
        if k in seen:
            continue
        seen.add(k)
        print("  " + h[:200])
        if len(seen) >= 8:
            break


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser()
    ap.add_argument("--shots", default=os.path.join(os.path.dirname(here), "shots"))
    ap.add_argument("--baseline", default=os.path.join(here, "baseline"))
    ap.add_argument("--threshold", type=float, default=6.0)
    ap.add_argument("--promote", action="store_true")
    ap.add_argument("--logdir")
    ap.add_argument("--since", help="ISO time of run start")
    a = ap.parse_args()

    since = None
    if a.since:
        try:
            since = datetime.fromisoformat(a.since.rstrip("Z")[:26]).timestamp()
        except Exception:
            pass

    names = shot_names(a.shots) if os.path.isdir(a.shots) else []
    seqs, names = split_sequences(names) if HAVE_PIL else ({}, names)
    print("shots: %d (%s)" % (len(names), ", ".join(names)))
    for prefix, frames in seqs.items():
        try:
            process_sequence(a.shots, prefix, frames)
        except Exception as e:
            print("  sequence %s failed: %s" % (prefix, e))
    print("DONE marker: %s" % ("yes" if os.path.exists(os.path.join(a.shots, "DONE")) else "NO"))

    if not HAVE_PIL:
        print("NOTE: Pillow not installed (pip install pillow): no JPGs, contact sheet or diff images; exact-match compare only")
        for n in names:
            b = os.path.join(a.baseline, n + ".png")
            if os.path.exists(b):
                same = hashlib.md5(open(b, "rb").read()).digest() == hashlib.md5(open(os.path.join(a.shots, n + ".png"), "rb").read()).digest()
                print("  %-24s %s" % (n, "identical" if same else "differs"))
            else:
                print("  %-24s no baseline" % n)
    else:
        os.makedirs(os.path.join(a.shots, "small"), exist_ok=True)
        flagged = []
        for n in names:
            try:
                im = load_rgb(os.path.join(a.shots, n + ".png"))
            except Exception as e:
                print("  %-24s unreadable: %s" % (n, e))
                continue
            small(im).save(os.path.join(a.shots, "small", n + ".jpg"), quality=88)
            b = os.path.join(a.baseline, n + ".png")
            if os.path.exists(b):
                score, amp = diff_score(im, load_rgb(b))
                amp.save(os.path.join(a.shots, "diff_" + n + ".jpg"), quality=85)
                flag = score > a.threshold
                if flag:
                    flagged.append(n)
                print("  %-24s diff %.2f %s" % (n, score, "<-- CHANGED" if flag else "ok"))
            else:
                print("  %-24s no baseline" % n)
        if names:
            contact_sheet(a.shots, names, os.path.join(a.shots, "contact_sheet.jpg"))
            print("contact sheet: %s" % os.path.join(a.shots, "contact_sheet.jpg"))
        if flagged:
            print("FLAGGED (> %.1f): %s" % (a.threshold, ", ".join(flagged)))

    summarize_results(a.shots)
    log_errors(a.logdir, since)

    if a.promote:
        os.makedirs(a.baseline, exist_ok=True)
        for n in names:
            shutil.copy2(os.path.join(a.shots, n + ".png"), os.path.join(a.baseline, n + ".png"))
        print("baseline updated with %d shots -> %s" % (len(names), a.baseline))


if __name__ == "__main__":
    main()
