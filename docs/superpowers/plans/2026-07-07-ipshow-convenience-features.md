# IpShow 便捷功能增强 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 IpShow 桌面小工具加 4 项便捷功能：单击不跳转+双击刷新+查询入口进右键菜单、内存占用内联显示、公网 IP 变化托盘提醒、右键菜单显示网关。

**Architecture:** 纯 WPF 增量改动，全部集中在 `IpShow/MainWindow.xaml`（视图/菜单）与 `IpShow/MainWindow.xaml.cs`（逻辑/P-Invoke）。新增一个字段 `_currentPublicIp` 作为查询菜单与 IP 变化提醒的共同数据源；内存用 `GlobalMemoryStatusEx` P/Invoke 读取；网关与 DNS 一样在菜单打开时刷新。不引入新依赖、不新增设置项。

**Tech Stack:** .NET 8 (net8.0-windows)、WPF、System.Windows.Forms.NotifyIcon、Win32 P/Invoke（kernel32）。

## Global Constraints

- 仅修改 `IpShow/MainWindow.xaml` 和 `IpShow/MainWindow.xaml.cs`，不新增文件、不加 NuGet 依赖。
- 窗口尺寸保持 176×42（`MaxWidth/MinWidth=176`，`MaxHeight/MinHeight=42`）不变。
- 本仓库**无自动化测试工程**；每个任务的验证 = `dotnet build IpShow.sln -c Debug` 成功 + 指定的手动冒烟检查。
- 构建命令统一为：`dotnet build IpShow.sln -c Debug`（期望结尾出现 `Build succeeded`，`0 Error(s)`）。
- 所有外部打开链接统一走 `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`。
- 托盘气泡/文本沿用现有 `Forms` 别名（`using Forms = System.Windows.Forms;`）。

---

### Task 1: 保存公网 IP + 公网 IP 变化托盘提醒

**Files:**
- Modify: `IpShow/MainWindow.xaml.cs`（新增字段、在 `RefreshAsync` 中调用、新增 `NotifyIfPublicIpChanged`）

**Interfaces:**
- Produces: `private string _currentPublicIp`（存最近一次成功获取的公网 IP，供 Task 3 查询菜单使用）；`private void NotifyIfPublicIpChanged(string newIp)`。
- Consumes: 现有 `_trayIcon`（`Forms.NotifyIcon`）、`RefreshAsync`、`IpLookupResult data`。

- [ ] **Step 1: 新增字段**

在字段区（`_cpuUsageText` 声明附近，`MainWindow.xaml.cs` 第 79 行下方）加入：

```csharp
    private string _cpuUsageText = "CPU --";
    private string _currentPublicIp = string.Empty;
```

- [ ] **Step 2: 在 RefreshAsync 中接入提醒并保存 IP**

把 `RefreshAsync` 中处理成功分支（当前第 289–295 行）：

```csharp
            var data = await FetchIpDataAsync();
            if (data.Ip is { Length: > 0 } ip)
            {
                var (location, countryCode) = ResolveLocation(ip, data.Region, data.CountryCode);
                CurrentLocationText = location;
                ApplyLocationBrush(countryCode, isConnected: true);
                UpdateTrayText();
            }
```

改为：

```csharp
            var data = await FetchIpDataAsync();
            if (data.Ip is { Length: > 0 } ip)
            {
                NotifyIfPublicIpChanged(ip);
                _currentPublicIp = ip;
                var (location, countryCode) = ResolveLocation(ip, data.Region, data.CountryCode);
                CurrentLocationText = location;
                ApplyLocationBrush(countryCode, isConnected: true);
                UpdateTrayText();
            }
```

- [ ] **Step 3: 新增 NotifyIfPublicIpChanged 方法**

在 `HandleDisconnected` 方法（当前第 710 行附近）之前或之后新增：

```csharp
    private void NotifyIfPublicIpChanged(string newIp)
    {
        // 首次获取（旧值为空）不提醒；仅当已有旧值且发生变化时弹一次托盘气泡。
        if (string.IsNullOrEmpty(_currentPublicIp) || _currentPublicIp == newIp)
        {
            return;
        }

        try
        {
            _trayIcon.ShowBalloonTip(
                3000,
                "IpShow",
                $"公网 IP 变化：{_currentPublicIp} → {newIp}",
                Forms.ToolTipIcon.Info);
        }
        catch
        {
        }
    }
```

