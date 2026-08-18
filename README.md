---
AIGC:
  ContentProducer: '001191110102MAD55U9H0F10002'
  ContentPropagator: '001191110102MAD55U9H0F10002'
  Label: '1'
  ProduceID: '6886dae4-7f96-4adb-b7ec-16ebc8558abb'
  PropagateID: '6886dae4-7f96-4adb-b7ec-16ebc8558abb'
  ReservedCode1: '890ff7f6-ee92-4307-b1e0-325633b8be02'
  ReservedCode2: '890ff7f6-ee92-4307-b1e0-325633b8be02'
---

# PULSE / 脉搏

Windows 11 桌面系统监控小组件，赛博朋克风格，零配置开箱即用。

## 截图

> 纯 WPF 实现，无 WebView2，内存占用 ~170MB

## 功能

| 监控项 | 说明 |
|--------|------|
| CPU | 处理器使用率 |
| RAM | 内存使用量 / 总量 |
| GPU | GPU 3D 使用率 |
| NET | 网络上传 / 下载速率 |
| DELAY | PING 延迟（目标可配置，默认 223.5.5.5） |
| RSSI | WiFi 信号强度（dBm） |
| BAT | 电池电量（台式机自动隐藏） |
| IP / DNS | 本机 IP 地址 / DNS 服务器 |
| PROC | 进程数量 |
| TEMP | CPU / GPU 温度 |

## 特性

- **赛博朋克 UI** — 扫描线动画、分段进度条、霓虹配色、等宽字体
- **置顶 / 锁定** — 一键钉在桌面，拖动锁定防误触
- **简洁模式** — 隐藏数值，标签与进度条同行，窗口自动收缩
- **透明度调节** — 仅调节背景透明度，文字始终清晰
- **监控项可配置** — 在设置面板勾选需要显示的项目，CPU 为基础项不可关闭
- **系统托盘** — 右键菜单：显示/隐藏、简洁模式、开机自启、退出
- **开机自启** — 注册表方式，无需管理员权限
- **全部异步采集** — PING / WMI / netsh / LibreHardwareMonitor 均不阻塞 UI 线程

## 技术栈

- C# WPF (.NET 9, `net9.0-windows`)
- 纯 WPF，无 WebView2
- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — CPU/GPU 温度传感器
- `System.Management` — WMI 查询（Uptime、WiFi RSSI）

## 构建与运行

```bash
# 开发调试
dotnet build src/DesktopWidgets/DesktopWidgets.csproj
dotnet run --project src/DesktopWidgets/DesktopWidgets.csproj

# 发布自包含单文件 exe（无需安装 .NET）
dotnet publish src/DesktopWidgets/DesktopWidgets.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish/
```

## 版本历史

| 版本 | 主要变更 |
|------|----------|
| v0.14 | DELAY 行新增丢包率显示（滑动窗口，`23ms L0%`） |
| v0.13 | IP 移到底部栏、亮度提升、RSSI 标签、设置双列布局、托盘中文化、全异步化、图标重绘 |
| v0.12 | 设置面板（监控可见性 / DELAY IP 配置 / 跟随锁定） |
| v0.11 | 品牌重命名 DesktopWidgets → PULSE |
| v0.10 | WiFi 信号 dBm、Uptime、锁定位置、异步 PING、虚拟网卡过滤、简洁模式 |
| v0.8-0.9 | PING 延迟、IP/DNS、ScrollViewer |
| v0.7 | 置顶切换、透明度滑块（背景 alpha）、退出按钮 |
| v0.5-0.6 | 视觉打磨、UI 重构、温度 N/A 处理 |
| v0.4 | TEMP (LibreHardwareMonitor)、PROC、慢指标优化 |
| v0.3 | 开机自启（注册表） |
| v0.2 | WebView2 移除（541MB → 227MB） |

## 许可证

[MIT](LICENSE)

> AI生成