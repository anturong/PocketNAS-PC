# DLNA 模块计划（存档）

> 状态：**未开工**，方案待定。存档于 2026-11-04。
> 结论先行：等真正要做时，先跑 P0 取证，再在 A / B 之间选一条。

## 目标

DLNA 模块要做到和 WebDAV、SSH 一致的**无感开启**：点一下按钮就完事，用户不需要自己进控制面板配媒体流。

## 两条路径（待选）

| | A. 系统媒体流 + 自动塞库 | B. 内嵌 MiniDLNA |
|---|---|---|
| 无感开启 | ✅ | ✅ |
| 任意目录 | ⚠️ 需把目录塞进 Windows 媒体库，污染用户资源管理器，撤销麻烦 | ✅ 原生支持（`media_dir=`） |
| 转码 | ❌ | ❌ |
| 依赖 | 系统自带，零体积 | 需带第三方 exe，约 5~10MB |
| 许可 | 无 | GPLv2（分发需附许可证与源码获取方式） |
| 兼容性 | 电视兼容性好（微软 profile） | 一般，个别品牌需调 |

**倾向 B**：理由是与 WebDAV/SSH 的体验一致性——能选任意目录。系统媒体流的"塞库"副作用一旦出问题，排查成本远高于自带一个 exe。

**B 的前置风险**：MiniDLNA 没有官方 Windows 二进制，现存都是 cygwin 编译的第三方版本，质量参差，必须先验证稳定性再决定。

## 系统媒体流的硬约束（选 A 时须知）

1. 只共享 Windows「视频 / 音乐 / 图片」三个库，不认任意目录
2. 依赖 Windows Media Player 可选功能；N 版 / 精简版需装 Media Feature Pack，可能要联网和重启
3. 不转码，MKV / 外挂字幕能否播取决于电视

## 阶段拆分

| 阶段 | 内容 | 说明 |
|---|---|---|
| **P0 取证** | 真机只读检测：`WMPNetworkSvc` 服务状态、NSS 注册表结构、防火墙规则组、可选功能名 | 不凭记忆写死键名，Win10 各版本差异大 |
| **P1 骨架** | `DlnaView` 替换占位页：状态卡 + 一键开启按钮 | 外壳侧，与 `WebDavView` / `SshView` 同一套写法 |
| **P2 后端** | 按选定路径实现 `IDlnaBackend` | A 或 B |
| **P3 闭环** | 选目录 → 开启 → 显示「电视可见 / 已索引 N 个文件」→ 关闭 | 体验对齐另外两个模块 |

## 抽象接口（P4 预留）

```csharp
interface IDlnaBackend
{
    string Name { get; }
    Task<DlnaStatus> DetectAsync();
    Task<Result> EnableAsync();
    Task<Result> OpenSettingsAsync();
}
```

先只有 `WindowsMediaStreamingBackend`，将来接 MiniDLNA 时新增实现，UI 不用改。

## 不做的事

- 不自己实现 UPnP AV 协议栈（SSDP + SOAP + DIDL-Lite）。最简版本约一千多行能跑通，但各品牌电视怪癖多，兼容性成本远超预期。

## 其他相关结论

- Win10 已于 2025-10-14 停止支持，系统媒体流仍可用但不再更新
- 播放期间电脑不能休眠；访客 Wi-Fi 会隔离设备，必然发现不到
