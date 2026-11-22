# Copyright (c) 2026 cnoctave.top
# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of LiveStreamClipTool; see LICENSE for the full license text.

"""用 faster-whisper 把切片音频转成文本（由应用调用）。

语音通过 ffmpeg 解码为 16k 单声道 PCM 后直接传给模型，
避免 faster-whisper 内置 PyAV 解码在不同 av 版本间的兼容问题。
"""
import argparse
import os
import subprocess
import sys
import tempfile
import wave


def stage(msg: str) -> None:
    print("STAGE\t" + msg, flush=True)


def fail(code: int, msg: str) -> None:
    print("ERROR\t" + msg, flush=True)
    sys.exit(code)


def load_audio(ffmpeg: str, input_path: str):
    import numpy as np

    fd, wav_path = tempfile.mkstemp(suffix=".wav", prefix="lsct_whisper_")
    os.close(fd)
    try:
        proc = subprocess.run(
            [ffmpeg, "-hide_banner", "-loglevel", "error",
             "-i", input_path, "-vn", "-ac", "1", "-ar", "16000",
             "-f", "wav", wav_path, "-y"],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        if proc.returncode != 0:
            lines = proc.stderr.strip().splitlines()
            fail(4, "音频解码失败：" + (lines[-1] if lines else "ffmpeg 返回错误"))
        with wave.open(wav_path, "rb") as w:
            pcm = w.readframes(w.getnframes())
        return np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
    finally:
        try:
            os.remove(wav_path)
        except OSError:
            pass


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--model", default="small")
    parser.add_argument("--language", default="zh")
    args = parser.parse_args()

    use_mirror = os.environ.get("_WHISPER_MIRROR") == "1"
    if use_mirror:
        os.environ.setdefault("HF_ENDPOINT", "https://hf-mirror.com")
    os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

    try:
        from faster_whisper import WhisperModel
    except ImportError:
        fail(3, "未安装 faster-whisper，请先到「依赖管理」安装 Whisper 语音识别")

    stage("正在加载 Whisper 模型（首次使用需下载约 500MB，请耐心等待）…")
    try:
        model = WhisperModel(args.model, device="cpu", compute_type="int8")
    except Exception as ex:
        if not use_mirror and not os.environ.get("HF_ENDPOINT"):
            stage("直连 HuggingFace 失败，改用镜像 hf-mirror.com 重试…")
            env = dict(os.environ)
            env["_WHISPER_MIRROR"] = "1"
            os.execve(sys.executable,
                      [sys.executable, os.path.abspath(__file__)] + sys.argv[1:], env)
        lines = str(ex).strip().splitlines()
        fail(2, "模型加载失败：" + (lines[0] if lines else ex.__class__.__name__))

    stage("正在解码音频…")
    audio = load_audio(args.ffmpeg, args.input)
    total_sec = max(1.0, len(audio) / 16000.0)

    stage("正在识别语音，片段较长时耗时较久…")
    segments, info = model.transcribe(
        audio, language=args.language or None, vad_filter=True, beam_size=5)

    lines = []
    for seg in segments:
        text = seg.text.strip()
        if text:
            lines.append(text)
        print("PROGRESS\t%d" % min(99, int(seg.end / total_sec * 100)), flush=True)

    text = "\n".join(lines)
    with open(args.output, "w", encoding="utf-8") as f:
        f.write(text)
    print("INFO\tlanguage=%s segments=%d" % (info.language, len(lines)), flush=True)
    stage("识别完成，共 %d 句" % len(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main())
