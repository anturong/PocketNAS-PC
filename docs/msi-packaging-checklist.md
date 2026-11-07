# MSI 安装包 · 打包说明与验证清单

> 状态：**待实施**，仅说明与检查项，**不含任何代码改动**。记录于 2026-11-07，同日修订主线。
> 结论先行：做 MSI / setup.exe 的**首要目标是保证服务在独立账号下稳定运行** —— 把服务登录身份、授权、目录与证书权限在安装期一次性固化，使无人值守、重启、切换登录用户时行为一致。安装时提权只是达成它的手段，不是目的。
> OneDrive 云占位符导致服务起不来，只是「独立账号 + Session 0 与用户会话隔离」这一类问题的**一个实例**，不是问题全集。
> 本文只新增文档，不修改 README、AGENTS.md 或任何源码；用于以后打包 MSI 时逐条核对。

---

## 1. 为什么要做：独立账号运行稳定

### 1.1 主线：服务不该依赖「当前登录的用户」

WebDAV / FTPS 都是 Windows 服务，目标是常驻、开机自启、无人值守。服务跑在 **Session 0**，登录身份是 `LocalSystem` 或专用服务账号，与交互式登录用户隔离。

安装期必须一次性完成、且**互为前提**的一串状态：

```
选择/创建服务账号 → 授予「作为服务登录」→ 给该账号 SID 授权目录 / 证书私钥 / URL ACL
  → 程序文件落到本地磁盘 → 可写数据落到 ProgramData 并授权
  → 用同一账号注册服务 → 启动并验证 → 失败可回滚
```

这些操作全部需要管理员权限。只做一半最危险：例如服务注册了、但账号没被授予「作为服务登录」，现象是「装完当场能跑，重启或改密码后突然起不来」，比直接报错更难查。

MSI / setup.exe 的价值就在这里：**提权 + 原子化 + 回滚 + 卸载清理**，把这套状态一次性做对，而不是运行时每点一次按钮弹一次 UAC、留下半套状态。

### 1.2 独立账号带来的隔离约束（问题全集）

| 类别 | 独立账号下的实际行为 | 典型症状 |
|---|---|---|
| 用户会话资源 | OneDrive / 网盘占位符、映射网络驱动器、用户态同步客户端、`%USERPROFILE%` 下文件全部不可见或无法按需水合 | 服务加载 DLL 失败、配置文件找不到 |
| 用户态配置 | `HKCU`、`%APPDATA%`、证书 `CurrentUser\My`、DPAPI / 凭据管理器都指向**服务账号自己的 profile** | 登录用户装的证书 / 凭据服务读不到，HTTPS 握手失败 |
| 账号生命周期 | 账号密码过期、密码策略变更、「作为服务登录」被组策略覆盖 | 服务突然无法启动，事件 `7038` / `7041` |
| 权限边界 | 共享目录、日志、配置、证书私钥、HTTP.sys URL ACL 必须显式授给服务账号 SID | 启动后写不进日志 / 读不到共享目录 |
| 会话隔离 | Session 0 无桌面、无托盘、无网络驱动器、无用户态 provider | 依赖界面或用户会话的逻辑静默失败 |
| 可写位置 | `Program Files` 对普通账号只读 | 配置保存失败、共享目录建不了 |

2026-11-07 那次故障落在**第一行**：OneDrive 只是触发点，换成任何「只在登录用户会话里才可用」的资源，同样会挂。

### 1.3 案例：OneDrive 云占位符（1.2 第一行的实例）

便携版 `PocketNAS-PC-v1.0.0-win-x64-portable` 解压在 OneDrive 同步目录里，主程序日志报：

```
[18:50:01] WebDAV 防火墙已放行 WebDAV Service (HTTPS) 端口 8756
[18:50:02] WebDAV 正在安装服务（会弹出 UAC 确认）...
[18:50:06] WebDAV 操作失败: 启动服务失败: Cannot start service 'WebDAVService' on computer '.'.
[18:50:06] WebDAV 提权失败（UAC 被取消）
```

当天 16:11 → 18:50 共 6 次，每次都失败。Windows 事件日志里的真实原因：

| 来源 | 事件 | 内容 |
|---|---|---|
| Application · .NET Runtime | 1023 | `WebDAVService.exe`: `Failed to load the dll from [...\service\hostfxr.dll], HRESULT: 0x8007018B`；`Error code: 0x80008082` |
| System · SCM | 7009 | 等待 `WebDAV Service` 服务的连接超时（30000 毫秒） |
| System · SCM | 7000 | 服务启动失败：did not respond to the start or control request in a timely fashion |
| System · SCM | 7045 | 服务已安装，自动启动，服务账户 `LocalSystem`（每点一次「启动」都重装一次） |

