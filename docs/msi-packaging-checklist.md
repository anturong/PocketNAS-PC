# MSI 安装包 · 打包说明与验证清单

> 状态：**待实施**，仅说明与检查项，**不含任何代码改动**。记录于 2026-11-07。
> 结论先行：做 MSI / setup.exe 值得，但**能躲开故障的不是「安装时提权」，而是「程序文件固定落到本地磁盘」**。
> 本文只新增文档，不修改 README、AGENTS.md 或任何源码；用于以后打包 MSI 时逐条核对。

---

## 1. 为什么要做：一次真实故障的复盘

### 1.1 现象

便携版 `PocketNAS-PC-v1.0.0-win-x64-portable` 解压在 **OneDrive 同步目录**里，主程序右侧日志报：

```
[18:50:01] WebDAV 防火墙已放行 WebDAV Service (HTTPS) 端口 8756
[18:50:02] WebDAV 正在安装服务（会弹出 UAC 确认）...
[18:50:06] WebDAV 操作失败: 启动服务失败: Cannot start service 'WebDAVService' on computer '.'.
[18:50:06] WebDAV 提权失败（UAC 被取消）
```

当天 16:11 → 18:50 共试了 6 次，**每次都失败**。防火墙规则本身是成功放行的。

### 1.2 真实原因（Windows 事件日志）

| 来源 | 事件 | 内容 |
|---|---|---|
| Application · .NET Runtime | 1023 | `WebDAVService.exe`: `Failed to load the dll from [...\service\hostfxr.dll], HRESULT: 0x8007018B`；`Error code: 0x80008082` |
| System · SCM | 7009 | 等待 `WebDAV Service` 服务的连接超时（30000 毫秒） |
| System · SCM | 7000 | 服务启动失败：The service did not respond to the start or control request in a timely fashion |
| System · SCM | 7045 | 服务已安装，服务类型 自动启动，服务账户 `LocalSystem`（每点一次「启动」都会重装一次服务） |

故障链：

```
便携包放在 OneDrive
  → 925 个文件中 774 个是「仅联机」占位符
    （属性含 FILE_ATTRIBUTE_OFFLINE 0x1000 / FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS 0x400000）
  → service\hostfxr.dll、coreclr.dll、hostpolicy.dll 等自包含 .NET 运行时 DLL 全未落地
  → 服务以 LocalSystem 在 Session 0 运行，没有用户登录会话里的 OneDrive 同步客户端响应水合请求
  → LoadLibrary 失败，.NET 宿主根本没起来
  → SCM 等 30 秒超时 → ServiceController.Start() 抛 "Cannot start service ..."
  → 上层把异常一律归为「提权失败（UAC 被取消）」
```

服务目录下 452 个文件里当时有 449 个是占位符，所以这不是偶发，是必现。

### 1.3 三个必须澄清的误判（避免改错地方）

| 误判 | 事实 |
|---|---|
| **UAC 被取消** | 没有取消。UAC helper 成功注册了服务（SCM 7045），失败发生在之后的**启动**阶段。属于异常分类错误：只有 `Win32Exception.NativeErrorCode == 1223 (ERROR_CANCELLED)` 才是取消 |
| **权限不够** | 服务本来就是 `LocalSystem`，本机最高权限，目录 ACL 里 SYSTEM 也是 FullControl。`0x8007018B` 是「云占位符在当前上下文无法水合」，**不是 Access Denied**，再提权也没用 |
| **HTTPS 8756 / 证书配置有问题** | 防火墙与端口配置正常。故障发生在 .NET 运行时加载阶段，**早于**读取 `webdav_config.json` 和绑定证书 |

### 1.4 现场怎么修好的（仅作对照，不是长期方案）

- 把整个目录复制到本地路径以触发水合，占位符 774 → 0；
- 目录标记 `FILE_ATTRIBUTE_PINNED (0x80000)`「始终保留在此设备上」，防止再次脱水；
- 之后服务 `Running`，8080 与 8756 均由 HTTP.sys 监听，全程**没有改过任何权限配置**。

局限：pin 依赖 OneDrive 客户端常驻；换机器、重装系统、用户手动「释放空间」都会复发；用户也仍可能把便携包解压到任意云盘 / 网盘目录。所以这只是治标。

### 1.5 MSI 为什么能躲开，以及为什么不是靠提权

| 提权真正解决的 | 真正切断故障链的 |
|---|---|
| 注册 / 启动 Windows 服务、防火墙放行、`netsh http add sslcert`、写 `Program Files`、把证书导入 `LocalMachine\My` | 安装器把程序文件**固定落到 `%ProgramFiles%`**：本地实体文件、非云同步目录，用户不再自己解压到 OneDrive |

