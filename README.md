# EndfieldCharge · 终末地风格电量 HUD

插上 / 拔掉充电器时，从屏幕顶部弹出一块"灵动岛"式 HUD，显示当前电量（mWh 与百分比）。
视觉与动画风格复刻《终末地》工业 / 超充模式 HUD。

- **插电**：完整三态动画 —— 电标弹出 → 胶囊撑高成圆角矩形显示「超充模式」→ 收成圆胶囊显示电量 → 停留 → 整体缩小收回
- **拔电**：简化动画 —— 只弹电量圆胶囊，内容在胶囊完全出来后快速显现 → 停留 → 收回

## 功能

| 功能 | 说明 |
|------|------|
| 电量显示 | 剩余 / 满充容量（mWh，整数）与百分比，读取 `CallNtPowerInformation`，WMI 兜底 |
| 电源监听 | `RegisterPowerSettingNotification` 订阅 GUID_ACDC_POWER_SOURCE，2s 轮询兜底，400ms 双向去抖（过滤 Windows 满电瞬时抖动） |
| 低电量变色 | 电量 < 20% 时黄绿电量圈变红（#FF4D4F） |
| 开机自启 | 托盘菜单勾选，写 `HKCU\...\CurrentVersion\Run`（当前用户级，无需管理员） |
| 主屏定位 | 强制主显示器顶部居中，避免多屏时弹到副屏 |
| 托盘常驻 | 无主窗口，托盘图标 + 右键菜单（预览 / 开机自启 / 退出） |

## 运行要求

- Windows 10 1809+ / Windows 11
- .NET 8 运行时（Release 为框架依赖单文件发布，需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)）
- x64

## 构建

```bash
# 调试
dotnet build -c Debug

# 发布（单文件 exe，输出到 bin/Release/net8.0-windows/win-x64/publish/）
dotnet publish -c Release -o publish
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
│  ├─ PowerNative.cs        # P/Invoke：powrprof、message-only 窗口
│  └─ PowerWatcher.cs       # 电源变化监听 + 去抖确认
├─ Views/
│  ├─ HudWindow.axaml       # HUD 视觉树（胶囊 / 电标 / 标题 / 数字 / 徽章 / 波纹）
│  └─ HudWindow.axaml.cs    # 定位、数据绑定、完整版与简化版播放入口
├─ Styles/                  # 颜色主题与图标几何（StreamGeometry）
└─ Assets/                  # 托盘图标
```

## 动画实现要点

- Avalonia 11 的 `KeyFrame` 使用 **`KeySpline`（贝塞尔控制点）** 做逐段缓动，多关键帧下 `Animation.Easing` 不生效 —— 每段必须显式指定 `KeySpline`，否则该段为线性。
- `Border.HeightProperty`（即 `Layoutable.HeightProperty`）可直接动画，因此胶囊高度的 `60 → 90 → 60` 用独立轨道驱动。
- 收尾「整体缩小关没」由外层 `ScaleHost` 的 `RenderTransform` 统一缩放，胶囊本身宽度不动。

## 许可证

MIT
