# EndfieldCharge · 终末地风格电量 HUD

插上 / 拔掉充电器时，从屏幕顶部弹出一块"灵动岛"式 HUD，显示当前电量（mWh 与百分比）。
视觉与动画风格复刻《终末地》工业 / 超充模式 HUD。

支持 **Windows 10 1809+ / Windows 11** 与 **macOS 13+（Apple Silicon / Intel）**

- **插电**：完整三态动画 —— 电标弹出 → 胶囊撑高成圆角矩形显示「超充模式」→ 收成圆胶囊显示电量 → 停留 → 整体缩小收回
- **拔电**：简化动画 —— 只弹电量圆胶囊，内容在胶囊完全出来后快速显现 → 停留 → 收回

## 下载安装

从 [Releases](https://github.com/Lenkmat/endfield-charge/releases) 下载：

| 文件 | 平台 | 说明 |
|------|------|------|
| `EndfieldCharge-x.y.z-setup.exe` | Windows | Inno Setup 安装版（中文/英文向导，可选桌面快捷方式与开机自启） |
| `EndfieldCharge-x.y.z-portable.zip` | Windows | 便携版，解压即用 |
| `EndfieldCharge-x.y.z-osx-arm64.dmg` | macOS | Apple Silicon，拖入「应用程序」即可 |

### macOS 首次打开

当前发布未做 Apple 公证，首次打开会被 Gatekeeper 拦截（提示"无法验证开发者"）。
任选一种方式绕过：

```bash
# 方式 A：命令行去隔离（推荐，一次即可）
xattr -dr com.apple.quarantine /Applications/EndfieldCharge.app
```

方式 B：在「访达」中**右键**点击 App → 选择「打开」→ 弹窗中再次点「打开」。
方式 C：系统设置 → 隐私与安全性 → 底部「仍要打开」。

macOS 上应用为**菜单栏常驻**（不占 Dock），列表见 [macOS 与 Windows 的差异](#macos-与-windows-的差异)。

## 功能

| 功能 | 说明（Win = Windows，Mac = macOS） |
|------|------|
| 电量显示 | 剩余 / 满充容量（mWh）与百分比。Win：`CallNtPowerInformation`，WMI 兜底；Mac：IORegistry `AppleSmartBattery`（mAh × mV 精确换算，并支持健康度），`pmset` 兜底 |
| 电源监听 | Win：`RegisterPowerSettingNotification` 订阅 GUID_ACDC_POWER_SOURCE，2s 轮询兜底；Mac：`IOPSNotificationCreateRunLoopSource` 通知 + 5s 轮询兜底 —— 两端均为 400ms 双向去抖 |
| 低电量变色 | 电量 < 20% 时黄绿电量圈变红（#FF4D4F） |
| 提醒通知 | 低电量提醒（阈值可调 5–40%）与充满提醒（≥99%），卡牌风格弹窗，4s 自动消失 |
| 设置窗口 | 全局缩放（0.4–1.2）、显示时长（2–10s）、HUD 位置（顶部居中/靠右/靠左）、显示器选择、语言、开机自启，保存即生效并持久化 |
| 托盘菜单 | 四项：预览电量 HUD / 设置 / 检查更新 / 退出。Win：左键单击弹出自绘深色菜单；Mac：菜单栏图标左键弹出系统原生菜单（Windows 行为保留不变） |
| 动画微调 | 设置窗口「动画」页实时预览并微调时长 / 回弹 / 波纹参数，保存即生效并持久化 |
| 节能模式提示 | 开 / 关节能模式时弹出对应 HUD。Win：24H2+（build 26100+）订阅 GUID_ENERGY_SAVER_STATUS、轮询注册表 EnergySaverState，旧系统用 GUID_POWER_SAVING_STATUS + SystemStatusFlag；Mac：`NSProcessInfo.isLowPowerModeEnabled`，`pmset` 的 `powermode` 兜底，文案为「低电量模式」。设置「通知」页可开关 |
| 检查更新 | 读取 GitHub Releases API，比较程序集版本，一键跳转下载页 |
| 多语言 | 中文 / 英文，默认跟随系统，可在设置中手动切换 |
| 开机自启 | 设置窗口「通用」页开关。Win：写 `HKCU\...\CurrentVersion\Run`（无需管理员）；Mac：写 `~/Library/LaunchAgents/com.lenkmat.endfieldcharge.plist` |
| 统一图标 | Win：托盘 / 各窗口 / exe / 安装器 / 卸载器统一使用 `Assets\tray_bolt`；Mac：菜单栏用 `Assets/tray_bolt.png` |
| 单实例 | 只允许一个实例常驻。Win：命名互斥体；Mac：`~/Library/Application Support/EndfieldCharge/.lock` 文件独占 |
| 自检诊断 | `--selftest` 输出电池 / 电源 / 低电量模式 / bundle / 自启状态 JSON 后退出，便于报障与 CI 冒烟 |
| 日志 | Win：`%TEMP%\EndfieldCharge\log-YYYYMMDD.txt`；Mac：`~/Library/Logs/EndfieldCharge/log-YYYYMMDD.txt` |

## 运行要求

| 平台 | 要求 |
|------|------|
| Windows | Windows 10 1809+ / Windows 11，x64；.NET 8 运行时（Release 为框架依赖单文件发布，需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)） |
| macOS | macOS 13+；Apple Silicon 或 Intel；**self-contained**（无需另装 .NET 运行时） |

## 构建

工程通过 `$(OS)` 条件选择目标框架，因此**同一份代码**在哪个平台构建就是哪个平台的产物：

| 构建主机 | TargetFramework | RID | 产物 |
|----------|-----------------|-----|------|
| Windows | `net8.0-windows` | `win-x64` | 单文件 exe（框架依赖，需 .NET 8 Desktop Runtime） |
| macOS | `net10.0` | `osx-arm64` / `osx-x64` | `.app` bundle（self-contained，无需运行时） |

### Windows

```bash
dotnet build -c Debug

# 发布（单文件 exe，输出到 publish/）
dotnet publish -c Release -o publish

# 本地打安装包（需安装 Inno Setup，iscc 在 PATH 中）
iscc installer\EndfieldCharge.iss
```

> 注意：`PublishSingleFile` 只把托管 dll 打进 exe，SkiaSharp 的 native dll
> （libSkiaSharp / libHarfBuzzSharp / av_libglesv2）仍需与 exe 同目录 ——
> 便携分发请打包整个 `publish/` 目录，不要只拷 exe。

### macOS

```bash
# 调试
dotnet build -c Debug

# 诊断（不需要 UI，最快确认 IOKit / AppKit 互操作可用）
dotnet bin/Debug/net10.0/EndfieldCharge.dll --selftest
# Release 构建的输出路径带 RID：bin/Release/net10.0/osx-arm64/EndfieldCharge.dll

# 打包 .app + .dmg（内含 --selftest 冒烟门禁；产物在 build/macos/dist/）
./scripts/package-macos.sh

# 可选环境变量
#   VERSION=1.2.3                     指定版本号（默认取 git tag）
#   RID=osx-x64                       Intel Mac
#   NO_DMG=1                          只出 .app 不出 dmg
#   SIGNING_IDENTITY="Developer ID Application: X (TEAMID)"   正式签名
#   NOTARY_PROFILE=AC_PASSWORD        公证（需先 notarytool store-credentials）
```

### CI / 发布（GitHub Actions）

推送到 `main` 会并行构建两个平台的产物（Actions 页面可下载 artifact）：

- **Windows** job（`windows-latest`）：Inno Setup 安装包 + 便携版 zip
- **macOS** job（`macos-latest`）：`.app` + `.dmg`（打包脚本内含 `--selftest` 冒烟门禁）

推送 `v*` 标签（如 `v1.0.0`）会额外创建 GitHub Release，把标签版本号同时写入
程序集版本、安装包文件名与 `CFBundleShortVersionString`（保证两平台的应用内
「检查更新」都能正确比较版本）：

```bash
git tag v1.0.0
git push origin v1.0.0
```

可选 Secrets：`MACOS_SIGNING_IDENTITY`、`MACOS_NOTARY_PROFILE`（缺失时自动降级为 ad-hoc 签名）。

## 调试参数

启动时追加参数，无需真的插拔电源：

| 参数 | 作用 |
|------|------|
| `--selftest` | 打印电池 / 电源 / 低电量模式 / bundle / 自启状态 JSON 后退出（不进 UI） |
| `--demo` | 用示例数据播放一次**完整**动画（插电） |
| `--preview` | 用本机真实电池数据播放一次完整动画 |
| `--preview-unplug` | 用示例数据播放一次**简化**动画（拔电） |
| `--debug-ring` | 静态呈现状态 C（电量态）1.5s |
| `--notch-birth` | 强制启用 macOS 刘海出生前导（无刘海外接屏也能预览该动画） |
| `--power-log` | 输出电源事件日志到 `%TEMP%\power-log.txt`（Windows，Debug 构建） |

> `--selftest` 优先级最高。其余参数互斥，按 `--demo` → `--preview-unplug` → `--preview` 的优先级生效。

macOS 上排查插拔电源问题：应用日志 `~/Library/Logs/EndfieldCharge/log-YYYYMMDD.txt` 会在每次
电源变化时输出 `AC connected/disconnected → 上报 HUD`

## 项目结构

```
EndfieldCharge/
├─ Program.cs               # 入口：单实例、--selftest、Avalonia 配置
├─ App.axaml.cs             # 应用主体：托盘装配、电源订阅、提醒、设置分发
├─ Platform/                # ★ 跨平台抽象层（本次 macOS 移植新增）
│  ├─ PlatformServices.cs   #   唯一运行时分发点（Windows / macOS）
│  ├─ IPowerMonitor.cs      #   电源/电池/省电模式监听接口
│  ├─ IPlatformServices.cs  #   平台服务接口 + 托盘动作定义
│  ├─ BatterySnapshot.cs    #   跨平台共享电池快照模型
│  ├─ AppPaths.cs           #   平台标准目录（AppData / Library）
│  ├─ HudDisplayCoordinator.cs  # HUD 事件合并（同一物理动作只播一次）
│  ├─ NotchBirthGeometry.cs # macOS 刘海出生几何（纯函数，10 个单测）
│  ├─ SelfTest.cs           #   --selftest 诊断输出
│  ├─ Windows/              #   仅 Windows 目标编译（由 csproj 的 Compile Remove 保证）
│  │  ├─ WindowsPowerNative.cs      # P/Invoke：powrprof、message-only 窗口
│  │  ├─ WindowsPowerMonitor.cs     # 电源变化监听 + 400ms 去抖（消息循环线程）
│  │  ├─ WindowsBatteryReader.cs    # powrprof 主路径 + WMI 兜底
│  │  ├─ WindowsAutoStart.cs        # HKCU Run 键
│  │  └─ WindowsPlatformServices.cs
│  └─ MacOS/                #   仅 macOS 目标编译
│     ├─ MacOSPowerNative.cs        # IOKit / CoreFoundation P/Invoke
│     ├─ MacOSBatteryReader.cs      # AppleSmartBattery → BatterySnapshot
│     ├─ MacOSPowerMonitor.cs       # IOPS 通知 + 5s 轮询 + 400ms 去抖（双线程加锁）
│     ├─ MacOSAppKit.cs             # libobjc 互操作（NSWindow / NSApp / NSBundle / NSProcessInfo）
│     ├─ MacOSAutoStart.cs          # ~/Library/LaunchAgents plist
│     ├─ MacOSTrayMenu.cs           # 原生 NativeMenu
│     └─ MacOSPlatformServices.cs
├─ Animations/
│  └─ HudAnimations.cs      # 时间线与动画轨道（KeySpline 逐段缓动）
├─ Services/
│  ├─ Logger.cs             # 文件日志（平台目录见 AppPaths）
│  └─ UpdateChecker.cs      # GitHub Releases 更新检查
├─ Settings/
│  ├─ AppSettings.cs        # 设置模型（缩放/动画微调/位置/显示器/语言/提醒）
│  ├─ SettingsManager.cs    # 设置加载与持久化
│  ├─ SettingsWindow.axaml  # 设置窗口（通用 / 动画 / 通知 / 关于）
│  └─ SettingsWindow.axaml.cs
├─ Views/
│  ├─ HudWindow.axaml(.cs)  # HUD 视觉树（胶囊 / 电标 / 标题 / 数字 / 徽章 / 波纹）
│  └─ TrayMenuWindow.axaml(.cs)    # 仅 Windows：左键自绘托盘菜单
├─ Styles/                  # 颜色主题与图标几何（StreamGeometry）
├─ Assets/                  # tray_bolt.png（运行时图标）+ tray_bolt.ico（仅 Windows）
├─ build/macos/             # Info.plist 模板 + entitlements
├─ scripts/package-macos.sh # macOS .app / .dmg 打包（内含 --selftest 冒烟门禁）
├─ docs/                    # 移植评估方案 / 分发说明 / 验证指南
├─ tests/EndfieldCharge.Tests/  # 单测：HUD 事件合并时序（10 用例）
├─ installer/               # 仅 Windows：Inno Setup 脚本与中文本地化
└─ .github/workflows/       # CI：Windows + macOS 双平台构建，打标签发 Release
```

## macOS 与 Windows 的差异

| 方面 | Windows | macOS |
|------|---------|-------|
| 呈现方式 | 托盘图标 + 任务栏 | 菜单栏常驻（`LSUIElement` 不占 Dock、不进 Cmd-Tab） |
| 菜单交互 | 左键弹出自绘深色菜单，**同时**播放一次电量预览 | 左键弹出系统原生菜单；预览走菜单里的「预览电量 HUD」项（Avalonia 的 macOS 后端不派发托盘点击事件） |
| 电池数据 | `CallNtPowerInformation`（mWh），WMI 兜底 | IORegistry `AppleSmartBattery`（mAh × mV），`pmset` 兜底；**可算健康度** |
| 事件通知 | `RegisterPowerSettingNotification`，2s 轮询兜底 | `IOPSNotificationCreateRunLoopSource`，5s 轮询兜底 |
| 省电模式 | 「省电模式」，24H2+ 用 GUID_ENERGY_SAVER_STATUS | 「低电量模式」，`isLowPowerModeEnabled` |
| 开机自启 | `HKCU\...\CurrentVersion\Run` | `~/Library/LaunchAgents/*.plist` |
| 设置文件 | `%APPDATA%\EndfieldCharge\settings.json` | `~/Library/Application Support/EndfieldCharge/settings.json` |
| 日志 | `%TEMP%\EndfieldCharge\` | `~/Library/Logs/EndfieldCharge/` |
| HUD 窗口层级 | `Topmost=True` 即可 | 额外把 `NSWindow` 提升到 `NSPopUpMenuWindowLevel(101)` 并设 `collectionBehavior=273`，以盖住菜单栏/全屏应用并跨 Space 可见 |
| HUD 窗口透明 | `AllowsTransparency` 由 `TransparencyLevelHint` 驱动 | **必须**声明 `TransparencyLevelHint="Transparent"`，否则 NSWindow 背景为不透明的系统底色（表现为整块黑底）；详见 [`docs/macos-verification.md`](docs/macos-verification.md) §6 |
| HUD 位置 | 工作区顶部 | 菜单栏下方；左右位置按胶囊边缘留 20 DIP |
| HUD 出生动画 | 无，直接弹出 | **刘海屏专属前导**：窄条从刘海内部离开并展开，滑向左/中/右落点，保留三态时间线；收起沿原路返回。读取 `NSScreen.safeAreaInsets`，无刘海或读取失败时不播放 |
| 分发 | 安装包 exe / 便携 zip | `.app`（未公证，需绕过 Gatekeeper）+ `.dmg` |

## 动画实现要点

- Avalonia 11 的 `KeyFrame` 使用 **`KeySpline`（贝塞尔控制点）** 做逐段缓动，多关键帧下 `Animation.Easing` 不生效 —— 每段必须显式指定 `KeySpline`，否则该段为线性。
- `Border.HeightProperty`（即 `Layoutable.HeightProperty`）可直接动画，因此胶囊高度的 `60 → 90 → 60` 用独立轨道驱动。
- 收尾「整体缩小关没」由外层 `ScaleHost` 的 `RenderTransform` 统一缩放，胶囊本身宽度不动。

## 许可证

MIT
