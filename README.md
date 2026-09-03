# EndfieldCharge · 终末地风格电量 HUD

插上 / 拔掉充电器时，从屏幕顶部弹出一块"灵动岛"式 HUD，显示当前电量（mWh 与百分比）。
视觉与动画风格复刻《终末地》工业 / 超充模式 HUD。

- **插电**：完整三态动画 —— 电标弹出 → 胶囊撑高成圆角矩形显示「超充模式」→ 收成圆胶囊显示电量 → 停留 → 整体缩小收回
- **拔电**：简化动画 —— 只弹电量圆胶囊，内容在胶囊完全出来后快速显现 → 停留 → 收回

## 下载安装

从 [Releases](https://github.com/Lenkmat/endfield-charge/releases) 下载：

| 文件 | 说明 |
|------|------|
| `EndfieldCharge-x.y.z-setup.exe` | Inno Setup 安装版（中文/英文向导，可选桌面快捷方式与开机自启） |
| `EndfieldCharge-x.y.z-portable.zip` | 便携版，解压即用 |

## 功能

| 功能 | 说明 |
|------|------|
| 电量显示 | 剩余 / 满充容量（mWh，整数）与百分比，读取 `CallNtPowerInformation`，WMI 兜底 |
| 电源监听 | `RegisterPowerSettingNotification` 订阅 GUID_ACDC_POWER_SOURCE，2s 轮询兜底，400ms 双向去抖（过滤 Windows 满电瞬时抖动） |
| 低电量变色 | 电量 < 20% 时黄绿电量圈变红（#FF4D4F） |
| 提醒通知 | 低电量提醒（阈值可调 5–40%）与充满提醒（≥99%），卡牌风格弹窗，4s 自动消失 |
| 设置窗口 | 全局缩放（0.4–1.2）、显示时长（2–10s）、HUD 位置（顶部居中/靠右/靠左）、显示器选择、语言、开机自启，保存即生效并持久化 |
| 托盘菜单 | 左键单击弹出自定义深色菜单（预览 / 设置 / 检查更新 / 预览工具 / 退出）；右键为原生菜单兜底 |
| 预览工具 | 独立预览窗口，不插拔电源即可调整缩放、时长、位置并实时查看效果 |
| 检查更新 | 读取 GitHub Releases API，比较程序集版本，一键跳转下载页 |
| 多语言 | 中文 / 英文，默认跟随系统，可在设置中手动切换 |
| 开机自启 | 设置窗口「通用」页开关，写 `HKCU\...\CurrentVersion\Run`（当前用户级，无需管理员） |
| 统一图标 | 托盘 / 各窗口 / exe / 安装器 / 卸载器统一使用 `Assets\tray_bolt` 图标 |
| 日志 | `%TEMP%\EndfieldCharge\log-YYYYMMDD.txt`，方便排查托盘菜单定位等问题 |

## 运行要求

- Windows 10 1809+ / Windows 11
- .NET 8 运行时（Release 为框架依赖单文件发布，需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)）
- x64

## 构建

```bash
# 调试
dotnet build -c Debug

# 发布（单文件 exe，输出到 publish/）
dotnet publish -c Release -o publish

# 本地打安装包（需安装 Inno Setup，iscc 在 PATH 中）
iscc installer\EndfieldCharge.iss
```

> 注意：`PublishSingleFile` 只把托管 dll 打进 exe，SkiaSharp 的 native dll
> （libSkiaSharp / libHarfBuzzSharp / av_libglesv2）仍需与 exe 同目录 ——
> 便携分发请打包整个 `publish/` 目录，不要只拷 exe。

### CI / 发布（GitHub Actions）

推送到 `main` 分支会自动构建安装包与便携版 zip（Actions 页面可下载 artifact）。
推送 `v*` 标签（如 `v1.0.0`）会额外创建 GitHub Release，并把标签版本号写入
程序集版本与安装包文件名：

```bash
git tag v1.0.0
git push origin v1.0.0
```

## 调试参数

启动时追加参数，无需真的插拔电源：

| 参数 | 作用 |
|------|------|
| `--demo` | 用示例数据播放一次**完整**动画（插电） |
| `--preview` | 用本机真实电池数据播放一次完整动画 |
| `--preview-unplug` | 用示例数据播放一次**简化**动画（拔电） |
| `--debug-ring` | 静态呈现状态 C（电量态）1.5s |
| `--power-log` | 输出电源事件日志到 `%TEMP%\power-log.txt` |

> 注意：这几个参数互斥，按 `--demo` → `--preview-unplug` → `--preview` 的优先级生效。

## 项目结构

```
EndfieldCharge/
├─ Animations/
│  └─ HudAnimations.cs      # 时间线与动画轨道（KeySpline 逐段缓动）
├─ Services/
│  ├─ AutoStart.cs          # 开机自启（HKCU Run 键读写）
│  ├─ BatteryService.cs     # 电池快照（剩余/满充 mWh、百分比、AC 状态）
│  ├─ Logger.cs             # 文件日志（%TEMP%\EndfieldCharge\）
│  ├─ PowerNative.cs        # P/Invoke：powrprof、message-only 窗口
│  ├─ PowerWatcher.cs       # 电源变化监听 + 去抖确认
│  └─ UpdateChecker.cs      # GitHub Releases 更新检查
├─ Settings/
│  ├─ AppSettings.cs        # 设置模型（缩放/时长/位置/显示器/语言/提醒）
│  ├─ SettingsManager.cs    # 设置加载与持久化
│  ├─ SettingsWindow.axaml  # 设置窗口（通用 / 通知 / 关于）
│  └─ SettingsWindow.axaml.cs
├─ Views/
│  ├─ HudWindow.axaml(.cs)  # HUD 视觉树（胶囊 / 电标 / 标题 / 数字 / 徽章 / 波纹）
│  ├─ PreviewWindow.axaml(.cs)     # 预览工具窗口
│  └─ TrayMenuWindow.axaml(.cs)    # 左键自定义托盘菜单
├─ Styles/                  # 颜色主题与图标几何（StreamGeometry）
├─ Assets/                  # tray_bolt.png（运行时图标）+ tray_bolt.ico（exe/安装器图标）
├─ installer/
│  ├─ EndfieldCharge.iss    # Inno Setup 安装脚本
│  └─ Languages/            # 中文本地化（随仓库分发）
└─ .github/workflows/       # CI：自动构建 + 打标签发 Release
```

## 动画实现要点

- Avalonia 11 的 `KeyFrame` 使用 **`KeySpline`（贝塞尔控制点）** 做逐段缓动，多关键帧下 `Animation.Easing` 不生效 —— 每段必须显式指定 `KeySpline`，否则该段为线性。
- `Border.HeightProperty`（即 `Layoutable.HeightProperty`）可直接动画，因此胶囊高度的 `60 → 90 → 60` 用独立轨道驱动。
- 收尾「整体缩小关没」由外层 `ScaleHost` 的 `RenderTransform` 统一缩放，胶囊本身宽度不动。

## 许可证

MIT