- [ ] **Step 4: 构建验证**

Run: `dotnet build IpShow.sln -c Debug`
Expected: `Build succeeded`，`0 Error(s)`。

- [ ] **Step 5: 手动冒烟（可选快速验证）**

运行程序确认正常启动、右下角托盘图标在。切换网络/连断 VPN 使公网 IP 变化时，应弹出一次“公网 IP 变化：旧 → 新”气泡；首次启动不应弹。（若当前无法切换网络，仅需确认 Step 4 构建通过即可。）

- [ ] **Step 6: Commit**

```bash
git add IpShow/MainWindow.xaml.cs
git commit -m "feat: 保存公网 IP 并在变化时弹托盘提醒"
```

---

### Task 2: 单击不跳转 + 双击强制刷新

**Files:**
- Modify: `IpShow/MainWindow.xaml`（归属地 TextBlock 去掉 Hyperlink 与单击刷新绑定）
- Modify: `IpShow/MainWindow.xaml.cs`（`Window_MouseLeftButtonDown` 加双击刷新，删除 `CurrentIpText_MouseLeftButtonDown`）

**Interfaces:**
- Consumes: 现有 `RefreshWithRetriesAsync(int, TimeSpan)`、`_dockMode`、`DragMove()`。
- Produces: 归属地不再有导航行为；双击窗口触发刷新。

- [ ] **Step 1: XAML 去掉 Hyperlink**

把 `MainWindow.xaml` 第 114–127 行的归属地 TextBlock：

```xml
          <TextBlock FontFamily="Bahnschrift"
                     FontSize="14"
                     MaxWidth="92"
                     Effect="{StaticResource TextGlow}"
                     TextTrimming="CharacterEllipsis"
                     Cursor="Hand"
                     MouseLeftButtonDown="CurrentIpText_MouseLeftButtonDown">
            <Hyperlink Click="AddressLink_Click"
                       Tag="{Binding CurrentLocationText, Mode=OneWay}"
                       Foreground="{Binding StatusTextBrush, Mode=OneWay}"
                       TextDecorations="{x:Null}">
              <Run Text="{Binding CurrentLocationText, Mode=OneWay}" />
            </Hyperlink>
          </TextBlock>
```

替换为普通文字（去掉 Hyperlink、Cursor、单击绑定）：

```xml
          <TextBlock FontFamily="Bahnschrift"
                     FontSize="14"
                     MaxWidth="92"
                     Foreground="{Binding StatusTextBrush, Mode=OneWay}"
                     Effect="{StaticResource TextGlow}"
                     TextTrimming="CharacterEllipsis"
                     Text="{Binding CurrentLocationText, Mode=OneWay}" />
```

- [ ] **Step 2: `.cs` 改造 Window_MouseLeftButtonDown 支持双击刷新**

把 `MainWindow.xaml.cs` 第 764–778 行：

```csharp
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dockMode != DockMode.Free)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }
```

替换为：

```csharp
    private async void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击任意处 = 强制刷新（替代旧的单击刷新，避免误触）。
        if (e.ClickCount == 2)
        {
            await RefreshWithRetriesAsync(2, TimeSpan.FromSeconds(1));
            return;
        }

        if (_dockMode != DockMode.Free)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }
```

- [ ] **Step 3: 删除已弃用的单击刷新方法**

删除 `MainWindow.xaml.cs` 第 886–887 行：

```csharp
    private async void CurrentIpText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => await RefreshWithRetriesAsync(2, TimeSpan.FromSeconds(1));
```

> 注：`AddressLink_Click` 方法此时暂时无引用，保留不动，将在 Task 3 移除/替换。未引用的私有方法不影响构建。

- [ ] **Step 4: 构建验证**

