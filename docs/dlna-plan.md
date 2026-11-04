# DLNA 模块计划（存档）

> 状态：**未开工**，方案待定。存档于 2026-11-04。
> 结论先行：等真正要做时，先跑 P0 取证，再在 A / B 之间选一条。

## 方向调整（2026-11-04 追加）

用户明确要 **Windows 做接收方**，发起方是手机。这推翻了下面「PC 当片源」的全部假设，
模块定位从 **DMS（发送方）** 改为 **DMR（接收方 / 渲染器）**。

因此 A / B 两条路线（系统媒体流、MiniDLNA）**都不适用**——它们只能做发送。
DMR 需要自己实现，开源参考见下文「参考实现」。

### 但先要确认手机走的是哪条路

| 手机上的操作 | 协议 | 我们的工作 |
|---|---|---|
| 视频 App 的「投屏 / TV」按钮，列表里出现设备名 | DLNA（DMR 方向） | 写 DMR 接收端，可行 |
| 系统「投屏 / 屏幕镜像」 | Miracast，整屏镜像 | 无需开发，用 Windows「无线显示器」 |
| iPhone 的隔空播放 | AirPlay | 不是 DLNA，另说 |

**未确认前不要开工。** 判断方法：点投屏后若出现「PC 设备名」且可选中投送 → DLNA；
若提示「正在连接 / 镜像中」→ Miracast。

### DMR 路线（确认走 DLNA 后）

**参考实现**（GPL 项目，只读不抄）：

| 项目 | 语言 | 价值 |
|---|---|---|
| **Macast**（xfangfang） | Python，GPL-3.0 | **最贴近我们的目标**：跨平台 DLNA 接收器，状态栏常驻，手机投电脑 |
| gmrender-resurrect | C + GStreamer + libupnp | 最干净的 DMR 参考，几千行 |
| Platinum UPnP SDK | C++，带 VS 工程 | 有完整 MediaRenderer 示例，Windows 可跑 |
| libupnp 的 tv_device | C | 最小 DMR 样例 |
| Coherence | Python | MediaRenderer 状态机写得最清楚 |

## Macast 值得借鉴的点

项目地址：https://gitee.com/xfangfang/Macast （Python，GPL-3.0，1.2K stars）

1. **形态和我们的设想一致**：状态栏/托盘常驻、无主窗口，手机一投就播。
2. **播放后端用外部 mpv 进程**，靠 mpv 的 JSON IPC 控制播放，而不是内嵌播放器。
   - 好处：格式覆盖最全、崩溃不影响主程序、可随时切换播放器
   - 自带「边下边看」「复制媒体链接」这类实用功能
3. **插件机制**：内置插件商店，用来适配各播放器与**国内 App 私有魔改的 DLNA 协议**
   （其 `nirvana` 插件专做这件事）。这是个警示：只实现标准 DLNA，未必连得上 B 站、爱奇艺这类 App。
4. **排障路径可直接复用**：默认 HTTP 端口 1068，用 `http://IP:1068` 能否访问判断是否被发现；
   顺序是 防火墙 → 路由器 UPnP/AP 隔离 → App 本地网络权限 → 是否同网段。

### 与我们的方案差异

| 维度 | Macast | 我们的计划 |
|---|---|---|
| 语言 | Python | C# / .NET 8 |
| 播放 | 外部 mpv 进程 + IPC | 内嵌 libVLC（FlyleafLib 备选） |
| 形态 | 状态栏应用 | 主程序内的模块（托盘常驻） |
| 许可 | GPL-3.0 | 只读参考，不引入其代码 |

播放后端值得再权衡：**mpv IPC 的控制能力比 libVLC 内嵌更完整**（实时状态、精确 seek、切轨），
且进程隔离更稳；代价是 mpv 为 GPL，随包分发需谨慎。第一版先用 libVLC 内嵌，
遇到格式或稳定性问题再考虑切 mpv。

**.NET 现实**：没有成熟的 DMR 库。`Rssdp`（MIT）只能解决 SSDP 发现。

**要实现的协议层**（约 1500~2500 行）：

```
SSDP     NOTIFY alive/byebye + 响应 M-SEARCH           ~200 行
HTTP     /description.xml + 三个服务的 SCPD             ~150 行
SOAP     AVTransport / RenderingControl / ConnectionManager  ~600 行
GENA     SUBSCRIBE/NOTIFY + LastChange 事件             ~300 行
播放集成  收到 URI → 拉流 → 回报进度与状态               ~400 行
```

**播放内核**：FlyleafLib（LGPL，FFmpeg 封装，WPF 下 Direct3D 渲染）优先，
备选 LibVLCSharp（LGPL）。系统 MediaElement 格式覆盖太少，排除。

**实施顺序**：

| 阶段 | 目标 | 验收 |
|---|---|---|
| D1 | 空壳 DMR：能被手机投屏列表发现 | 手机列表出现「PocketNAS-PC」 |
| D2 | 能播放：接收 URI 并播放 | 手机点投送后 PC 出画面 |
| D3 | 控制：暂停/进度/音量 + 事件回报 | 手机上拖进度条、调音量同步生效 |
| D4 | 兼容性：BubbleUPnP、各视频 App、系统投屏 | 逐个真机过一遍 |

**风险**：协议写完只占三分之一，剩下是各 Control Point 的兼容性调试（metadata 格式差异大）。

---

以下为原「PC 当片源（DMS）」方案，仅在方向回到发送方时适用。

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