也就是说，**权限从来不是瓶颈，文件落地位置才是**。MSI 顺带把落地位置规范化了，才躲开这个问题。

边界：MSI / Inno 的安装目录用户**可以改**，选到 OneDrive 就原样复发；因此仍需要启动自检（见 P11），MSI 不是免疫。

---

## 2. 打包前：必须落实的提示项

> 这些是打包时**要逐条确认 / 提示**的事项，本文不改代码。每条都写明了原因，别只当流程走过场。

### 2.1 构建与产物

| 编号 | 提示项 | 原因 / 不做的后果 |
|---|---|---|
| P01 | 版本号单一来源，`-Version` 注入 `ProductVersion`，不改源码 | 版本号写死在多处会导致升级判定错乱 |
| P02 | 打包前核对 `dist` 完整：主程序 + `service\` + `service\ftps\` + `service\web\` + `android\` + 语言资源，共约 925 个文件 | 少一个运行时 DLL 就是启动期崩溃，且现场极难定位 |
| P03 | 用脚本扫描 `dist` 自动生成 WiX component，**不要手写** | 900+ 文件手写既易漏，升级时 component GUID 也难保持稳定 |
| P04 | 固定 `UpgradeCode` + `MajorUpgrade` | 否则装出两份并存、服务重复注册 |

### 2.2 落地位置与数据目录（本次故障的核心）

| 编号 | 提示项 | 原因 / 不做的后果 |
|---|---|---|
| P05 | 安装目录固定 `ProgramFiles64Folder` / `{autopf}`，**明确禁止装到 OneDrive、网盘、云同步目录** | 这就是本次故障的根因；安装器若能拒绝这类路径，问题在源头就被切断 |
| P06 | 配置、`webdav_share`、日志分离到 `%ProgramData%\PocketNAS\`，并给 Users 写权限 | 现在这些都写在程序目录；装进 Program Files 后普通用户只读，会**新增**「保存配置失败 / 共享目录写不进」的 bug |
| P07 | 所有路径改绝对：基于 `AppContext.BaseDirectory` 或 ProgramData | 服务以 LocalSystem 跑，CWD 是 `C:\Windows\System32`；`root_dir: "webdav_share"` 这类相对路径会解析到系统目录 |
| P08 | 安装时把 `service\` 复制到 Program Files 再注册服务，服务运行文件不留在分发位置 | 参考 USBOrder 的做法；服务指向的最后目录必须是本地实体文件 |

### 2.3 服务、网络与证书

| 编号 | 提示项 | 原因 / 不做的后果 |
|---|---|---|
| P09 | 服务注册交给 WiX `ServiceInstall` / `ServiceControl` | ImagePath 自动带引号、卸载自动停服；现在每次点「启动」都 `sc create`（7045 当天重复 6 次） |
| P10 | 防火墙例外按模块放行：WebDAV 8080 / 8756，FTPS 21 / 990，SSH 22 | 目前是运行时弹 UAC 临时加规则，重装 / 换端口容易漏 |
| P11 | 启动自检：安装目录在云盘，或文件带 `OFFLINE (0x1000)` / `RECALL_ON_DATA_ACCESS (0x400000)` → **明确报错**「安装目录不可用于服务，请装到本地磁盘」 | MSI 装到 OneDrive 仍会复发；必须给出可行动的提示，而不是误报 UAC |
| P12 | HTTPS 证书在安装期落地：自签证书导入 `LocalMachine\My`、给 SYSTEM 私钥读权限、`netsh http add sslcert` 绑定 8756 | 当前 `cert_thumbprint: null` + `enable_https: true` 是空配置；这些操作本来就必须提权，放安装期最合适 |
| P13 | 异常分类修正：只有 1223 是 UAC 取消，其它输出原始异常，并附 SCM 事件 7000 / 7009 排查提示 | 本次日志把人带偏了半小时 |
| P14 | 覆盖安装前先停服务 | 服务占用 exe 会导致替换失败，或提示「重启后才能替换」 |
| P15 | 卸载保留 `%ProgramData%` 下的配置 / 日志 / 共享目录，或明确询问 | 别误删用户数据 |

### 2.4 便携版并存时

| 编号 | 提示项 | 原因 |
|---|---|---|
| P16 | 保留便携版时，说明文档明确写「不要解压到 OneDrive / 网盘目录」 | 便携版仍可自由选目录，光做 MSI 挡不住这条路径 |

---

## 3. 安装后验证清单（逐条打勾）

**A. 产物**

- [ ] MSI / setup.exe 版本号与 `-Version` 一致
- [ ] `msiexec /i ... /quiet` 静默安装成功，安装日志无 error
- [ ] 安装包体积合理（自包含运行时约 300 MB，压缩后应在 100–160 MB 量级）

**B. 落地位置（最关键）**

- [ ] 文件在 `%ProgramFiles%\PocketNAS-PC`，**不在** OneDrive / 网盘目录
- [ ] `service\hostfxr.dll`、`coreclr.dll`、`hostpolicy.dll` 属性**不含** `Offline` / `RecallOnDataAccess`
      排查命令：`Get-Item ...\hostfxr.dll | Select-Object Attributes`
- [ ] 故意把安装目录改到 OneDrive 路径 → 安装被拒绝，或启动自检给出明确提示（**不是**「UAC 被取消」）
- [ ] 安装目录含空格 / 中文时服务 ImagePath 仍带引号且能启动

**C. 服务与端口**

- [ ] `WebDAVService`、`FtpsService` 状态 `Running`，启动类型符合预期
- [ ] `services.msc` 里「可执行文件的路径」指向 Program Files 且带引号
- [ ] 8080、8756（以及启用模块的 21 / 990 / 22）处于监听；`netsh http show sslcert` 有 8756 绑定
- [ ] 事件日志无 `.NET Runtime 1023`、无 SCM `7000` / `7009`

**D. 配置与数据**

- [ ] 配置写在 `%ProgramData%\PocketNAS\`，普通用户可保存
- [ ] 共享目录可写、日志能落盘（服务以 LocalSystem 运行）
- [ ] 服务启动日志打印实际使用的配置文件**绝对路径**
- [ ] 改端口 → 保存 → 重启服务后生效（界面与服务读的是同一份配置）

**E. 本次故障的专项回归**

- [ ] 断网状态下服务仍能启动（本地实体文件不应依赖网络）
- [ ] 重启机器后服务自动启动成功
- [ ] 对安装目录做「释放空间」/ 脱水（若仍在云盘）→ 自检能拦住并给出明确提示
- [ ] 复现旧故障场景时，日志里出现的是云占位符提示，而不是「提权失败（UAC 被取消）」

**F. 升级与卸载**

- [ ] 旧版 → 新版 `MajorUpgrade` 后不残留两份服务
- [ ] 覆盖安装时服务被正确停止，exe 可替换
- [ ] 卸载后服务已删除、防火墙规则已清理、ProgramData 数据保留

---

## 4. 参考：USBOrder 里可直接复用的现成模板

仓库 `github.com/anturong/USBOrder`（本地工程 `UsbDriveManager\`）：

| 文件 | 作用 |
|---|---|
| `deploy/make-wxs.ps1` | 扫描 `dist` 自动生成 WiX v4 的 `Generated.wxs`，900+ 文件正合适 |
| `installer/Package.wxs` | `ProgramFiles64Folder` + `MajorUpgrade` + `MediaTemplate EmbedCab` + 快捷方式 |
| `deploy/build-msi.ps1` | `dotnet build` WiX 工程，产物改名到 `release\` |
| `installer/usborder.iss` | Inno Setup 7：`PrivilegesRequired=admin`、`{autopf}\...`、`Compression=lzma2/max`，约 40 行 |
| `deploy/Install-UsbDriveService.ps1` | 把 `service\` 复制到 Program Files 再注册服务 —— **这一步才是真正切断云占位符的地方** |
| `%ProgramData%\UsbDriveManager\config.json` + `logs\` | 数据目录分离的现成范式 |

选型建议：单人项目优先 **Inno Setup**（脚本短、压缩好、改名字即可用）；**MSI** 留给静默 / 企业部署。两套脚本在 USBOrder 里都已跑通，不必重新设计。

---

## 5. 边界与待同步

- 本文**只新增文档**，不改代码、不改 README、不改 AGENTS.md。
- 按 `AGENTS.md` 第 1 节「每次踩到新坑同步更新」的约定，建议后续把「云占位符导致服务启动失败」补进 AGENTS.md 第 4 节「已知的坑」；并在 README「已知待办」加一条指向本文的链接。这两处**本次未改**，等确认后再动。
- 打包脚本真正落地时，遵守「一个功能一个 commit」；文档用 `docs:` 前缀单独提交，不与构建脚本混在一个 commit 里。
