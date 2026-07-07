# IpShow 便捷功能增强 — 设计文档

日期：2026-07-07
项目：IpShow（WPF / .NET 8 桌面小工具，176×42，显示 归属地 / CPU / ↓下载 / ↑上传）

## 背景与目标

当前小工具的归属地文字是一个 `Hyperlink`，**左键单击会直接打开 Google 地图搜索**，同时同一处又绑定了单击刷新，极易误触。用户希望：单击不再跳转，把"查询"入口收进右键菜单，并新增几项便捷功能。

本次范围锁定为 4 件事，其余不做：

1. 单击不跳转 + 双击刷新 + 查询入口进右键菜单
2. 内存占用显示（内联在 CPU 旁）
3. 公网 IP 变化提醒（最简化）
4. 右键菜单显示网关地址

改动全部集中在 `IpShow/MainWindow.xaml` 与 `IpShow/MainWindow.xaml.cs`，不引入新依赖。

---

## 功能 1：单击不跳转 + 双击刷新 + 查询入口进菜单

### 现状
- `MainWindow.xaml:121` 归属地是 `Hyperlink`，`AddressLink_Click` 打开 Google 地图。
- 同处 `CurrentIpText_MouseLeftButtonDown`（`.cs:886`）单击触发一次刷新。
- `FetchIpDataAsync` 拿到公网 IP 后只用了归属地，IP 本身被丢弃。

### 改动
- **去掉 `Hyperlink`**：归属地改为普通 `Run`/`TextBlock`，绑定 `CurrentLocationText`，前景色沿用 `StatusTextBrush`。单击不再有任何导航行为。
- **删除 `CurrentIpText_MouseLeftButtonDown`** 及其 XAML 绑定。
- **双击 = 强制刷新**：在 `Window_MouseLeftButtonDown` 开头判断 `e.ClickCount == 2`，若是则 `await RefreshWithRetriesAsync(2, 1s)` 并 `return`（不进入 `DragMove`）。单击仍走原有 `DragMove` 拖动逻辑。
- **保存公网 IP**：新增字段 `private string _currentPublicIp = string.Empty;`。在 `RefreshAsync` 拿到 `data.Ip` 后赋值（供查询菜单与 IP 变化提醒使用）。
- **右键菜单新增两项查询入口**，位置在 `DnsMenu` / `GatewayMenu` 之后、Ping 组之前，中间用 `Separator` 分隔：
  - `MenuItem` "IP 查询网站" → 打开 `https://ipinfo.io/{_currentPublicIp}`（若 IP 为空则打开 `https://ipinfo.io`）。
  - `MenuItem` "地图查询归属地" → 沿用现有 `AddressLink_Click` 的 Google 地图逻辑，改成从菜单触发；归属地为空或 "-" 时不动作。
- 两个查询入口均用 `Process.Start(new ProcessStartInfo(url){ UseShellExecute = true })`。

### 验收
- 单击归属地文字：不跳转、不刷新，可拖动窗口（Free 模式下）。
- 双击小窗任意处：触发一次强制刷新。
- 右键菜单出现"IP 查询网站""地图查询归属地"，点击分别打开对应网页。

---

## 功能 2：内存占用显示（内联在 CPU 旁）

### 改动
- 新增 P/Invoke：`GlobalMemoryStatusEx` + `MEMORYSTATUSEX` 结构，读取 `dwMemoryLoad`（0–100 已用内存百分比）。
- 新增可绑定属性 `RamUsageText`（如 `RAM 60%`），在现有 1 秒定时器回调 `UpdateSystemStats()` 里更新（新增 `UpdateRamUsage()`）。读取失败显示 `RAM --`。
- **显示位置**：小窗第一行 CPU 文本右侧再加一个 `TextBlock`，绑定 `RamUsageText`，字号/字体与 CPU 一致（FontSize 11，Cascadia Mono），`Margin` 留出小间距。
- 宽度处理：窗口宽度保持 176。若一行放不下，将归属地 `TextBlock` 的 `MaxWidth` 从 92 适当下调（如 76）以让出空间；以实际渲染为准微调。
- 托盘 Tooltip 文本 `UpdateTrayText()` 一并加入 RAM（在 63 字符上限内，超出仍走现有 `TrimTrayText`）。

### 验收
- 小窗第一行显示 `<归属地>  CPU xx%  RAM yy%`，数值每秒刷新且不溢出窗口。

---

## 功能 3：公网 IP 变化提醒（最简化）

### 改动
- 复用功能 1 的 `_currentPublicIp`。
- 在 `RefreshAsync` 成功拿到新 IP 时：
  - 若 `_currentPublicIp` **非空**且与新 IP **不同** → 调 `_trayIcon.ShowBalloonTip(3000, "IpShow", $"公网 IP 变化：{old} → {new}", ToolTipIcon.Info)`。
  - 然后再更新 `_currentPublicIp = new`。
  - 首次获取（旧值为空）不提醒。
- 无新增设置项、无新增 UI，零配置。

### 验收
- 首次启动获取 IP：不弹提醒。
- 切换网络/VPN 导致公网 IP 变化：弹出一次托盘气泡显示新旧 IP。
- 断网→恢复但 IP 未变：不提醒。

---

## 功能 4：网关地址进菜单

### 改动
- 新增 `UpdateGateway()`：遍历 `NetworkInterface.GetAllNetworkInterfaces()`，取 `OperationalStatus.Up` 且非 Loopback 接口的 `GetIPProperties().GatewayAddresses`，选第一个 IPv4（`AddressFamily.InterNetwork`），显示为 `Gateway x.x.x.x`；无则 `Gateway —`。
- 在 `ContextMenu_Opened` 里像现有 `UpdateDns()` 一样调用 `UpdateGateway()`（菜单打开时刷新）。
- XAML 在 `DnsMenu` 下方新增 `MenuItem Name="GatewayMenu"`，初始 Header `Gateway —`。

### 验收
- 右键打开菜单：`Gateway` 一行显示当前默认网关 IPv4；无网关时显示占位符。

---

## 影响范围与非目标

- **仅改** `IpShow/MainWindow.xaml`、`IpShow/MainWindow.xaml.cs`。
- **不引入**新 NuGet 依赖。
- **非目标**（本次明确不做）：内网 IP、一键复制、悬停 Tooltip 明细、自定义 Ping 目标、流量统计、网速曲线、多语言、不透明度/字号调节、单位切换等。

## 风险

- 一行内联 CPU + RAM 在 176px 宽度下可能偏挤 —— 通过下调归属地 `MaxWidth` 与实际渲染微调解决，不改窗口尺寸。
- `GlobalMemoryStatusEx` 为系统整体内存负载（非本进程），符合"内存占用"直觉。
