# 技术债：主程序管理员权限 → 按需提权

> 记录时间：2026-11-08　触发：PC 1.0.0 安装完成页点「启动」报 `CreateProcess 失败；错误代码 740`
> 状态：**1.0.1 已用一行修复绕过（安装器侧），根因仍在，本条未做**

## 一、现状

`src/PocketNAS.PC/app.manifest` 与 `SshHelper/app.manifest` 均为：

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

后果：

1. **整个主程序必须以管理员身份启动**。双击图标必弹 UAC，程序全程高权限运行，权限远大于实际需要。
2. **安装器装完点「启动」报 740**：Inno Setup 的 `[Run] ... postinstall` 条目默认以**启动安装器的原始用户（未提权）**身份 `CreateProcess`，而 `CreateProcess` 不像 `ShellExecute` 会触发 UAC，遇到 `requireAdministrator` 直接 `ERROR_ELEVATION_REQUIRED (740)`。
3. 1.0.1 的临时修法：安装器 `[Run]` 条目加 `runascurrentuser`，用安装器的提权 token 启动。**只解决安装完成页那一次启动**，日常双击仍旧弹 UAC。

## 二、当初为什么要求管理员

`app.manifest` 里的注释：

> 两个模块都要改系统配置：sshd 配置/密钥部署、Windows 服务安装，必须管理员权限。

拆开看，真正需要管理员的操作只有这几类，**都是用户点按钮才发生的低频操作，不是启动即需要**：

| 功能 | 位置 | 提权现状 |
| --- | --- | --- |
| FTPS / WebDAV 服务安装、卸载、重启 | `src/PocketNAS.PC/Views/FtpsView.xaml.cs` | **已按需提权**：`Elevation.RelaunchAsAdmin(args)`，非管理员时弹 UAC 带 `--ftps-install` 等参数重启自身 |
| sshd 一键配置（装 OpenSSH 功能、改 sshd_config、重启服务、放行防火墙） | `SshHelper/SshView.xaml.cs` → `RunPowershellCommand` | **无提权**，纯靠主程序自身的管理员 token |
| 部署公钥（`authorized_keys` / `administrators_authorized_keys`） | `SshHelper/SshView.xaml.cs` | **无提权**，直接写系统目录文件 |
| 界面配置读写 | 主程序 | 不需要管理员（安装版配置目录已授 `Users:(M)`） |

也就是说：FTPS 那一半已经是「按需提权」的正确写法，只是因为 manifest 要求管理员，`Elevation.IsRunningAsAdmin()` 永远为 true，那段代码成了死分支。**sshd 那一半是真正欠的账。**

## 三、要做的改造

### 3.1 manifest

- `src/PocketNAS.PC/app.manifest`：`requireAdministrator` → `asInvoker`
- `SshHelper/app.manifest`：同上（当前它作为 `SshView` 程序集被主程序加载，manifest 不生效；独立运行时会生效）

改完立刻收益：普通权限启动、无 UAC；FTPS 服务的安装/卸载/重启自动走已有 `RelaunchAsAdmin` 弹一次 UAC。

### 3.2 SshView 整条链路改为「一次提权执行整段脚本」

现状是逐条 `powershell -Command "..."` 同步执行（`UseShellExecute=false`，无法带 `Verb=RunAs`），且中间夹杂直接文件写入。`asInvoker` 之后这些操作会全部 `Access Denied`。

推荐做法：

1. 把「一键配置」涉及的全部动作（启用 OpenSSH 能力、写 `sshd_config`、生成/部署密钥、`Restart-Service sshd`、`New-NetFirewallRule`）拼成一个临时 `.ps1`。
2. 用 `ProcessStartInfo { FileName="powershell", Arguments="-NoProfile -ExecutionPolicy Bypass -File <tmp.ps1>", UseShellExecute=true, Verb="runas" }` 启动——**一次 UAC 跑完整个流程**，而不是每条命令弹一次。
3. 输出重定向在 `UseShellExecute=true` 下不可用：脚本内 `Start-Transcript` 或 `*> <tmp-out.txt>`，结束后读回文件追加进界面日志。
4. 用户取消 UAC（`Win32Exception 1223`）要单独识别，提示「已取消，未做任何修改」，不能当失败处理。
5. 脚本里每一步都要能单独报告成败，界面日志保持现在的分步外观（1/5、2/5 …）。

### 3.3 便携版（绿色版）目录可写性

改造后主程序以普通用户运行，**若便携版解压在 `Program Files` 等受保护目录，写配置文件会失败**（现在靠管理员 token 硬写，问题被掩盖了）。需要：

- 启动时检测程序目录是否可写，不可写时明确提示「请解压到用户目录，或用安装版」；
- 或配置统一落到 `%AppData%\PocketNAS`（与安装版 `%ProgramData%` 的合并方案一起排）。

### 3.4 顺带

- 服务账号仍为 `LocalSystem`（目标虚拟账户 `NT SERVICE\WebDAVService`）
- 数据目录仍散落在安装目录（目标 `%ProgramData%\PocketNAS`）
- `.iss` 版本号是手写常量，未与代码版本联动

以上三条与本条同批改更划算，因为都涉及「权限 + 目录基准」的同一片代码。

## 四、对两种发布形态的影响（改前 → 改后）

| 场景 | 现在（requireAdministrator） | 改造后（asInvoker + 按需提权） |
| --- | --- | --- |
| 安装版：装完点「启动」 | 1.0.0 报 740；1.0.1 靠 `runascurrentuser` 才通 | 直接通，无需 flag |
| 安装版：日常双击主程序 | 每次弹 UAC，全程高权限 | 不弹 UAC，普通权限 |
| 安装版：装/卸 FTPS 服务 | 不弹 UAC（已高权限） | 弹一次 UAC（走 `RelaunchAsAdmin`） |
| 安装版：sshd 一键配置 | 不弹 UAC（已高权限） | 弹一次 UAC（3.2 改造后） |
| **便携版：双击运行** | **每次都弹 UAC**，整个程序高权限 | **不弹 UAC**，普通权限直接开 |
| 便携版：解压在 Program Files | 靠管理员 token 能写配置 | 写不进 → 需 3.3 的可写性提示 |
| 便携版：装服务 | 不弹 UAC | 弹一次 UAC |
| 便携版 / 开发目录与安装版服务同名 | 卸载安全阀靠标记文件，已兼容 | 不受影响 |

**结论：改造对便携版是净收益**（现在是双击必弹 UAC），唯一要补的是「程序目录不可写」的提示。

## 五、验收清单

- [ ] 安装版装完直接点「启动」正常进界面，无 740、无 UAC
- [ ] 开始菜单双击主程序不弹 UAC
- [ ] 便携版解压到 `D:\tools\PocketNAS` 双击不弹 UAC，配置能保存，重启后仍在
- [ ] 便携版解压到 `C:\Program Files\xxx` 双击给出「目录不可写」明确提示，不是崩在保存那一步
- [ ] FTPS 服务安装：弹一次 UAC，装完服务指向正确目录并运行
- [ ] sshd 一键配置：弹一次 UAC，日志分步正常，取消 UAC 提示「已取消」且不留半成品
- [ ] 卸载：服务与数据按标记文件清理，不误删便携版/开发目录的同名服务
