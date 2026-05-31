# IpShow

IpShow 是一个轻量的 Windows 桌面网络状态小窗，用来长期显示公网位置、实时上传/下载速度和 CPU 占用。它可以悬浮在桌面、置顶/置底，也可以只保留在右下角托盘里运行。

IpShow is a tiny Windows desktop network status overlay. It stays out of the way while showing the information you usually want at a glance: location, live upload/download speed, and CPU usage.

![IpShow logo](IpShow/Assets/IpShowLogo.png)

## 功能特性

- Windows WPF 极简悬浮窗
- 本地网卡实时上传/下载速度
- CPU 占用显示，无需额外运行时依赖
- 公网 IP 位置显示，支持可选本地 GeoLite2 数据库
- DNS 信息显示在右键菜单顶部
- 支持只显示在 Windows 右下角托盘
- 桌面小窗 / 托盘模式切换
- 窗口置顶、普通、置底图层模式
- 默认可自由拖动，也提供停靠位置预设
- 开机自启动开关
- Balanced / Light / Dark 三种文字配色
- 适配浅色和深色桌面的半透明圆角背景

## 界面预览

界面故意保持小而安静，适合全天放在桌面角落：

```text
Osaka    CPU 2%
↓ 1.1K/s ↑ 740B/s
```

## 环境要求

- Windows 10/11
- .NET 8 SDK，用于从源码构建

## 构建运行

```powershell
dotnet build
```

运行 Debug 构建：

```powershell
.\IpShow\bin\Debug\net8.0-windows\IpShow.exe
```

## GeoIP 数据库

IpShow 可以读取本地 MaxMind GeoLite2 City 数据库：

```text
IpShow/GeoIP/GeoLite2-City.mmdb
```

这个数据库是可选的。缺少该文件时，IpShow 仍然可以构建和运行，并会尽量使用远程查询结果里的位置信息。

GeoLite2 数据库由 MaxMind 提供，并受 MaxMind 自身许可约束。本仓库不需要提交 `.mmdb` 文件；如果你需要本地位置查询，可以自行从 MaxMind 下载并放到上面的目录。

## 隐私说明

IpShow 会读取本机网卡字节计数和系统 CPU 时间来显示实时状态。为了查询公网 IP 和位置，程序可能访问 `api.ipify.org`、`ipwho.is`、`ipinfo.io` 等外部 IP 服务。

## 开源许可

本项目使用 [MIT License](LICENSE) 开源。