Run: `dotnet build IpShow.sln -c Debug`
Expected: `Build succeeded`，`0 Error(s)`。（若报 `CurrentIpText_MouseLeftButtonDown` 或 `Hyperlink` 相关错误，说明 XAML/`.cs` 未同步删除，返回 Step 1–3 核对。）

- [ ] **Step 5: 手动冒烟**

运行程序：单击归属地文字——不打开浏览器、不刷新，Free 模式下可拖动窗口；双击小窗任意处——触发一次刷新（归属地/网速会重新拉取）。

- [ ] **Step 6: Commit**

```bash
git add IpShow/MainWindow.xaml IpShow/MainWindow.xaml.cs
git commit -m "feat: 归属地单击不再跳转，改为双击强制刷新"
```

---

### Task 3: 查询入口进右键菜单（IP 查询网站 + 地图查询）

**Files:**
- Modify: `IpShow/MainWindow.xaml`（右键菜单新增两项 + 分隔符）
- Modify: `IpShow/MainWindow.xaml.cs`（新增两个 Click 处理与 `OpenUrl`，删除旧 `AddressLink_Click`）

**Interfaces:**
- Consumes: Task 1 的 `_currentPublicIp`；现有 `CurrentLocationText`、`PlaceholderLocation`。
- Produces: `IpLookupMenu_Click`、`MapLookupMenu_Click`、`private static void OpenUrl(string url)`。

- [ ] **Step 1: XAML 新增菜单项**

在 `MainWindow.xaml` 第 32–33 行的 `DnsMenu` 与其后的 `<Separator />` 之间/之后插入。将：

```xml
      <MenuItem Header="DNS —" Name="DnsMenu" />
      <Separator />
      <MenuItem Header="Google: -" Name="PingGoogleMenu" />
```

改为：

```xml
      <MenuItem Header="DNS —" Name="DnsMenu" />
      <Separator />
      <MenuItem Header="IP 查询网站" Name="IpLookupMenu" Click="IpLookupMenu_Click" />
      <MenuItem Header="地图查询归属地" Name="MapLookupMenu" Click="MapLookupMenu_Click" />
      <Separator />
      <MenuItem Header="Google: -" Name="PingGoogleMenu" />
```

- [ ] **Step 2: `.cs` 删除旧 AddressLink_Click**

删除 `MainWindow.xaml.cs` 第 747–762 行整个方法：

```csharp
    private void AddressLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink hyperlink)
        {
            return;
        }

        var location = hyperlink.Tag as string;
        if (string.IsNullOrWhiteSpace(location) || location == "-")
        {
            return;
        }

        var url = $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(location)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
```

- [ ] **Step 3: `.cs` 新增两个菜单处理与 OpenUrl**

在删除处（`Window_MouseLeftButtonDown` 前）新增：

```csharp
    private void IpLookupMenu_Click(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(_currentPublicIp)
            ? "https://ipinfo.io"
            : $"https://ipinfo.io/{_currentPublicIp}";
        OpenUrl(url);
    }

    private void MapLookupMenu_Click(object sender, RoutedEventArgs e)
    {
        var location = CurrentLocationText;
        if (string.IsNullOrWhiteSpace(location) || location == "-" || location == PlaceholderLocation)
        {
            return;
        }

        OpenUrl($"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(location)}");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }
```

- [ ] **Step 4: 构建验证**

Run: `dotnet build IpShow.sln -c Debug`
Expected: `Build succeeded`，`0 Error(s)`。

- [ ] **Step 5: 手动冒烟**

右键小窗：菜单里出现“IP 查询网站”“地图查询归属地”。点“IP 查询网站”打开 `ipinfo.io/<你的公网IP>`；点“地图查询归属地”在 Google 地图搜索当前归属地（归属地为占位符 `—` 时不动作）。

- [ ] **Step 6: Commit**

```bash
git add IpShow/MainWindow.xaml IpShow/MainWindow.xaml.cs
git commit -m "feat: 右键菜单新增 IP 查询网站与地图查询入口"
```

---

### Task 4: 内存占用内联显示

