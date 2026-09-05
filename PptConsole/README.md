# PPT 控制台（PptConsole）

为触屏演示设计的 PPT 放映控制终端：底部吊起三颗胶囊（左＝页面列表＋上一页，中＝笔/选择/橡皮，右＝下一页＋页面列表），继承 EndfieldCharge HUD 的设计语言（#312F30 实心胶囊、白字 opacity 分层、唯一彩色 #C6CA4C、BackOut 回弹吊起、向上扩展工具面板）。墨迹采用自绘全屏覆盖层（不写入 PPT 文件），按页记忆，支持压感、笔画级擦除、逐笔撤销。

## 环境要求

| 项 | 说明 |
|---|---|
| 目标平台 | Windows（x64）触屏设备（触控优先，笔/鼠标亦可） |
| .NET SDK | 8.0（`<TargetFramework>net8.0-windows`） |
| 触屏手写压感 | 需支持 `PointerPointProperties.Pressure` 输入的设备 |

## 构建

```bash
cd PptConsole
dotnet restore
dotnet build -c Debug
dotnet publish -c Release -r win-x64 --self-contained false
```

产物目录：`bin/Release/net8.0-windows/win-x64/publish/`。

> 提示：Avalonia 通过 Roslyn 源生成器编译 `.axaml`。若改动界面后出现"找不到 InitializeComponent"等异常，先 `dotnet clean` 再重新构建。

## 运行

```bash
# 常驻模式：F5 进入 PowerPoint 放映 → 自动吊起控制台；Esc 退出 → 自动收回
dotnet run -c Debug

# 演示模式：无 PowerPoint 时在主屏预览控制台（便于无放映机调试 UI/动画）
dotnet run -c Debug -- --demo
```

托盘图标（左下角）左键可手动吊起/收回控制台，右键菜单退出。

## 操作映射

| 胶囊 | 按钮 | 行为 |
|---|---|---|
| 左短 | 上一页 | SendInput 方向键 ←，墨迹层页码 −1 |
| 左短 | ☰ 页面列表 | 入口（COM 阶段实现） |
| 中长 | 笔 | 墨迹层接管触控；向上弹出"颜色/粗细/撤销/清空"面板 |
| 中长 | 选择 | 墨迹层点击穿透，触控直达放映 |
| 中长 | 橡皮 | 墨迹层接管触控；弹出"大小/撤销/清空"面板；笔画级擦除 |
| 右短 | 下一页 | SendInput 方向键 →，墨迹层页码 +1 |
| 右短 | ☰ 页面列表 | 入口（COM 阶段实现） |

点击任意工具按钮即播 500ms 触控波纹；胶囊吊起按 左→中→右 错峰入场；面板收起支撑（BackOut）。

## 架构

```
App.axaml / App.axaml.cs         组装：放映检测→吊起、事件总线、托盘、演示模式
└─ ConsoleController.cs          三窗口协调器（摆放/错峰入场/出场/置顶）
   ├─ LeftCapsuleWindow          左短胶囊〔列表‖上一页〕
   ├─ CenterCapsuleWindow        中胶囊〔笔‖选择‖橡皮〕+ 向上扩展工具面板（高度自适应）
   └─ RightCapsuleWindow         右短胶囊〔下一页‖列表〕
CapsuleBehavior.cs               胶囊窗公共行为（静态：窗口壳/NOACTIVATE/Place/进出场）
InkOverlayWindow                 全屏自绘墨迹层（穿透切换/压感/橡皮/撤销/按页记忆）
ConsoleAnimations.cs             动画库（原 HudAnimations 曲线库的可逆交互化）
SlideshowWatcher.cs              Win32 轮询 PowerPoint 放映窗口（screenClass）+ 显示器定位
InputNative / Win32Interop       SendInput 方向键；窗口样式/显示器/键盘 P/Invoke
Styles/ ConsoleTheme+Geometries  设计令牌与图标几何
```

## 真机验证清单（首次在触屏 Windows 上跑）

1. 控制台吊起后，触控点击胶囊 → 放映窗口是否保持前台（NOACTIVATE 生效，键击不被吞）。
2. 笔模式画一笔 → 切"选择" → 翻页 → 翻回来，墨迹是否按页恢复。
3. 橡皮指示圈与笔画级擦除是否跟随触点。
4. 胶囊之间空白处触控是否直达放映层（三窗拆分收益）。

## 已知边界与后续（COM 阶段）

- **页码感知**：当前靠内部计数，不感知键盘/遥控翻页。接入 `SlideShowNextSlide` 事件后消除盲区。
- **页面列表**：左右 ☰ 为占位入口，COM 阶段实现页数/缩略图/点击跳页（`GotoSlide`）。
- **WPS 兼容**：放映窗口类名仅验证了 MS PowerPoint 的 `screenClass`；WPS 类名待验证（`SlideshowWatcher.SlideClasses` 已留扩展数组）。
- 若放映程序以管理员运行，`SendInput` 会被 UIPI 拦截，需同权限运行。