故障链：

```
便携包放在 OneDrive
  → 925 个文件中 774 个是「仅联机」占位符
    （属性含 FILE_ATTRIBUTE_OFFLINE 0x1000 / FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS 0x400000）
  → service\hostfxr.dll、coreclr.dll、hostpolicy.dll 等自包含 .NET 运行时 DLL 全未落地
  → 服务以 LocalSystem 在 Session 0 运行，没有登录用户会话里的 OneDrive 同步客户端响应水合请求
  → LoadLibrary 失败，.NET 宿主根本没起来
  → SCM 等 30 秒超时 → ServiceController.Start() 抛 "Cannot start service ..."
  → 上层把异常一律归为「提权失败（UAC 被取消）」
```

服务目录 452 个文件里当时 449 个是占位符，所以不是偶发是必现。

### 1.4 排查这类问题时的三个误判

| 误判 | 事实 |
|---|---|
| **UAC 被取消** | 没有取消。UAC helper 成功注册了服务（SCM 7045），失败发生在之后的**启动**阶段。只有 `Win32Exception.NativeErrorCode == 1223 (ERROR_CANCELLED)` 才是取消 |
| **权限不够** | 服务本来就是 `LocalSystem`，本机最高权限，目录 ACL 里 SYSTEM 也是 FullControl。`0x8007018B` 是「云占位符在当前上下文无法水合」，**不是 Access Denied** |
| **HTTPS 8756 / 证书配置有问题** | 防火墙与端口配置正常。故障发生在 .NET 运行时加载阶段，**早于**读取 `webdav_config.json` 和绑定证书 |

### 1.5 现场怎么修好的（仅作对照，不是长期方案）

- 把整个目录复制到本地路径触发水合，占位符 774 → 0；
- 目录标记 `FILE_ATTRIBUTE_PINNED (0x80000)`「始终保留在此设备上」防再次脱水；
- 之后服务 `Running`，8080 / 8756 均由 HTTP.sys 监听，**全程没有改过任何权限配置**。

它只解决了 1.2 表格的第一行，而且 pin 依赖 OneDrive 客户端常驻；换机器、重装系统、用户手动「释放空间」都会复发。1.2 里其余五行仍在，所以这只是治标。

### 1.6 MSI 在这套方案里负责什么 / 不负责什么

| MSI 负责（安装期固化） | MSI 不负责（要在设计上排除） |
|---|---|
| 创建 / 校验服务账号、授予 `SeServiceLogonRight`、密码与永不过期策略 | 让用户会话里的资源对服务可用（云文件、HKCU、CurrentUser 证书、网络驱动器） |
| 给账号 SID 授 ProgramData / 共享目录 / 日志 / 证书私钥 / URL ACL | 运行时临时补权限、临时弹 UAC 修状态 |
| `ServiceInstall` 注册服务时绑定同一账号，卸载自动停服清理 | 服务进程依赖当前登录用户 |
| 文件落地 `%ProgramFiles%`、数据落地 `%ProgramData%` | 用户把安装目录改到云盘（仍需自检拦住） |
| 防火墙例外、证书导入 `LocalMachine\My` + 私钥授权、端口绑定 | — |

边界：安装目录用户**可以改**，选到 OneDrive 就原样复发，因此仍需要启动自检（P10）。MSI 不是免疫。

---

## 2. 打包前：必须落实的提示项

> 这些是打包时**要逐条确认 / 提示**的事项，本文不改代码。每条写明原因，别只当流程走过场。

### 2.1 构建与产物

