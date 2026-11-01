/*
 * ec_adapter.h —— EndfieldCharge 电量适配器插件 ABI（C 语言风格）
 *
 * 插件 = 一个原生 DLL（位数需与主程序一致：x64），放进主程序目录的 adapters\ 下，
 * 重启主程序后自动加载。一个 DLL 就是一个适配器。
 *
 * 导出函数（全部 extern "C" + cdecl，函数名不可修饰）：
 *   ec_get_snapshot           必需：填充快照结构体，返回 0 = 成功，非 0 = 失败/暂无数据
 *   ec_adapter_id             可选：稳定唯一 ID（UTF-8），缺省用 DLL 文件名
 *   ec_adapter_name           可选：HUD 药丸上的显示名（UTF-8），缺省用 DLL 文件名
 *   ec_set_change_callback    可选：注册状态变化回调（变化即时弹 HUD；不注册则靠 15 秒轮询兜底）
 *
 * 字符串指针归插件所有，主程序不释放；插件只需保证指针在下一次调用前一直有效
 * （推荐静态缓冲区 / 字符串字面量）。
 *
 * 版本兼容：主程序调用 ec_get_snapshot 前会把 out->struct_size 设为它认识的
 * 结构体大小（即缓冲区容量）。插件若发现该值小于自己的 sizeof(struct ec_snapshot)，
 * 直接返回非 0 即可；任何情况下都不要写超过该容量的字节。新版只向结构体末尾追加字段。
 *
 * 未知值约定：double 字段填 NAN；remaining_seconds 填 -1。
 *
 * C/C++ 插件示例（编译：cl /LD my_adapter.c  或  gcc -shared -o my_adapter.dll my_adapter.c）
 * --------------------------------------------------------------------------
 *   #include "ec_adapter.h"
 *
 *   static const char* k_id   = "my-phone";
 *   static const char* k_name = "我的手机";
 *
 *   const char* ec_adapter_id(void)   { return k_id; }
 *   const char* ec_adapter_name(void) { return k_name; }
 *
 *   int32_t ec_get_snapshot(struct ec_snapshot* out)
 *   {
 *       if (out->struct_size < (uint32_t)sizeof(struct ec_snapshot))
 *           return 1;                        // 版本不兼容
 *       out->has_battery = 1;
 *       out->percent     = 58;
 *       out->ac_online   = 0;
 *       out->charging    = 0;
 *       out->remaining_wh = 20.9;
 *       out->full_wh      = 36.0;
 *       out->rate_watts   = NAN;             // 未知
 *       out->design_capacity_wh = NAN;       // 未知
 *       out->remaining_seconds  = -1;        // 未知
 *       return 0;
 *   }
 *
 * Rust 插件（Cargo.toml: crate-type = ["cdylib"]）
 * --------------------------------------------------------------------------
 *   #[repr(C)] struct EcSnapshot { ... 与本头文件字段一致 ... }
 *   #[no_mangle] pub extern "C" fn ec_get_snapshot(out: *mut EcSnapshot) -> i32 { ... }
 *   #[no_mangle] pub extern "C" fn ec_adapter_name() -> *const c_char { b"...\0".as_ptr().cast() }
 *
 * Go（cgo + //export）、Zig、Nim、Free Pascal 等一切能导出 C 函数的语言同理。
 */

#ifndef EC_ADAPTER_H
#define EC_ADAPTER_H

#include <stdint.h>
#include <math.h> /* NAN */

#if defined(__cplusplus)
#define EC_EXPORT extern "C" __declspec(dllexport)
#else
#define EC_EXPORT __declspec(dllexport)
#endif

/* 电量快照：自然对齐（x64/x86 一致），主程序侧镜像大小 64 字节。
 * has_battery = 0 时主程序按"无数据"处理，其余字段被忽略。 */
struct ec_snapshot
{
    uint32_t struct_size;          /* 主程序填：其结构体大小（缓冲区容量）；插件勿改 */
    int32_t  has_battery;          /* 0/1：是否有有效电池数据 */
    int32_t  percent;              /* 0..100 */
    int32_t  ac_online;            /* 0/1：是否接外接电源 */
    int32_t  charging;             /* 0/1：是否充电中 */
    /* 4 字节自然对齐填充（C 编译器与 C# Sequential 布局都会自动插入） */
    double   remaining_wh;         /* 剩余电量（Wh） */
    double   full_wh;              /* 当前满充容量（Wh），用于算百分比与健康度 */
    double   rate_watts;           /* 功率（W）：正=充电，负=放电；未知 = NAN */
    double   design_capacity_wh;   /* 设计容量（Wh）：健康度用；未知 = NAN */
    int64_t  remaining_seconds;    /* 预计剩余时间（秒）；未知 = -1 */
};

/* 可选：稳定唯一 ID（UTF-8）。ID 重复的插件会被忽略；缺省用 DLL 文件名。 */
EC_EXPORT const char* ec_adapter_id(void);

/* 可选：HUD 药丸上的显示名（UTF-8，可含中文）；缺省用 DLL 文件名。 */
EC_EXPORT const char* ec_adapter_name(void);

/* 必需：读取一次快照。返回 0 = 成功；非 0 = 失败/暂无数据（主程序忽略本次读数）。 */
EC_EXPORT int32_t ec_get_snapshot(struct ec_snapshot* out);

/* ================= 状态变化主动通知（可选，事件驱动） ================= */

/* 主程序注册进来的回调：user 为注册时传入的不透明指针，原样传回即可。 */
typedef void (*ec_change_fn)(void* user);

/* 可选：注册状态变化回调。插件应在"充电状态 / 外接电源状态改变"时调用 on_change(user)。
 *
 * 约定：
 *   - 回调可能来自任意线程，插件自行保证调用安全；
 *   - 不要在回调内再调用任何 ec_* 函数（有死锁风险）；
 *   - 尽快返回，不要做耗时工作；
 *   - 只在状态真正翻转时调用（插拔电、开始/停止充电），电量百分比变化不要调；
 *   - 不调用也不会丢事件：主程序另有 15 秒轮询兜底，回调只是让弹出更即时。 */
EC_EXPORT void ec_set_change_callback(ec_change_fn on_change, void* user);

#endif /* EC_ADAPTER_H */
