using System;

namespace EndfieldCharge.Services;

/// <summary>充电器档位。</summary>
public enum ChargeMode
{
    /// <summary>还没有可判定数据（刚插上、或电池不报速率）。</summary>
    Unknown,

    /// <summary>慢速充电（手机充电器、弱 PD 口、小功率适配器）。</summary>
    Normal,

    /// <summary>高功率充电（原装适配器、65W 以上 PD）。</summary>
    Fast,
}

/// <summary>
/// 充电器快慢判定。
///
/// ── 现实约束（必须说清）────────────────────────────────────────────
/// Windows 不提供"适配器额定功率"的公开接口：WMI 里只有 Win32_Battery 的
/// 充放电状态，USB-C PD 的协商功率也没有用户态 API。
/// 因此这里退而求其次，用**实测进电池功率**来反推充电器档位——
/// 这正是用户体感上的"充得快 / 充得慢"，比额定功率更贴近实际。
///
/// ── 判定逻辑 ──────────────────────────────────────────────────────
///   1. 取 SYSTEM_BATTERY_STATE.Rate（毫瓦，正=充电）作为瞬时充电功率；
///   2. EMA 平滑（alpha 0.35），滤掉固件上报的毛刺；
///   3. 双阈值滞回：≥30W 判快充，跌到 <22W 才回落到慢充，
///      避免在阈值附近反复横跳；
///   4. 记录本次充电会话的峰值功率，写入设置，下次插入时立刻定档——
///      因为刚插上的头一两秒功率还没爬上来，而 HUD 的标题 1.2 秒后就要显示。
/// </summary>
public sealed class ChargeModeService
{
    /// <summary>判为快充的功率阈值（瓦）。</summary>
    public const double FastThresholdWatts = 30d;

    /// <summary>从快充回落到慢充的阈值（瓦），滞回下限。</summary>
    public const double ReleaseThresholdWatts = 22d;

    private const double EmaAlpha = 0.35;

    private readonly AppSettings _settings;
    private double _emaWatts;
    private bool _hasEma;
    private double _peakWatts;

    public ChargeModeService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>当前判定结果。</summary>
    public ChargeMode Current { get; private set; } = ChargeMode.Unknown;

    /// <summary>平滑后的充电功率（瓦）；尚无数据时为 null。</summary>
    public double? SmoothedWatts => _hasEma ? _emaWatts : null;

    /// <summary>本次充电会话观测到的峰值充电功率（瓦）。</summary>
    public double PeakWatts => _peakWatts;

    /// <summary>
    /// 刚插上时用的初始档位：用上次记录的判定结果，
    /// 没有历史则保守给慢充（宁可显示"充电模式"，也不要乱标超充）。
    /// </summary>
    public ChargeMode InitialGuess() =>
        _settings.LastChargerFast ? ChargeMode.Fast : ChargeMode.Normal;

    /// <summary>喂入一次电池快照，返回更新后的档位。</summary>
    public ChargeMode Update(BatterySnapshot? snapshot)
    {
        // 拔电：本次会话结束，把峰值归档后清空，等下次插入重新累积
        if (snapshot is null || !snapshot.AcOnline || !snapshot.Charging)
        {
            ResetSession();
            return Current;
        }

        double? watts = snapshot.RateWatts;
        if (watts is null || watts.Value <= 0)
            return Current;

        double w = Math.Clamp(watts.Value, 0d, 300d);

        _emaWatts = _hasEma ? _emaWatts + EmaAlpha * (w - _emaWatts) : w;
        _hasEma = true;

        if (_emaWatts > _peakWatts)
            _peakWatts = _emaWatts;

        // 双阈值滞回
        Current = Current switch
        {
            ChargeMode.Fast => _emaWatts < ReleaseThresholdWatts ? ChargeMode.Normal : ChargeMode.Fast,
            _ => _emaWatts >= FastThresholdWatts ? ChargeMode.Fast : ChargeMode.Normal,
        };

        return Current;
    }

    /// <summary>会话结束：归档峰值，清空平滑值。</summary>
    public void ResetSession()
    {
        if (_peakWatts > 0)
        {
            _settings.LastChargerFast = Current == ChargeMode.Fast;
            _settings.LastChargerPeakWatts = Math.Round(_peakWatts, 1);
            _settings.Save();
        }

        _hasEma = false;
        _emaWatts = 0d;
        _peakWatts = 0d;
        Current = ChargeMode.Unknown;
    }
}
