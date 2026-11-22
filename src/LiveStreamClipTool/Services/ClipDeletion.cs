// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Services;

/// <summary>删除切片的媒体文件、同名文字、横竖版封面与数据库记录；队列任务由外键级联删除。</summary>
public static class ClipDeletion
{
    public static (bool Missing, string Error) DeleteClipFiles(string filePath)
    {
        var transcriptPath = WhisperService.TranscriptPathFor(filePath);
        try
        {
            if (File.Exists(transcriptPath))
            {
                File.Delete(transcriptPath);
            }
        }
        catch (Exception ex)
        {
            return (false, $"文字文件 {Path.GetFileName(transcriptPath)}：{ex.Message}");
        }

        var (coverH, coverV) = CoverService.PathsFor(filePath);
        foreach (var cover in new[] { coverH, coverV })
        {
            try
            {
                if (File.Exists(cover))
                {
                    File.Delete(cover);
                }
            }
            catch (Exception ex)
            {
                return (false, $"封面文件 {Path.GetFileName(cover)}：{ex.Message}");
            }
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return (false, "");
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"{Path.GetFileName(filePath)}：{ex.Message}");
        }
    }

    public static (bool Ok, bool Missing, string Error) Delete(long clipId, string filePath)
    {
        var (missing, error) = DeleteClipFiles(filePath);
        if (error.Length > 0)
        {
            return (false, missing, error);
        }

        try
        {
            AppServices.Clips.Delete(clipId);
            return (true, missing, "");
        }
        catch (Exception ex)
        {
            return (false, missing, $"数据库记录 {clipId}：{ex.Message}");
        }
    }
}