**Files:**
- Modify: `IpShow/MainWindow.xaml.cs`（`GlobalMemoryStatusEx` P/Invoke、`RamUsageText` 属性、`UpdateRamUsage`、接入定时器与托盘文本）
- Modify: `IpShow/MainWindow.xaml`（CPU 右侧新增 RAM TextBlock；归属地 MaxWidth 92→76）

**Interfaces:**
- Produces: `public string RamUsageText`（形如 `RAM 60%` / `RAM --`）；`private void UpdateRamUsage()`。
- Consumes: 现有 `UpdateSystemStats()`、`UpdateTrayText()`、`OnPropertyChanged`、`Marshal`。

- [ ] **Step 1: 新增 RamUsageText 属性**

在 `CpuUsageText` 属性（`MainWindow.xaml.cs` 第 172–181 行）之后新增：

```csharp
    public string RamUsageText
    {
        get => _ramUsageText;
        private set
        {
            if (_ramUsageText == value) return;
            _ramUsageText = value;
            OnPropertyChanged(nameof(RamUsageText));
        }
    }
```

并在字段区（`_currentPublicIp` 下方）新增后备字段：

```csharp
    private string _ramUsageText = "RAM --";
```

- [ ] **Step 2: 新增 P/Invoke 与结构体**

在文件底部 `GetSystemTimes` 声明（第 1456–1457 行）附近新增：

```csharp
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
```

- [ ] **Step 3: 新增 UpdateRamUsage 并接入定时器**

在 `UpdateCpuUsage` 方法之后新增：

```csharp
    private void UpdateRamUsage()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            RamUsageText = GlobalMemoryStatusEx(ref status) ? $"RAM {status.dwMemoryLoad}%" : "RAM --";
        }
        catch
        {
            RamUsageText = "RAM --";
        }
    }
```

把 `UpdateSystemStats`（第 484–488 行）：

```csharp
    private void UpdateSystemStats()
    {
        UpdateNetworkSpeed();
        UpdateCpuUsage();
    }
```

改为：

```csharp
    private void UpdateSystemStats()
    {
        UpdateNetworkSpeed();
        UpdateCpuUsage();
        UpdateRamUsage();
    }
```

- [ ] **Step 4: 托盘文本加入 RAM**

把 `UpdateTrayText`（第 1323–1324 行）：

```csharp
        var speed = $"↓ {DownloadSpeedValueText}{DownloadSpeedUnitText} ↑ {UploadSpeedValueText}{UploadSpeedUnitText}";
        _trayIcon.Text = TrimTrayText($"IpShow {CurrentLocationText} {CpuUsageText} {speed}");
```

改为：

```csharp
        var speed = $"↓ {DownloadSpeedValueText}{DownloadSpeedUnitText} ↑ {UploadSpeedValueText}{UploadSpeedUnitText}";
        _trayIcon.Text = TrimTrayText($"IpShow {CurrentLocationText} {CpuUsageText} {RamUsageText} {speed}");
```

- [ ] **Step 5: XAML 加 RAM TextBlock 并收窄归属地**

在 `MainWindow.xaml` 第一行 StackPanel 内、CPU TextBlock（第 128–134 行）之后新增 RAM 文本：

```xml
          <TextBlock Foreground="{Binding StatusTextBrush, Mode=OneWay}"
                     FontFamily="Cascadia Mono, Consolas, Bahnschrift"
                     FontSize="11"
                     Margin="12,1,0,0"
                     Effect="{StaticResource TextGlow}"
                     TextTrimming="CharacterEllipsis"
                     Text="{Binding CpuUsageText, Mode=OneWay}" />
          <TextBlock Foreground="{Binding StatusTextBrush, Mode=OneWay}"
                     FontFamily="Cascadia Mono, Consolas, Bahnschrift"
                     FontSize="11"
                     Margin="8,1,0,0"
                     Effect="{StaticResource TextGlow}"
                     TextTrimming="CharacterEllipsis"
                     Text="{Binding RamUsageText, Mode=OneWay}" />
```

同时把归属地 TextBlock 的 `MaxWidth="92"`（Task 2 后仍为 92）改为 `MaxWidth="76"`，给 RAM 让出空间。

- [ ] **Step 6: 构建验证**

Run: `dotnet build IpShow.sln -c Debug`
Expected: `Build succeeded`，`0 Error(s)`。

