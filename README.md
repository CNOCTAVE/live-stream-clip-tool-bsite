# 批量切片工具（B站）

一个面向 B 站直播录播的 Windows 桌面工具：批量管理主播、无人值守录制、自动切片、
语音转文字与封面生成，全流程集中在一个界面内完成。

- 仅使用 B 站**匿名公开接口**，不需要登录、不保存 Cookie
- 基于 WinUI 3 / Windows App SDK 1.8，界面支持 Mica 背景
- 数据保存在本机 SQLite，录播与切片文件保存在自定义根目录

## 功能

### 主播管理
- 通过 UID、个人空间链接或直播间链接探测主播信息（昵称、直播间、标题、开播状态）
- 「一键添加分区推荐主播」：实时读取 B 站直播主页的一级/二级分区，
  可限制每次添加的主播数量（0 表示不限），重复主播自动更新而不是重复插入
- 搜索、按列排序、手动拖动排序、批量删除
- 删除主播时级联清理其录播、切片、字幕、封面与队列任务

### 录播管理
- 一键开始/停止录制（FLV 优先，失败回退 HLS，只取 H.264）
- 可设置达到指定时长后自动停止，并在停止后自动进入切片队列
- 程序异常退出后自动恢复未完成的录制任务
- 支持导入本地视频参与切片

### 切片管理
- 自动检测高能片段并生成切片，自动切片与自动语音转文字共用同一个任务队列
- 按 CPU 占用情况决定是否启动自动任务，避免影响正在进行的录制
- 支持复选、全选与一键删除（含导出文件）

### 切片导出
- 原版切片列表支持逐条删除，删除时同步清理切片、同名字幕、横竖封面与队列任务
- 语音转文字（faster-whisper），转写框默认收起，可展开查看完整文本
- 自动封面：均匀截取 4 帧、随机商用字库与随机颜色渲染多行标题，
  同时输出 16:9 横版和 3:4 竖版 PNG，支持「换一换」

### 依赖与设置
- 依赖管理页检测 FFmpeg、Python 等运行环境并提供安装入口
- 设置录播/切片的统一根目录等参数

## 系统要求

- Windows 10 1809（17763）或更高版本，x64
- 运行发布包无需预装 .NET 或 Windows App SDK（发布包为 self-contained）
- 录制、切片与封面需要 FFmpeg
- 语音转文字需要 Python 3 与 `faster-whisper`（依赖管理页可辅助安装）

## 下载与使用

1. 在 [Releases](../../releases) 页面下载 `LiveStreamClipTool-win-x64-*.zip`
2. 解压到任意目录，运行 `LiveStreamClipTool.exe`
3. 首次启动后到「设置」选择录播/切片根目录，到「依赖管理」确认 FFmpeg 等依赖

## 从源码构建

需要 .NET 8 SDK 与 Windows 10 SDK（10.0.19041 及以上）。

```powershell
dotnet build src\LiveStreamClipTool\LiveStreamClipTool.csproj -c Debug -p:Platform=x64
```

生成的可执行文件位于：

```
src\LiveStreamClipTool\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\LiveStreamClipTool.exe
```

发布自包含版本：

```powershell
dotnet publish src\LiveStreamClipTool\LiveStreamClipTool.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish
```

仓库内置 GitHub Actions：发布 Release 时自动构建、打包 zip 并上传到该 Release。

## 目录结构

```
src/LiveStreamClipTool/
  Assets/Branding/      品牌图标（cnoctave.jpg、favicon.ico、app.ico）
  Assets/Fonts/         封面使用的开源字体
  Data/                 SQLite 仓储层
  Dialogs/              弹窗
  Models/               数据模型
  Services/             录制、切片、B 站接口、依赖、封面等服务
  Tools/                调用的 Python 脚本
  Views/                WinUI 页面
```

## 说明

- 本项目只访问 B 站公开的匿名接口，不代表 B 站官方立场
- 请遵守 B 站用户协议与相关法律法规，仅录制/剪辑你有权处理的内容
- 录播与切片文件默认保存在本机，不会上传到任何服务器

## 许可证

Copyright (c) 2026 cnoctave.top

本项目基于 [GNU General Public License v3.0 or later](LICENSE) 发布。
