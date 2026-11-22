// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LiveStreamClipTool.Services;

public sealed record CoverFrame(int Index, double Time, string Path);

public sealed record CoverResult(string HorizontalPath, string VerticalPath, string FontName);

/// <summary>
/// 自动封面：ffmpeg 均匀截 4 帧，再用内置的 5 种 OFL 免费商用字体把多行文字渲染成
/// 横版 16:9 与竖版 3:4 两张 PNG，保存到切片同目录。
/// </summary>
public sealed class CoverService
{
    public const int FrameCount = 4;

    private static readonly (string File, string Name)[] FontFiles =
    {
        ("ZCOOLKuaiLe-Regular.ttf", "站酷快乐体"),
        ("ZCOOLQingKeHuangYou-Regular.ttf", "站酷庆科黄油体"),
        ("ZCOOLXiaoWei-Regular.ttf", "站酷小薇体"),
        ("MaShanZheng-Regular.ttf", "马善政毛笔楷书"),
        ("LongCang-Regular.ttf", "龙藏体"),
    };

    private static readonly Color[] Palette =
    {
        Color.White,
        Color.FromArgb(255, 228, 92),
        Color.FromArgb(124, 249, 255),
        Color.FromArgb(255, 156, 240),
        Color.FromArgb(168, 255, 176),
        Color.FromArgb(255, 184, 107),
        Color.FromArgb(201, 182, 255),
    };

    private static readonly Lazy<(PrivateFontCollection Collection, FontFamily[] Families, string[] Names)>
        Fonts = new(LoadFonts);

    private readonly DependencyService _deps;
    private readonly PathService _paths;

    public CoverService(DependencyService deps, PathService paths)
    {
        _deps = deps;
        _paths = paths;
    }

    public static (string Horizontal, string Vertical) PathsFor(string clipPath)
    {
        var dir = Path.GetDirectoryName(clipPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(clipPath);
        return (Path.Combine(dir, baseName + "_横版.png"),
                Path.Combine(dir, baseName + "_竖版.png"));
    }

    public async Task<IReadOnlyList<CoverFrame>> ExtractFramesAsync(
        string videoPath, double durationSeconds, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("切片文件不存在", videoPath);
        }
        var ffmpeg = _deps.FindFfmpegPath();
        if (ffmpeg.Length == 0)
        {
            throw new InvalidOperationException("未检测到 ffmpeg，请先到「依赖管理」安装");
        }

        var duration = durationSeconds > 0.5 ? durationSeconds : 4.0;
        var key = Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes(videoPath.ToLowerInvariant())))[..12];
        var dir = Path.Combine(_paths.TempDir, "cover_frames", key);
        Directory.CreateDirectory(dir);

        var frames = new List<CoverFrame>(FrameCount);
        for (var i = 0; i < FrameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var time = duration * (i + 0.5) / FrameCount;
            var path = Path.Combine(dir, $"frame_{i + 1}.png");
            try
            {
                File.Delete(path);
            }
            catch
            {
                // 旧文件删不掉时由 -y 覆盖
            }
            var ok = await RunFfmpegAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", time.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", videoPath,
                "-frames:v", "1",
                "-vf", "scale='min(1920,iw)':-2",
                "-y", path,
            }, cancellationToken).ConfigureAwait(false);
            if (!ok || !File.Exists(path))
            {
                throw new InvalidOperationException($"截取第 {i + 1} 帧失败");
            }
            frames.Add(new CoverFrame(i, time, path));
        }
        return frames;
    }

    public CoverResult RenderCovers(
        string framePath, IReadOnlyList<string> lines, string horizontalPath, string verticalPath)
    {
        if (lines.Count == 0)
        {
            throw new InvalidOperationException("封面文字不能为空");
        }
        if (!File.Exists(framePath))
        {
            throw new FileNotFoundException("截帧文件不存在", framePath);
        }

        var (_, families, names) = Fonts.Value;
        var pick = Random.Shared.Next(families.Length);
        using var source = Image.FromFile(framePath);
        RenderOne(source, families[pick], lines, horizontalPath, 1920, 1080);
        RenderOne(source, families[pick], lines, verticalPath, 1080, 1440);
        return new CoverResult(horizontalPath, verticalPath, names[pick]);
    }

    private static void RenderOne(
        Image source, FontFamily family, IReadOnlyList<string> lines,
        string outPath, int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.CompositingQuality = CompositingQuality.HighQuality;

        var scale = Math.Max((float)width / source.Width, (float)height / source.Height);
        var dw = source.Width * scale;
        var dh = source.Height * scale;
        g.DrawImage(source, (width - dw) / 2f, (height - dh) / 2f, dw, dh);

        using (var dim = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
        {
            g.FillRectangle(dim, 0, 0, width, height);
        }
        using (var grad = new LinearGradientBrush(
                   new Rectangle(0, 0, width, height),
                   Color.FromArgb(30, 0, 0, 0), Color.FromArgb(175, 0, 0, 0),
                   LinearGradientMode.Vertical))
        {
            g.FillRectangle(grad, 0, 0, width, height);
        }

        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        var maxWidth = width * 0.86f;
        var maxHeight = height * 0.72f;
        var size = Math.Min(width * 0.11f, height * 0.24f);
        Font? font = null;
        try
        {
            while (true)
            {
                font?.Dispose();
                font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
                var widest = 0f;
                foreach (var line in lines)
                {
                    widest = Math.Max(widest, g.MeasureString(line, font, int.MaxValue, format).Width);
                }
                var total = size * 1.32f * lines.Count;
                if ((widest <= maxWidth && total <= maxHeight) || size <= 22)
                {
                    break;
                }
                size *= 0.92f;
            }

            var lineHeight = size * 1.32f;
            var top = (height - lineHeight * lines.Count) / 2f;
            var outline = Math.Max(2f, size * 0.05f);
            Color? previous = null;
            for (var i = 0; i < lines.Count; i++)
            {
                Color color;
                do
                {
                    color = Palette[Random.Shared.Next(Palette.Length)];
                } while (previous is { } prev && color.ToArgb() == prev.ToArgb());
                previous = color;

                using var path = new GraphicsPath();
                path.AddString(lines[i], family, (int)FontStyle.Regular, size,
                    new RectangleF(0, top + i * lineHeight, width, lineHeight), format);
                using (var pen = new Pen(Color.FromArgb(210, 0, 0, 0), outline * 2))
                {
                    g.DrawPath(pen, path);
                }
                using (var brush = new SolidBrush(color))
                {
                    g.FillPath(brush, path);
                }
            }
        }
        finally
        {
            font?.Dispose();
        }

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        bmp.Save(outPath, ImageFormat.Png);
    }

    private static async Task<bool> RunFfmpegAsync(
        string ffmpeg, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var errTask = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(true);
            }
            catch
            {
                // ignore
            }
            throw;
        }
        await errTask.ConfigureAwait(false);
        return proc.ExitCode == 0;
    }

    private static (PrivateFontCollection, FontFamily[], string[]) LoadFonts()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        var collection = new PrivateFontCollection();
        var families = new List<FontFamily>();
        var names = new List<string>();
        foreach (var (file, name) in FontFiles)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                continue;
            }
            var before = collection.Families.Length;
            collection.AddFontFile(path);
            if (collection.Families.Length > before)
            {
                families.Add(collection.Families[^1]);
                names.Add(name);
            }
        }
        if (families.Count == 0)
        {
            throw new InvalidOperationException("未找到封面字体文件（Assets/Fonts）");
        }
        return (collection, families.ToArray(), names.ToArray());
    }
}
