// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveStreamClipTool.Models;

namespace LiveStreamClipTool.Services;

/// <summary>
/// B 站匿名接口封装：UID 探测、直播间信息、直播取流（FLV 优先，失败回退 HLS）。
/// 匿名调用会要求 Referer，部分接口在未登录时仍可正常返回。
/// </summary>
public sealed class BilibiliService
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private const string RoomInfoApi = "https://api.live.bilibili.com/room/v1/Room/get_info";
    private const string StatusByUidApi =
        "https://api.live.bilibili.com/room/v1/Room/get_status_info_by_uids";
    private const string PlayUrlApi = "https://api.live.bilibili.com/room/v1/Room/playUrl";
    private const string PlayInfoApi =
        "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo";
    private const string HomepageDataApi =
        "https://api.live.bilibili.com/xlive/web-interface/v1/index/getList?platform=web";
    private const string AreaListApi =
        "https://api.live.bilibili.com/room/v1/Area/getList?show_pinyin=1";
    private const string AreaRoomListApi =
        "https://api.live.bilibili.com/room/v1/Area/getRoomList";

    private readonly HttpClient _http;

    public BilibiliService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
    }

    /// <summary>接受 UID、空间链接、直播间链接或房间号。</summary>
    public async Task<StreamerProbe> ProbeAsync(string input, CancellationToken ct = default)
    {
        var raw = (input ?? "").Trim();
        if (raw.Length == 0)
        {
            throw new InvalidOperationException("请填写主播 UID 或直播间链接");
        }

        var roomMatch = Regex.Match(raw, @"live\.bilibili\.com/(\d+)");
        var spaceMatch = Regex.Match(raw, @"space\.bilibili\.com/(\d+)");
        string uid;
        string viaRoom = "";

        if (roomMatch.Success)
        {
            viaRoom = roomMatch.Groups[1].Value;
            var room = await GetRoomInfoAsync(viaRoom, ct);
            uid = room.Uid;
            if (string.IsNullOrEmpty(uid))
            {
                throw new InvalidOperationException("这个直播间没有绑定主播 UID");
            }
        }
        else if (spaceMatch.Success)
        {
            uid = spaceMatch.Groups[1].Value;
        }
        else if (raw.All(char.IsDigit))
        {
            uid = raw;
        }
        else
        {
            throw new InvalidOperationException(
                "UID 必须是纯数字或直播间链接（如 live.bilibili.com/12345）");
        }

        var (uname, face, roomIdFromStatus, titleFromStatus, living) =
            await GetStatusByUidAsync(uid, ct);

        if (string.IsNullOrEmpty(roomIdFromStatus))
        {
            throw new InvalidOperationException(
                $"没查到 UID {uid}：请确认是 B 站空间号（个人主页网址里的数字）");
        }

        var title = titleFromStatus;
        var roomId = roomIdFromStatus;
        var livingNow = living;
        try
        {
            var room = await GetRoomInfoAsync(roomId, ct);
            roomId = room.RoomId;
            title = room.Title;
            livingNow = room.Living;
        }
        catch
        {
            // 直播间信息接口偶发抖动时用状态接口的结果兜底
        }

        return new StreamerProbe
        {
            Uid = uid,
            Nickname = uname,
            RoomId = roomId,
            RoomTitle = title,
            FaceUrl = face,
            Living = livingNow
        };
    }

    private async Task<(string Uname, string Face, string RoomId, string Title, bool Living)>
        GetStatusByUidAsync(string uid, CancellationToken ct)
    {
        var url = $"{StatusByUidApi}?uids[]={Uri.EscapeDataString(uid)}";
        using var doc = await GetJsonAsync(url, "https://live.bilibili.com/", ct);
        var root = doc.RootElement;
        if (GetInt(root, "code") != 0)
        {
            throw new InvalidOperationException(
                $"查询主播失败：code={GetInt(root, "code")} {GetString(root, "message")}");
        }
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return ("", "", "", "", false);
        }
        if (!data.TryGetProperty(uid, out var info))
        {
            return ("", "", "", "", false);
        }
        return (GetString(info, "uname"),
                GetString(info, "face"),
                GetString(info, "room_id"),
                GetString(info, "title"),
                GetInt(info, "live_status") == 1);
    }

    public async Task<RoomInfo> GetRoomInfoAsync(string roomId, CancellationToken ct = default)
    {
        var url = $"{RoomInfoApi}?room_id={Uri.EscapeDataString(roomId)}";
        using var doc = await GetJsonAsync(url, $"https://live.bilibili.com/{roomId}", ct);
        var root = doc.RootElement;
        if (GetInt(root, "code") != 0)
        {
            throw new InvalidOperationException(
                $"直播间信息获取失败：code={GetInt(root, "code")} {GetString(root, "message")}");
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("直播间不存在");
        }
        var rid = GetString(data, "room_id");
        if (rid.Length == 0 || !rid.All(char.IsDigit))
        {
            throw new InvalidOperationException("直播间不存在");
        }
        var area = GetString(data, "area_name");
        var parent = GetString(data, "parent_area_name");
        return new RoomInfo
        {
            Uid = GetString(data, "uid"),
            RoomId = rid,
            Title = GetString(data, "title"),
            Area = parent.Length > 0 && area.Length > 0 ? $"{parent} · {area}" : area,
            Cover = GetString(data, "keyframe").Length > 0
                ? GetString(data, "keyframe") : GetString(data, "user_cover"),
            Living = GetInt(data, "live_status") == 1
        };
    }

    /// <summary>
    /// 实时读取 B 站直播主页数据，得到主页展示的一级分区（parentAreaId），
    /// 再取分区树补全名称和二级分区（areaId）。每次调用都会重新请求，不做缓存。
    /// </summary>
    public async Task<List<AreaCategory>> GetAreaCategoriesAsync(CancellationToken ct = default)
    {
        var parentOrder = new List<int>();
        var seen = new HashSet<int>();
        var parents = new Dictionary<int, (string Name, List<AreaSubCategory> Subs)>();

        using (var home = await GetJsonAsync(HomepageDataApi, "https://live.bilibili.com/", ct))
        {
            var root = home.RootElement;
            if (GetInt(root, "code") != 0)
            {
                throw new InvalidOperationException(
                    $"读取直播主页分区失败：code={GetInt(root, "code")} {GetString(root, "message")}");
            }
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("area_entrance_v2", out var entrance) &&
                    entrance.ValueKind == JsonValueKind.Object &&
                    entrance.TryGetProperty("list", out var entranceList) &&
                    entranceList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in entranceList.EnumerateArray())
                    {
                        AddParentLink(GetString(item, "link"));
                    }
                }
                if (data.TryGetProperty("room_list", out var rooms) &&
                    rooms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var room in rooms.EnumerateArray())
                    {
                        if (room.TryGetProperty("module_info", out var module) &&
                            module.ValueKind == JsonValueKind.Object)
                        {
                            AddParentLink(GetString(module, "link"));
                        }
                    }
                }
            }
        }

        using (var doc = await GetJsonAsync(AreaListApi, "https://live.bilibili.com/", ct))
        {
            var root = doc.RootElement;
            if (GetInt(root, "code") != 0 ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"读取分区列表失败：code={GetInt(root, "code")} {GetString(root, "message")}");
            }
            foreach (var parent in data.EnumerateArray())
            {
                var parentId = (int)GetInt(parent, "id");
                var subs = new List<AreaSubCategory>();
                if (parent.TryGetProperty("list", out var subList) &&
                    subList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sub in subList.EnumerateArray())
                    {
                        var subId = int.TryParse(GetString(sub, "id"), out var sid) ? sid : 0;
                        var subName = GetString(sub, "name");
                        if (subId > 0 && subName.Length > 0)
                        {
                            subs.Add(new AreaSubCategory { Id = subId, Name = subName });
                        }
                    }
                }
                parents[parentId] = (GetString(parent, "name"), subs);
            }
        }

        var result = new List<AreaCategory>();
        foreach (var parentId in parentOrder)
        {
            if (!parents.TryGetValue(parentId, out var info) ||
                info.Name.Length == 0 || info.Subs.Count == 0)
            {
                continue;
            }
            var category = new AreaCategory { ParentId = parentId, Name = info.Name };
            category.SubAreas.AddRange(info.Subs);
            result.Add(category);
        }

        // 主页结构变动时兜底：退回分区树里所有带二级分区的父分区
        if (result.Count == 0)
        {
            foreach (var (parentId, info) in parents)
            {
                if (info.Name.Length == 0 || info.Subs.Count == 0)
                {
                    continue;
                }
                var category = new AreaCategory { ParentId = parentId, Name = info.Name };
                category.SubAreas.AddRange(info.Subs);
                result.Add(category);
            }
        }
        return result;

        void AddParentLink(string link)
        {
            var match = Regex.Match(link ?? "", @"[?&]parentAreaId=(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var parentId) &&
                parentId > 0 && seen.Add(parentId))
            {
                parentOrder.Add(parentId);
            }
        }
    }

    /// <summary>逐页抓取指定分区当前所有直播中的主播，按 UID 去重。</summary>
    public async Task<List<AreaRoom>> GetAreaRoomsAsync(
        int parentAreaId, int areaId, CancellationToken ct = default)
    {
        var result = new List<AreaRoom>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var referer = "https://live.bilibili.com/p/eden/area-tags" +
                      $"?areaId={areaId}&parentAreaId={parentAreaId}";

        for (var page = 1; page <= 50; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{AreaRoomListApi}?parent_area_id={parentAreaId}&area_id={areaId}" +
                      $"&sort_type=online&page={page}&page_size=50";
            using var doc = await GetJsonAsync(url, referer, ct);
            var root = doc.RootElement;
            if (GetInt(root, "code") != 0)
            {
                throw new InvalidOperationException(
                    $"获取分区主播失败：code={GetInt(root, "code")} {GetString(root, "message")}");
            }
            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                break;
            }
            foreach (var item in data.EnumerateArray())
            {
                var uid = GetString(item, "uid");
                if (uid.Length == 0 || !seen.Add(uid))
                {
                    continue;
                }
                result.Add(new AreaRoom
                {
                    Uid = uid,
                    Nickname = GetString(item, "uname"),
                    RoomId = GetString(item, "roomid"),
                    RoomTitle = GetString(item, "title"),
                    FaceUrl = GetString(item, "face")
                });
            }
            await Task.Delay(120, ct);
        }
        return result;
    }

    /// <summary>取直播流地址。先试老 FLV 接口，不行再走新 HLS 接口（只取 H.264）。</summary>
    public async Task<string> GetLiveStreamUrlAsync(string roomId, int qn, CancellationToken ct = default)
    {
        var flvError = "";
        try
        {
            var url = $"{PlayUrlApi}?cid={Uri.EscapeDataString(roomId)}&qn={qn}" +
                      "&platform=web&ptype=8";
            using var doc = await GetJsonAsync(url, $"https://live.bilibili.com/{roomId}", ct);
            var root = doc.RootElement;
            if (GetInt(root, "code") == 0 &&
                root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("durl", out var durl) &&
                durl.ValueKind == JsonValueKind.Array &&
                durl.GetArrayLength() > 0)
            {
                var media = GetString(durl[0], "url");
                if (media.Length > 0)
                {
                    return media;
                }
            }
            flvError = $"code={GetInt(root, "code")}";
        }
        catch (Exception ex)
        {
            flvError = ex.Message;
        }

        var hls = await TryGetHlsUrlAsync(roomId, qn, ct);
        if (hls.Length > 0)
        {
            return hls;
        }
        throw new InvalidOperationException(
            string.IsNullOrEmpty(flvError)
                ? "没有取到直播流地址（可能刚下播）"
                : $"没有取到直播流地址：{flvError}");
    }

    private async Task<string> TryGetHlsUrlAsync(string roomId, int qn, CancellationToken ct)
    {
        try
        {
            var url = $"{PlayInfoApi}?room_id={Uri.EscapeDataString(roomId)}" +
                      $"&protocol=0,1&format=0,1,2&codec=0,1&qn={qn}" +
                      "&platform=web&ptype=8&dolby=5&panorama=1";
            using var doc = await GetJsonAsync(url, $"https://live.bilibili.com/{roomId}", ct);
            var root = doc.RootElement;
            if (GetInt(root, "code") != 0 ||
                !root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("playurl_info", out var info) ||
                info.ValueKind != JsonValueKind.Object ||
                !info.TryGetProperty("playurl", out var playurl))
            {
                return "";
            }
            if (!playurl.TryGetProperty("stream", out var streams) ||
                streams.ValueKind != JsonValueKind.Array)
            {
                return "";
            }
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("format", out var formats) ||
                    formats.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var format in formats.EnumerateArray())
                {
                    if (!format.TryGetProperty("codec", out var codecs) ||
                        codecs.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var codec in codecs.EnumerateArray())
                    {
                        if (GetString(codec, "codec_name") != "avc")
                        {
                            continue;
                        }
                        var baseUrl = GetString(codec, "base_url");
                        if (baseUrl.Length == 0 ||
                            !codec.TryGetProperty("url_info", out var urlInfos) ||
                            urlInfos.ValueKind != JsonValueKind.Array ||
                            urlInfos.GetArrayLength() == 0)
                        {
                            continue;
                        }
                        var host = GetString(urlInfos[0], "host");
                        var extra = GetString(urlInfos[0], "extra");
                        if (host.Length > 0)
                        {
                            return host + baseUrl + extra;
                        }
                    }
                }
            }
        }
        catch
        {
            // 新接口失败时返回空串，由调用方决定报错
        }
        return "";
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", referer);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : el.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetRawText()
                : "";

    private static long GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number &&
        p.TryGetInt64(out var v) ? v : 0;
}