| 编号 | 提示项 | 原因 / 不做的后果 |
|---|---|---|
| P01 | 版本号单一来源，`-Version` 注入 `ProductVersion`，不改源码 | 版本号写死在多处会导致升级判定错乱 |
| P02 | 打包前核对 `dist` 完整：主程序 + `service\` + `service\ftps\` + `service\web\` + `android\` + 语言资源，约 925 个文件 | 少一个运行时 DLL 就是启动期崩溃，现场极难定位 |
| P03 | 用脚本扫描 `dist` 自动生成 WiX component，**不要手写** | 900+ 文件手写既易漏，升级时 component GUID 也难稳定 |
| P04 | 固定 `UpgradeCode` + `MajorUpgrade` | 否则装出两份并存、服务重复注册 |

### 2.2 服务账号与授权（主线，先核对）

| 编号 | 提示项 | 原因 / 不做的后果 |
|---|---|---|
| P05 | 明确服务账号方案并写进文档：`LocalSystem` / 专用本地账号 / gMSA / 虚拟服务账户 `NT SERVICE\WebDAVService`（**本项目待定，本文不拍板**） | 账号类型决定后面所有授权方式；不定死会出现「装的时候用 A 账号、注册服务用 B 账号」 |
| P06 | 账号创建与「作为服务登录」在安装期完成，且**幂等**（重复安装不重复建号） | 运行时弹 UAC 补授权容易半途失败；重装重复建号会留下垃圾账号 |
| P07 | 账号生命周期：禁止交互登录、密码策略（永不过期或定期轮换）；用 gMSA / 虚拟账户可免密码管理 | 密码过期是「用了一周突然起不来」的经典原因 |
| P08 | 给服务账号 SID 显式授权：ProgramData 下配置 / 日志 / 共享目录、证书私钥、`netsh http add urlacl` | 服务账号默认没有这些权限，只用管理员账号本地测试时看不出来 |
| P09 | **设计上不引用任何用户会话资源**：不读 `HKCU` / `CurrentUser` 证书 / `%APPDATA%` / `%USERPROFILE%` / 网盘目录；配置与共享目录一律 ProgramData 绝对路径 | 这是 1.2 整张表的根，OneDrive 只是其中一条 |
| P10 | 启动自检：安装目录在云盘，或文件带 `OFFLINE (0x1000)` / `RECALL_ON_DATA_ACCESS (0x400000)` → 明确报错「安装目录不可用于服务，请装到本地磁盘」 | MSI 装到 OneDrive 仍会复发；且不要误报 UAC |
| P11 | 服务注册交给 WiX `ServiceInstall` / `ServiceControl`，账号参数与 P05 一致，ImagePath 自动带引号、卸载自动停服 | 现在每次点「启动」都 `sc create`（当天 7045 重复 6 次），账号参数容易漏 |

### 2.3 落地位置与数据目录

| 编号 | 提示项 | 原因 / 不做的后果 |
|---|---|---|
| P12 | 安装目录固定 `ProgramFiles64Folder` / `{autopf}`，明确禁止云同步目录 | 见 1.2 第一行：本地实体文件、非云同步目录 |
| P13 | 配置、`webdav_share`、日志分离到 `%ProgramData%\PocketNAS\`，并按 P08 给账号授权 | 现在都写在程序目录；装进 Program Files 后普通账号只读，会**新增**「保存配置失败 / 共享目录写不进」 |
| P14 | 所有路径改绝对：基于 `AppContext.BaseDirectory` 或 ProgramData | 服务 CWD 是 `C:\Windows\System32`；`root_dir: "webdav_share"` 这类相对路径会解析到系统目录 |

### 2.4 网络、证书与健壮性

| 编号 | 提示项 | 原因 / 不做的后果 |
|---|---|---|
| P15 | 防火墙例外按模块放行：WebDAV 8080 / 8756，FTPS 21 / 990，SSH 22 | 运行时临时加规则，重装 / 换端口容易漏 |
| P16 | HTTPS 证书导入 `LocalMachine\My`、给服务账号私钥读权限、`netsh http add sslcert` 绑定 8756 | 当前 `cert_thumbprint: null` + `enable_https: true` 是空配置；证书放 `CurrentUser\My` 服务账号读不到 |
| P17 | 覆盖安装前先停服务 | 服务占用 exe 会导致替换失败或要求重启 |
| P18 | 异常分类：只有 `1223 (ERROR_CANCELLED)` 是 UAC 取消，其它输出原始异常并附 SCM `7000` / `7009` / `7038` / `7041` 排查提示 | 本次日志把人带偏了半小时 |
| P19 | 卸载保留 ProgramData 下配置 / 日志 / 共享目录，或明确询问；**账号是否删除也要有明确策略** | 别误删用户数据，也别留孤儿账号 |

### 2.5 便携版并存时

| 编号 | 提示项 | 原因 |
|---|---|---|
| P20 | 保留便携版时，说明文档明确「不要解压到 OneDrive / 网盘目录」 | 便携版仍可自由选目录，光做 MSI 挡不住这条路径 |

---

## 3. 安装后验证清单（逐条打勾）

**A. 产物**

- [ ] MSI / setup.exe 版本号与 `-Version` 一致
- [ ] `msiexec /i ... /quiet` 静默安装成功，安装日志无 error
- [ ] 安装包体积合理（自包含运行时约 300 MB，压缩后应在 100–160 MB 量级）

**B. 账号与授权（主线）**

- [ ] `services.msc` → 服务属性「登录为」是 P05 选定的账号，**不是当前登录用户**
- [ ] 该账号在「本地安全策略 → 用户权限分配 → 作为服务登录」里
- [ ] **注销当前用户 / 换另一个账号登录**，服务仍能启动并对外提供 WebDAV / FTPS 访问
- [ ] **完全无人登录**（锁屏 / 重启后未登录）状态下服务自动启动且可访问
- [ ] 停止 → 启动服务不依赖任何交互式会话
- [ ] 事件日志无 `7038`（账号登录失败）、`7041`（登录类型不允许）、`7000`
- [ ] 模拟密码过期 / 密码变更后，失败原因可诊断；若用 gMSA / 虚拟账户则确认无密码管理项
- [ ] 用 `icacls` / `netsh http show urlacl` 逐项核对账号对 ProgramData、共享目录、证书私钥、URL ACL 的权限

**C. 落地位置**

- [ ] 文件在 `%ProgramFiles%\PocketNAS-PC`，**不在** OneDrive / 网盘目录
- [ ] `service\hostfxr.dll`、`coreclr.dll`、`hostpolicy.dll` 属性**不含** `Offline` / `RecallOnDataAccess`
      排查命令：`Get-Item ...\hostfxr.dll | Select-Object Attributes`
- [ ] 故意把安装目录改到 OneDrive 路径 → 安装被拒绝，或启动自检给出明确提示（**不是**「UAC 被取消」）
- [ ] 安装目录含空格 / 中文时服务 ImagePath 仍带引号且能启动

**D. 服务与端口**

- [ ] `WebDAVService`、`FtpsService` 状态 `Running`，启动类型符合预期
- [ ] `services.msc` 里「可执行文件的路径」指向 Program Files 且带引号
- [ ] 8080、8756（以及启用模块的 21 / 990 / 22）处于监听；`netsh http show sslcert` 有 8756 绑定
- [ ] 事件日志无 `.NET Runtime 1023`、无 SCM `7000` / `7009`

**E. 配置与数据**

- [ ] 配置写在 `%ProgramData%\PocketNAS\`，普通用户可保存
- [ ] 共享目录可写、日志能落盘（服务账号身份下）
- [ ] 服务启动日志打印实际使用的配置文件**绝对路径**
- [ ] 改端口 → 保存 → 重启服务后生效（界面与服务读的是同一份配置）

**F. 隔离专项回归**

- [ ] 断网状态下服务仍能启动（本地实体文件不应依赖网络）
- [ ] 重启机器后服务自动启动成功
- [ ] 把程序放到 OneDrive / 网盘目录 → 自检能拦住并给出明确提示，而不是「提权失败（UAC 被取消）」
- [ ] 证书在 `LocalMachine\My` 且服务账号有私钥读权限，**不是** `CurrentUser\My`
- [ ] 全程不读 `HKCU` / `%APPDATA%` / `%USERPROFILE%`（用日志或 ProcMon 确认实际读取路径）
- [ ] 共享目录不放用户目录 / 映射网络驱动器，改放 ProgramData 后服务可读写

**G. 升级与卸载**

- [ ] 旧版 → 新版 `MajorUpgrade` 后不残留两份服务
- [ ] 覆盖安装时服务被正确停止，exe 可替换
- [ ] 卸载后服务已删除、防火墙规则已清理、ProgramData 数据保留、账号处置符合 P19

---

## 4. 参考：USBOrder 里可直接复用的现成模板

仓库 `github.com/anturong/USBOrder`（本地工程 `UsbDriveManager\`）：

| 文件 | 作用 |
|---|---|
| `deploy/Install-UsbDriveService.ps1` | **独立账号方案的现成实现，最值得参考**：「使用已有账号」走 `LogonUser` 验证、检查是否在 Administrators、补授「作为服务登录」（密码非永不过期只警告、不改账号）；「新建专用账号」负责建号、加组、授服务登录权、禁止交互登录。两种模式都会把 `service\` 复制到 Program Files 再注册服务 |
| `deploy/make-wxs.ps1` | 扫描 `dist` 自动生成 WiX v4 的 `Generated.wxs`，900+ 文件正合适 |
| `installer/Package.wxs` | `ProgramFiles64Folder` + `MajorUpgrade` + `MediaTemplate EmbedCab` + 快捷方式 |
| `deploy/build-msi.ps1` | `dotnet build` WiX 工程，产物改名到 `release\` |
| `installer/usborder.iss` | Inno Setup 7：`PrivilegesRequired=admin`、`{autopf}\...`、`Compression=lzma2/max`，约 40 行 |
| `%ProgramData%\UsbDriveManager\config.json` + `logs\` | 数据目录分离的现成范式 |

选型建议：单人项目优先 **Inno Setup**（脚本短、压缩好、改名字即可用）；**MSI** 留给静默 / 企业部署。两套脚本在 USBOrder 里都已跑通，不必重新设计。

---

## 5. 边界与待同步

- 本文**只新增文档**，不改代码、不改 README、不改 AGENTS.md。
- 修订记录：2026-11-07 初版把 OneDrive 云占位符当成了主线；同日按项目实际目标修订为「独立账号运行稳定」，OneDrive 降级为 1.2 隔离约束中的一个实例（1.3 案例）。服务账号的具体选型（P05）**仍待项目拍板**，本文只列选项不替项目决定。
- 按 `AGENTS.md` 第 1 节「每次踩到新坑同步更新」的约定，建议后续把「服务账号与用户会话隔离」补进 AGENTS.md 第 4 节「已知的坑」；并在 README「已知待办」加一条指向本文的链接。这两处**本次未改**，等确认后再动。
- 打包脚本真正落地时，遵守「一个功能一个 commit」；文档用 `docs:` 前缀单独提交，不与构建脚本混在一个 commit 里。

---

## 6. 落地记录：v1 用 Inno Setup 做出来了（2026-11-07）

结论：**先做 Inno Setup**，脚本在 `installer/PocketNAS.iss`，构建脚本 `installer/build.ps1`，产物 `dist\PocketNAS-PC-Setup-1.0.0.exe`（318 MB → 108 MB）。MSI 留到需要静默/企业部署时再补。

### 6.1 这一版做了什么

| 对应清单 | 做法 |
|---|---|
| P10 / P12 | `DefaultDirName={autopf}\PocketNAS`；选目录时 `NextButtonClick` 拦 OneDrive / Dropbox / 坚果云 / 百度网盘等，直接报 0x8007018B 的原因 |
| P17 | `ssInstall` 阶段先 `net stop WebDAVService` + `net stop FtpsService`，`CloseApplications=yes` 兜住界面进程 |
| P11 | 装完在 `ssPostInstall` 调程序自己的 `--install` / `--ftps-install`（防火墙、URL ACL、证书绑定都在里面，不另写一份）；成功后写标记 `service\.setup-installed-services` |
| P19 | 卸载时**只删自己注册的服务**（凭上面那个标记），再问一句要不要连共享目录和配置一起删；静默卸载默认保留数据 |
| P13 变通 | 配置 / 日志 / 共享目录仍在 `service\` 下（代码现状），但安装期只对 **`webdav_config.json`、`ftps_config.json`、`webdav_share\`、`logs\`、`ftps\share\`、`ftps\logs\`** 授 `Users:(M)`；**`service\` 目录本身不授权**，否则普通用户能替换 `WebDAVService.exe`，等于白送 SYSTEM |

### 6.2 这一版没做（下次要补）

1. **服务账号仍是 `LocalSystem`**（`WindowsServiceHelper.InstallService` 里 `sc create` 没带 `obj=`）。清单 P05 倾向的虚拟账户 `NT SERVICE\WebDAVService` 需要改代码：`sc create ... obj= "NT SERVICE\WebDAVService" password= ""`，同时 ACL 要把授权对象从 `Users` 换成该虚拟账户的 SID —— 属于代码改动，未动。
2. **数据没搬到 `{commonappdata}\PocketNAS`**（P13 / P14 的正解）。搬了就要改 `ConfigHelper` 与 `FtpsOptions` 的基准目录，未动。
3. **提权路径没有端到端实测**：本机 UAC 在非交互会话里被系统直接拒（`ERROR_CANCELLED`），Program Files 写入、服务注册、Program Files 下的 icacls 只做了脚本自检，需要人工点一次「是」才能确认。低权限自测包已验：文件落地、`{}` 配置生成、三个数据目录创建、ACL 授权范围、静默卸载保留数据且不动同名服务。
4. 版本号 `1.0.0` 是手写常量（`#define MyAppVersion`），没做成从 csproj 读；改版本要同时改 `csproj` 与 `.iss`（或 `build.ps1 -Version`）。

### 6.3 已知构建坑

- `dotnet build -r win-x64` 之前必须先 `dotnet restore` **`PocketNAS.FtpsService` 单独一次**，否则嵌套 publish 报 `NETSDK1047`（assets 里没有 `net8.0/win-x64`）。
- 嵌套 publish 在 `PublishDir` 被外部覆盖时**不会带 `web\*.html`**，`build.ps1` 里单独补了一次；否则装出来的 WebDAV 服务没有浏览页。
- `.iss` 的 Pascal 注释里不能写 `{app}` 这类花括号，会被当成嵌套注释，编译报 `Syntax error`。
