using System;
using EndfieldCharge.Services;

namespace EndfieldCharge.Models;

/// <summary>HUD 展示的内容类型。</summary>
public enum HudMode
{
    /// <summary>有电池：显示电量（mWh + 百分比）。</summary>
    Battery,

    /// <summary>台式机（无电池）：显示整机功耗 W + 设备负载。</summary>
    PowerDraw,
}

/// <summary>HUD 一次展示所需的全部内容（数值已格式化，视图只负责贴）。</summary>
public sealed record HudContent(
    HudMode Mode,
    string Title,        // 超充模式 / 充电模式 / 功耗模式（简化胶囊不用）
    string Caption,      // 英文角标
    string Primary,      // 大数字
    string Unit,         // 大数字右侧的小字单位
    int RingPercent,     // 徽章圆环百分比
    bool RingDanger)     // 圆环是否走告警色
{
    public static readonly HudContent Empty = new(
        HudMode.Battery, string.Empty, string.Empty, "--", string.Empty, 0, false);
}

/// <summary>把电池 / 负载 / 充电器档位翻译成一份 HUD 内容。</summary>
public static class HudContentFactory
{
    /// <summary>设备负载走告警色的阈值（%）。</summary>
    public const int LoadDangerPercent = 90;

    /// <summary>低电量阈值（%），与负载告警无关。</summary>
    public const int LowBatteryPercent = 20;

    /// <summary>
    /// 组装"三态完整动画"用的内容。
    /// </summary>
    /// <param name="battery">电池快照；台式机为 null。</param>
    /// <param name="load">系统负载快照。</param>
    public static HudContent CreateFull(
        BatterySnapshot? battery,
        SystemLoadSnapshot load)
    {
        // ---- 台式机 / 读不到电池：功耗模式 ----
        if (battery is null || !battery.HasBattery)
        {
            return new HudContent(
                Mode: HudMode.PowerDraw,
                Title: "功耗模式",
                Caption: "/// POWER DRAW",
                Primary: load.Watts.HasValue ? load.Watts.Value.ToString("F0") : "--",
                Unit: "W",
                RingPercent: (int)Math.Round(load.LoadPercent),
                RingDanger: load.LoadPercent >= LoadDangerPercent);
        }

        // ---- 笔记本：统一显示充电模式（不再区分快慢充，避免歧义） ----
        return new HudContent(
            Mode: HudMode.Battery,
            Title: "充电模式",
            Caption: "/// CHARGE MODE",
            Primary: (battery.RemainingWh * 1000).ToString("F0"),
            Unit: $"/{battery.FullWh * 1000:F0}",
            RingPercent: battery.Percent,
            RingDanger: battery.Percent < LowBatteryPercent);
    }

    /// <summary>
    /// 组装"简化胶囊"用的内容（拔电时只弹一下电量 / 功耗）。
    /// </summary>
    public static HudContent CreateSimple(
        BatterySnapshot? battery,
        SystemLoadSnapshot load)
    {
        if (battery is null || !battery.HasBattery)
        {
            return new HudContent(
                Mode: HudMode.PowerDraw,
                Title: string.Empty,
                Caption: string.Empty,
                Primary: load.Watts.HasValue ? load.Watts.Value.ToString("F0") : "--",
                Unit: "W",
                RingPercent: (int)Math.Round(load.LoadPercent),
                RingDanger: load.LoadPercent >= LoadDangerPercent);
        }

        return new HudContent(
            Mode: HudMode.Battery,
            Title: string.Empty,
            Caption: string.Empty,
            Primary: (battery.RemainingWh * 1000).ToString("F0"),
            Unit: $"/{battery.FullWh * 1000:F0}",
            RingPercent: battery.Percent,
            RingDanger: battery.Percent < LowBatteryPercent);
    }
}