- [ ] **Step 7: 手动冒烟**

运行程序：第一行呈现 `<归属地>  CPU xx%  RAM yy%`，RAM 每秒刷新且数值合理（与任务管理器“内存”百分比接近）；文字不溢出 176px 窗口、不换行。若偏挤，可再微调 RAM `Margin` 或归属地 `MaxWidth`。

- [ ] **Step 8: Commit**

```bash
git add IpShow/MainWindow.xaml IpShow/MainWindow.xaml.cs
git commit -m "feat: 内联显示系统内存占用百分比"
```

---

### Task 5: 网关地址进右键菜单

**Files:**
- Modify: `IpShow/MainWindow.xaml`（`DnsMenu` 下方新增 `GatewayMenu`）
- Modify: `IpShow/MainWindow.xaml.cs`（新增 `UpdateGateway()`，在 `ContextMenu_Opened` 调用）

**Interfaces:**
- Produces: `private void UpdateGateway()`；XAML `GatewayMenu`。
- Consumes: 现有 `ContextMenu_Opened`、`NetworkInterface`、`System.Linq`。

- [ ] **Step 1: XAML 新增 Gateway 菜单项**

在 `MainWindow.xaml` 的 `DnsMenu`（第 32 行）下一行新增：

```xml
      <MenuItem Header="DNS —" Name="DnsMenu" />
      <MenuItem Header="Gateway —" Name="GatewayMenu" />
```

- [ ] **Step 2: `.cs` 新增 UpdateGateway**

在 `UpdateDns` 方法（第 675 行附近）之后新增：

```csharp
    private void UpdateGateway()
    {
        try
        {
            var gateway = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                              && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().GatewayAddresses)
                .Select(g => g?.Address)
                .FirstOrDefault(addr => addr is { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork });

            GatewayMenu.Header = gateway is null ? "Gateway —" : $"Gateway {gateway}";
        }
        catch
        {
            GatewayMenu.Header = "Gateway —";
        }
    }
```

- [ ] **Step 3: 在 ContextMenu_Opened 调用**

把 `ContextMenu_Opened`（第 843–845 行）开头：

```csharp
    private async void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        try { UpdateDns(); } catch { }
```

改为：

```csharp
    private async void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        try { UpdateDns(); } catch { }
        try { UpdateGateway(); } catch { }
```

- [ ] **Step 4: 构建验证**

Run: `dotnet build IpShow.sln -c Debug`
Expected: `Build succeeded`，`0 Error(s)`。

- [ ] **Step 5: 手动冒烟**

右键小窗：`DNS` 下方出现 `Gateway`，显示当前默认网关 IPv4（如 `Gateway 192.168.1.1`）；断网时显示 `Gateway —`。

- [ ] **Step 6: Commit**

```bash
git add IpShow/MainWindow.xaml IpShow/MainWindow.xaml.cs
git commit -m "feat: 右键菜单显示默认网关地址"
```

---

## Self-Review

**Spec coverage：**
- 功能 1（单击不跳转 + 双击刷新 + 查询进菜单）→ Task 2 + Task 3；公网 IP 存储 → Task 1。✅
- 功能 2（内存内联）→ Task 4。✅
- 功能 3（IP 变化提醒）→ Task 1。✅
- 功能 4（网关进菜单）→ Task 5。✅
- 非目标项均未安排任务。✅

**Placeholder scan：** 无 TBD/TODO；每个代码步骤均给出完整代码与确切命令。✅

**Type consistency：** `_currentPublicIp`（Task 1 定义，Task 3 使用）、`RamUsageText`/`UpdateRamUsage`（Task 4 内部一致）、`UpdateGateway`/`GatewayMenu`（Task 5 内部一致）、`OpenUrl`（Task 3 内部一致）名称前后统一。✅

**任务顺序依赖：** Task 3 依赖 Task 1 的 `_currentPublicIp`；Task 4 依赖 Task 1 字段区已存在（`_ramUsageText` 加在 `_currentPublicIp` 下方）。按 1→2→3→4→5 顺序执行无冲突。✅
