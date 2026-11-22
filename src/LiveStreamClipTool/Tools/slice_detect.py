# -*- coding: utf-8 -*-
# Copyright (c) 2026 cnoctave.top
# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of LiveStreamClipTool; see LICENSE for the full license text.

"""批量切片工具 · 自动切片检测（移植自 replay-radio/tools/auto_segments.py）

对**本地录播文件**（FLV/MP4 等）做逐秒音频特征分析，找出「有内容」的片段：
    每秒整体响度高于本块的自适应阈值（排除静音与低语）。阈值按 300 秒分块各求一次，
    避免不同主播/不同时段的麦克风增益差把小声那一段整体判成静音。
    25-100Hz 伴奏能量只用于边界吸附，不再作为「有没有内容」的门槛 —— 原版要求
    有伴奏才算内容，纯说话的录播会被整段丢掉。
    检测后经过边界外扩 / 伴奏进出吸附 / 长段细分。

只依赖 Python 标准库与 ffmpeg，不下载任何网络内容。

用法：
    python slice_detect.py --input rec.flv --output segs.json --ffmpeg ffmpeg.exe

输出 JSON：
    {"input": "...", "duration": 3600.0, "count": 12,
     "segments": [{"start": 120, "end": 480}, ...]}

进度（stdout，每行一条，供宿主程序解析）：
    PROGRESS\t<0-100>\t<阶段说明>
    INFO\t<日志>
"""
import argparse
import json
import math
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0


def _hide_startupinfo():
    if os.name != "nt":
        return None
    si = subprocess.STARTUPINFO()
    si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    si.wShowWindow = 0
    return si


HIDE_SI = _hide_startupinfo()

CHUNK = 600            # 每块音频秒数
THR_BLOCK = 300        # 自适应阈值的分块秒数
DEFAULT_WORKERS = 2
SILENCE_DB = -55.0
SILENCE_RUN = 3
PAD_RADIUS = 120
BASS_JUMP = 4.0
BASS_WIN = 8
LONG_SEG = 480
PIECE_MIN = 180
TALK_MIN = 8
TALK_RATIO = 0.5
TALK_GAP = 20.0

_print_lock = threading.Lock()


def emit_progress(pct, stage):
    with _print_lock:
        print("PROGRESS\t%d\t%s" % (max(0, min(100, int(pct))), stage), flush=True)


def emit_info(msg):
    with _print_lock:
        print("INFO\t%s" % str(msg)[:500], flush=True)


def find_ffmpeg(explicit=None):
    if explicit and os.path.isfile(explicit):
        return explicit
    env = os.environ.get("FFMPEG")
    if env and os.path.exists(env):
        return env
    w = shutil.which("ffmpeg")
    if w:
        return w
    for p in (
        os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe"),
        r"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        r"C:\ffmpeg\bin\ffmpeg.exe",
    ):
        if os.path.exists(p):
            return p
    return "ffmpeg"


def run_ff(ff, args, timeout=900, cwd=None):
    return subprocess.run(
        [ff, "-hide_banner", "-loglevel", "error"] + args,
        capture_output=True, timeout=timeout, cwd=cwd,
        creationflags=NO_WINDOW, startupinfo=HIDE_SI)


def find_ffprobe(ff):
    d = os.path.dirname(ff) or "."
    cand = os.path.join(d, "ffprobe.exe" if os.name == "nt" else "ffprobe")
    if os.path.exists(cand):
        return cand
    w = shutil.which("ffprobe")
    return w or ""


def probe_duration(ff, path):
    fp = find_ffprobe(ff)
    if fp:
        r = subprocess.run(
            [fp, "-v", "error", "-show_entries", "format=duration",
             "-of", "default=noprint_wrappers=1:nokey=1", path],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            creationflags=NO_WINDOW, startupinfo=HIDE_SI)
        try:
            v = float((r.stdout or "").strip().splitlines()[0])
            if v > 0:
                return v
        except Exception:
            pass
    r = subprocess.run(
        [ff, "-hide_banner", "-i", path],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        creationflags=NO_WINDOW, startupinfo=HIDE_SI)
    m = re.search(r"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", r.stderr or "")
    if m:
        return int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))
    return 0.0


def chunk_features(ff, input_path, start, dur, wavname, cwd=None):
    """取 [start, start+dur) 的音频并计算逐秒特征。"""
    cwd = cwd or os.getcwd()
    r = run_ff(ff, ["-ss", str(start), "-t", str(dur),
                    "-i", input_path, "-vn", "-ac", "1", "-ar", "16000",
                    "-c:a", "pcm_s16le", "-y", wavname], cwd=cwd)
    wav = os.path.join(cwd, wavname)
    if not os.path.exists(wav) or os.path.getsize(wav) < 1000:
        err = (r.stderr or b"").decode("utf-8", "replace")[-300:]
        raise RuntimeError("音频提取失败：%s" % err)

    def band(name, filt, key):
        out = "b_%s.txt" % name
        run_ff(ff, ["-i", wavname, "-af",
                    ("%sasetnsamples=n=16000,astats=metadata=1:reset=1:length=1,"
                     "ametadata=print:key=%s:file=%s:direct=1"
                     % (filt, key, out)),
                    "-f", "null", "-"], cwd=cwd)
        vals = []
        fp = os.path.join(cwd, out)
        if not os.path.exists(fp):
            return vals
        with open(fp, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = re.search(re.escape(key) + r"=(-?[\d.]+|-?inf|nan)", line)
                if not m:
                    continue
                s = m.group(1)
                vals.append(None if s in ("nan", "inf", "-inf") else float(s))
        return vals

    K_RMS = "lavfi.astats.Overall.RMS_level"
    K_CREST = "lavfi.astats.1.Crest_factor"
    K_ZCR = "lavfi.astats.1.Zero_crossings_rate"

    full = band("full", "", K_RMS)
    bass = band("bass", "highpass=f=25,lowpass=f=100,", K_RMS)
    crest = band("crest", "", K_CREST)
    zcr = band("zcr", "", K_ZCR)

    n = min(len(full), len(bass), len(crest), len(zcr))
    rows = []
    for i in range(n):
        fv = full[i]
        rows.append({
            "t": int(start) + i,
            "rms_full": -80.0 if fv is None else fv,
            "bass_ratio": ((bass[i] - fv) if (bass[i] is not None
                                              and fv is not None and fv > -70) else -99.0),
            "crest": crest[i],
            "zcr": zcr[i],
        })
    return rows


# ---------------------------------------------------------------- 检测

def smooth(x, w):
    half = w // 2
    return [sum(x[max(0, i - half):min(len(x), i + half + 1)])
            / (min(len(x), i + half + 1) - max(0, i - half)) for i in range(len(x))]


def otsu(vals, bins=64):
    lo, hi = min(vals), max(vals)
    if hi - lo < 1e-6:
        return hi
    step = (hi - lo) / bins
    hist = [0] * bins
    for v in vals:
        k = min(bins - 1, max(0, int((v - lo) / step)))
        hist[k] += 1
    total = len(vals)
    sum_all = sum(((i + 0.5) * step + lo) * hist[i] for i in range(bins))
    sum_b = 0.0
    w_b = 0
    best, best_t = -1.0, hi
    for i in range(bins):
        w_b += hist[i]
        if w_b == 0:
            continue
        w_f = total - w_b
        if w_f == 0:
            break
        sum_b += ((i + 0.5) * step + lo) * hist[i]
        m_b = sum_b / w_b
        m_f = (sum_all - sum_b) / w_f
        var = w_b * w_f * (m_b - m_f) ** 2
        if var > best:
            best, best_t = var, (i + 1) * step + lo
    return best_t


def split_long(segs, rows, max_seg):
    out = []
    for a, b in segs:
        if b - a <= max_seg:
            out.append([a, b])
            continue
        k = int((b - a + max_seg - 1) // max_seg)
        cuts = []
        for i in range(1, k):
            target = a + (b - a) * i / k
            best = None
            for r in rows:
                if abs(r["t"] - target) > 90:
                    continue
                score = r["bass_ratio"] * 1.0 + r["rms_full"] * 0.15
                if best is None or score < best[1]:
                    best = (r["t"], score)
            if best is not None:
                cuts.append(best[0])
        cuts = sorted(set(cuts))
        prev = a
        for c in cuts:
            if c - prev >= 60:
                out.append([prev, c])
                prev = c
        out.append([prev, b])
    return out


def talk_gaps(rows, a, b, thr, bthr, min_len=TALK_MIN, ratio=TALK_RATIO):
    out = []
    run = None

    def close(s, e):
        seg = [r for r in rows if s <= r["t"] < e]
        if not seg or e - s < min_len:
            return
        talk = sum(1 for r in seg
                   if r["rms_full"] >= thr and r["bass_ratio"] < bthr)
        if talk / float(len(seg)) >= ratio:
            out.append((s, e))

    for r in rows:
        if not (a <= r["t"] < b):
            continue
        if r["rms_full"] >= thr and r["bass_ratio"] < bthr:
            if run is None:
                run = r["t"]
        else:
            if run is not None:
                close(run, r["t"])
                run = None
    if run is not None:
        close(run, b)
    return out


def split_by_talk(segs, rows, thr, bthr, long_seg=LONG_SEG, min_piece=PIECE_MIN):
    out = []
    for a, b in segs:
        if b - a <= long_seg:
            out.append([a, b])
            continue
        prev = a
        for s, e in talk_gaps(rows, a, b, thr, bthr):
            c = (s + e) // 2
            if c - prev >= min_piece and b - c >= min_piece:
                out.append([prev, c])
                prev = c
        out.append([prev, b])
    return out


def _med(xs):
    ys = sorted(v for v in xs if v is not None)
    if not ys:
        return None
    n = len(ys)
    return ys[n // 2] if n % 2 else (ys[n // 2 - 1] + ys[n // 2]) / 2.0


def silence_zones(rows):
    zones = []
    run = None
    for r in rows:
        quiet = r["rms_full"] < SILENCE_DB
        if quiet:
            if run is None:
                run = r["t"]
        else:
            if run is not None:
                if r["t"] - run >= SILENCE_RUN:
                    zones.append((run, r["t"]))
                run = None
    if run is not None and rows[-1]["t"] + 1 - run >= SILENCE_RUN:
        zones.append((run, rows[-1]["t"] + 1))
    return zones


def _dedup(ts, gap=15):
    if not ts:
        return []
    out = [ts[0]]
    for t in ts[1:]:
        if t - out[-1] < gap:
            out[-1] = (out[-1] + t) // 2
        else:
            out.append(t)
    return out


def bass_steps(rows):
    ins, outs = [], []
    n = len(rows)
    for i in range(BASS_WIN, n - BASS_WIN):
        before = _med([rows[j]["bass_ratio"] for j in range(i - BASS_WIN, i)])
        after = _med([rows[j]["bass_ratio"] for j in range(i, i + BASS_WIN)])
        if before is None or after is None:
            continue
        if before <= -95 or after <= -95:
            continue
        d = after - before
        if d >= BASS_JUMP:
            ins.append(rows[i]["t"])
        elif d <= -BASS_JUMP:
            outs.append(rows[i]["t"])
    return _dedup(ins), _dedup(outs)


def pad_boundaries(segs, rows, radius=PAD_RADIUS, min_seg=45.0):
    if radius <= 0:
        return segs, 0
    sil = silence_zones(rows)
    tmin = rows[0]["t"] if rows else 0
    tmax = (rows[-1]["t"] + 1) if rows else 0

    def blocked(lo, hi):
        return any(not (ze <= lo or zs >= hi) for zs, ze in sil)

    out = []
    for a, b in segs:
        na, nb = a, b
        step = 1
        while na - step >= max(tmin, a - radius):
            if blocked(na - step, na):
                break
            na -= 1
        step = 1
        while nb + step <= min(tmax, b + radius):
            if blocked(nb, nb + step):
                break
            nb += 1
        if nb - na < min_seg:
            na, nb = a, b
        out.append([na, nb])

    out.sort(key=lambda x: x[0])
    fixed = []
    for a, b in out:
        if fixed and a < fixed[-1][1]:
            mid = (a + fixed[-1][1]) // 2
            if mid - fixed[-1][0] >= min_seg and b - mid >= min_seg:
                fixed[-1][1] = mid
                a = mid
            else:
                a = fixed[-1][1]
        fixed.append([a, b])

    changed = sum(1 for (a, b), (x, y) in zip(segs, fixed)
                  if abs(a - x) > 3 or abs(b - y) > 3)
    return fixed, changed


def snap_boundaries(segs, rows, radius=45, min_seg=45.0):
    if radius <= 0 or not segs:
        return segs, 0
    ins, outs = bass_steps(rows)
    if not ins and not outs:
        return segs, 0
    out = []
    for a, b in segs:
        na, nb = a, b
        for t in ins:
            if abs(t - a) <= radius and abs(t - a) < abs(na - a) + 1:
                na = t
        for t in outs:
            if abs(t - b) <= radius and abs(t - b) < abs(nb - b) + 1:
                nb = t
        if nb - na < min_seg:
            na, nb = a, b
        out.append([na, nb])

    out.sort(key=lambda x: x[0])
    fixed = []
    for a, b in out:
        if fixed and a < fixed[-1][1]:
            mid = (a + fixed[-1][1]) // 2
            if mid - fixed[-1][0] >= min_seg and b - mid >= min_seg:
                fixed[-1][1] = mid
                a = mid
            else:
                a = fixed[-1][1]
        fixed.append([a, b])

    changed = sum(1 for (a, b), (x, y) in zip(segs, fixed)
                  if abs(a - x) > 3 or abs(b - y) > 3)
    return fixed, changed


def refine_by_bass(segs, rows, min_seg=45.0, max_seg=720.0):
    if not segs:
        return segs, 0
    ins, _outs = bass_steps(rows)
    if not ins:
        return segs, 0

    out = []
    changed = 0
    for a, b in segs:
        if b - a <= max_seg:
            out.append([a, b])
            continue
        pts = sorted(t for t in ins if a + min_seg <= t <= b - min_seg)
        if not pts:
            out.append([a, b])
            continue
        prev = a
        for t in pts + [b]:
            if t - prev > max_seg or t == b:
                usable = [x for x in pts if prev + min_seg <= x <= prev + max_seg]
                if t == b and (b - prev) <= max_seg:
                    cut = b
                elif usable:
                    cut = usable[-1]
                else:
                    cut = min(t, b)
                if cut - prev >= min_seg:
                    out.append([prev, cut])
                    prev = cut
        if b - prev >= min_seg:
            out.append([prev, b])
        elif out:
            out[-1][1] = max(out[-1][1], b)
        changed += 1

    fixed = []
    for a, b in out:
        if b - a > max_seg:
            fixed.extend(split_long([[a, b]], rows, max_seg))
        else:
            fixed.append([a, b])

    merged = []
    for a, b in sorted(fixed, key=lambda x: x[0]):
        if merged and (b - a < min_seg or a < merged[-1][1]):
            merged[-1][1] = max(merged[-1][1], b)
        else:
            merged.append([a, b])
    return merged, changed


def talk_ratio(rows, a, b, thr, bthr):
    seg = [r for r in rows if a <= r["t"] < b]
    if not seg:
        return 0.0
    n = sum(1 for r in seg if r["rms_full"] >= thr and r["bass_ratio"] < bthr)
    return n / float(len(seg))


def block_thresholds(sm, block=THR_BLOCK):
    """按块分别求 Otsu 阈值。

    整段共用一个阈值时，把两段不同增益的录播拼在一起（实测 -33 dB 与 -20 dB）
    会把安静的那一整段判成静音。分块后每块只跟自己的响度分布比较。
    """
    n = len(sm)
    out = [-70.0] * n
    for b0 in range(0, n, block):
        vals = [v for v in sm[b0:b0 + block] if v > -70]
        if len(vals) < 20:
            continue
        t = otsu(vals)
        for i in range(b0, min(b0 + block, n)):
            out[i] = t
    return out


def detect(rows, min_seg, min_gap, max_seg=720, snap=90):
    if len(rows) < 30:
        return [], None, None, 0
    f = [r["rms_full"] for r in rows]
    br = [r["bass_ratio"] for r in rows]
    sm = smooth(f, 11)
    smb = smooth(br, 11)

    vals = [v for v in sm if v > -70]
    if len(vals) < 20:
        return [], None, None, 0
    thr = otsu(vals)
    local_thr = block_thresholds(sm)

    bvals = [v for v in smb if v > -90]
    bthr = otsu(bvals) if len(bvals) >= 20 else -90.0

    # 只按响度判断「这一秒有没有声音」；伴奏判据只留给边界吸附。
    # 原来的双判据要求 25-100Hz 伴奏够强，说话内容会被整段丢掉
    # （实测 5 分钟录播只剩 35% 覆盖率，且拼成 1 段）。
    mask = [sm[i] > local_thr[i] for i in range(len(rows))]

    runs = []
    i = 0
    while i < len(mask):
        if mask[i]:
            j = i
            while j + 1 < len(mask) and mask[j + 1]:
                j += 1
            runs.append([rows[i]["t"], rows[j]["t"] + 1])
            i = j + 1
        else:
            i += 1

    merged = []
    for r in runs:
        if not merged:
            merged.append(list(r))
            continue
        gap_a, gap_b = merged[-1][1], r[0]
        gap_len = gap_b - gap_a
        if gap_len >= min_gap:
            merged.append(list(r))
            continue
        if gap_len >= TALK_GAP and talk_ratio(rows, gap_a, gap_b, thr, bthr) >= TALK_RATIO:
            merged.append(list(r))
        else:
            merged[-1][1] = r[1]

    kept = []
    pend = None
    for r in merged:
        if r[1] - r[0] >= min_seg:
            kept.append([pend[0], r[1]] if pend is not None else r)
            pend = None
        elif kept:
            kept[-1][1] = r[1]
        else:
            pend = r if pend is None else [pend[0], r[1]]
    if pend is not None:
        kept.append(pend)

    changed = 0
    if snap > 0:
        kept, n0 = refine_by_bass(kept, rows, min_seg=min_seg, max_seg=max_seg)
        changed += n0
    split = split_long(kept, rows, max_seg)

    if snap > 0:
        split, n1 = pad_boundaries(split, rows, radius=snap, min_seg=min_seg)
        split, n2 = snap_boundaries(split, rows, radius=max(30, snap // 2),
                                    min_seg=min_seg)
        changed = max(changed, n1, n2)

    if split:
        split = split_by_talk(split, rows, thr, bthr)
    return split, thr, bthr, changed


# ---------------------------------------------------------------- 主流程

def main():
    ap = argparse.ArgumentParser(description="本地录播文件自动切片检测")
    ap.add_argument("--input", required=True)
    ap.add_argument("--output", required=True)
    ap.add_argument("--ffmpeg", default="")
    ap.add_argument("--min-seg", type=float, default=45)
    ap.add_argument("--min-gap", type=float, default=10)
    ap.add_argument("--max-seg", type=float, default=720)
    ap.add_argument("--snap", type=float, default=90)
    ap.add_argument("--chunk", type=float, default=CHUNK)
    ap.add_argument("--workers", type=int, default=DEFAULT_WORKERS)
    args = ap.parse_args()

    ff = find_ffmpeg(args.ffmpeg)
    if not os.path.isfile(args.input):
        emit_info("输入文件不存在：%s" % args.input)
        return 2
    emit_info("使用 ffmpeg：%s" % ff)

    total = probe_duration(ff, args.input)
    if total <= 0:
        emit_info("无法读取视频时长")
        return 3
    emit_info("时长 %.1f 秒" % total)
    emit_progress(0, "准备分析")

    work = os.path.join(os.path.dirname(os.path.abspath(args.output)), ".slice_work")
    os.makedirs(work, exist_ok=True)

    chunk_len = max(60, int(args.chunk))
    jobs = [(s, min(chunk_len, total - s)) for s in range(0, int(math.ceil(total)), chunk_len)]
    workers = max(1, min(int(args.workers), len(jobs)))
    done = [0]
    lock = threading.Lock()
    rows_by_index = {}

    def one_chunk(idx_and_job):
        idx, (start, dur) = idx_and_job
        cwd = os.path.join(work, "w%d" % (idx % workers))
        os.makedirs(cwd, exist_ok=True)
        for attempt in range(3):
            try:
                rows = chunk_features(ff, os.path.abspath(args.input), start, dur,
                                      "chunk.wav", cwd=cwd)
                break
            except Exception as e:
                if attempt == 2:
                    raise
                emit_info("第 %d 块重试：%s" % (idx + 1, e))
                time.sleep(1.5)
        with lock:
            done[0] += 1
            emit_progress(90.0 * done[0] / len(jobs), "分析音频 %d/%d" % (done[0], len(jobs)))
        return idx, rows

    rows = []
    try:
        with ThreadPoolExecutor(max_workers=workers) as pool:
            for idx, part in pool.map(one_chunk, list(enumerate(jobs))):
                rows_by_index[idx] = part
    except Exception as e:
        emit_info("音频分析失败：%s" % e)
        return 4

    for idx in sorted(rows_by_index):
        rows.extend(rows_by_index[idx])
    rows.sort(key=lambda r: r["t"])
    emit_info("逐秒特征 %d 条" % len(rows))

    emit_progress(92, "检测片段")
    segments, thr, bthr, changed = detect(
        rows, args.min_seg, args.min_gap, args.max_seg, args.snap)

    clean = []
    for a, b in segments:
        a = max(0, int(round(a)))
        b = min(int(round(total)), int(round(b)))
        if b - a >= max(10, int(args.min_seg)):
            clean.append({"start": a, "end": b})

    with open(args.output, "w", encoding="utf-8") as f:
        json.dump({
            "input": os.path.abspath(args.input),
            "duration": round(total, 1),
            "min_seg": args.min_seg,
            "min_gap": args.min_gap,
            "max_seg": args.max_seg,
            "snap": args.snap,
            "threshold_rms": thr,
            "threshold_bass": bthr,
            "count": len(clean),
            "segments": clean,
        }, f, ensure_ascii=False, indent=2)

    emit_info("检测完成：%d 段" % len(clean))
    emit_progress(100, "完成")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        emit_info("异常：%s" % exc)
        sys.exit(1)
