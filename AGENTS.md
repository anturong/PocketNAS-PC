# AGENTS.md · 项目记忆

> 这份文件记录**改代码之前必须先知道的约定、硬约束和踩过的坑**。
> README 讲「是什么、怎么跑」，这里讲「怎么改才不闯祸」。
> 每次改动约定或踩到新坑，同步更新本文件（与本地工作记忆保持一致）。

---

## 1. 硬约束

| 约束 | 说明 |
|---|---|
| **FTPS 不动** | 现状即定稿。不要提议重构 FTPS、不要把它换成 HTTP、不要"顺手优化"。曾做过 HTTP 与 FTP 的取流对比，结论仅作参考，不构成改造理由。 |
| **一个功能一个 commit** | 每个 commit 只做一件事、能独立编译；完成即推送 `origin/master`。格式见 README「提交约定」。 |
| **源仓库只读** | `ew` / `ssh` 两个 remote 的 push 地址是 `PUSH-DISABLED`，只能 fetch / subtree pull。原仓库 `easy_webdav`、`SshHelper` 不允许改动。 |
| **程序需要管理员权限** | `app.manifest` 是 `requireAdministrator`（SSH 写 `administrators_authorized_keys`、安装服务都需要）。因此 `--shot` 自截图从普通会话启动会被拒，界面验证需人工配合。 |
| **不要删 `.workbuddy`** | 项目数据目录，非临时缓存。 |

---

## 2. 构建与运行

```bash
dotnet build PocketNAS-PC.sln -c Debug
```

**服务在运行时构建会失败**（`service\WebDAVService.exe` / `service\ftps\FtpsService.exe` 被占用）：

```bash
dotnet build PocketNAS-PC.sln -c Debug -p:SkipWebDavService=true -p:SkipFtpsService=true
```

产物：`src/PocketNAS.PC/bin/<Config>/net8.0-windows/PocketNAS-PC.exe`，两个服务分布在同目录 `service\` 下。

**Debug / Release 配置各自独立**（相对程序位置解析），但 Windows 服务全局唯一，指向最后一次「安装服务」时所在目录。详见 README 对应章节。

---

## 3. 架构约定

- **分层**：`src/PocketNAS.PC` 只放 UI；协议实现放各自类库（`PocketNAS.Ftps`、`easy_webdav/src/*`、`SshHelper`）；服务是独立 exe（Generic Host + `UseWindowsService` + Serilog）。
- **配置**：程序目录存在 `service\` 时读写 `service\` 下那份，与服务端一致；相对路径一律**相对配置文件所在目录**解析，保证整个目录可搬移。
- **服务管理**：统一走 `PocketNAS.Shared.WindowsServiceHelper`（sc create/delete/start/stop + 状态等待），各模块只留 `OnInstalled` / `OnUninstalling` 钩子。
- **日志**：所有模块写 `PocketNAS.Shared.LogService`，由外壳右侧面板统一渲染，切换模块不清空；服务端写 Serilog 到 `logs\service.log`。
- **IP 选择**：统一 `LanAddresses`，跳过回环 / APIPA / 未启用网卡，按「有网关 > 以太网/WLAN > 其他 > 网卡名」排序，显示 `IP · 网卡名`，`ComboBoxItem.Tag` 存纯 IP。
- **绑定地址与展示地址必须分开**：WebDAV 用 `host="+"`，FTPS 用 `listen_ip=0.0.0.0` + `advertise_ip`。下拉只列真实网卡，**不要**把「0.0.0.0 / 全部网卡」混进下拉——那是绑定概念，界面不暴露。

---

## 4. 已知的坑（都真实踩过）

| 坑 | 症状 | 正确做法 |
|---|---|---|
| Button 模板换成 `TextBlock` | 文字正常了，但 Path 图标（眼睛）整个消失 | 保留 `ContentPresenter`，在模板内用 Style 覆盖内部 `TextBlock.Foreground` |
| MSBuild 传相对 `PublishDir` | 产物落到子项目的 `bin` 下，外壳目录仍是空的 | 先 `GetFullPath` 转绝对路径再传给 `MSBuild` |
| 界面与服务各读一份配置 | 改了端口保存后服务不生效（8443 连不上） | 统一到 `service\` 下那份，启动日志打印实际配置文件路径 |
| 合并时丢了 `app.manifest` | sshd 配置全报「需要提升」、写 `administrators_authorized_keys` 拒绝访问 | 外壳 manifest 必须是 `requireAdministrator` |
| `UseWindowsForms` 引入歧义 | `MessageBox` / `Brushes` / `UserControl` 全部冲突 | 加 using 别名 |
| `--shot` 在管理员清单下失效 | `Permission denied` | 临时编不带清单的版本，或让人工截图 |

---

## 5. FTPS 的现状与边界（不要动）

- 定位是**文件通道**：手机走 21 显式 `AUTH TLS`，VLC 走 990 隐式。二维码给手机的是 `port: 21` + `tls: "explicit"`。
- 已修的三个安全洞：未认证可 `DELE`/`MKD`（现 530）、路径可逃出根目录（现根目录包含校验）、明文传密码（现强制 `AUTH TLS`）。
- 只读账号已拦截 `STOR` / `DELE` / `MKD`；`APPE` / `RNFR` / `RNTO` 尚未实现，将来补时记得加只读判断。
- 代码里的 `ABOR` 无条件回 226、`RETR` 中断也回 226、`CWD` 指向文件回 250，是**为 VLC 做的兼容**，不是 bug，不要"清理"。

---

## 6. 模块状态

| 模块 | 状态 | 备注 |
|---|---|---|
| ① WebDAV | ✅ | 服务 + 界面，配置/端口/二维码/密码掩码均可用 |
| ② SSH | ✅ | 服务配置 + 密钥 + 扫码导入 |
| ③ FTPS | ✅ | 21 显式 / 990 隐式，服务 + 界面 + 二维码 |
| ④ DLNA | ⬜ 未开工 | 方向存档 `docs/dlna-plan.md`，**开工前必须先回答那 4 个问题** |

---

## 7. 开工前的自检

改任何模块前先问三句：

1. 这个改动**属于哪个模块**、commit 前缀对不对？
2. 有没有**第二处**需要同步（配置路径、防火墙、二维码载荷、README）？
3. 改完之后**能不能真的跑起来**——服务在跑要加跳过参数，界面要人点一遍 UAC。

改完必做：`0 警告 0 错误` → 单独 commit → `git push` → 确认远端 sha 与本地一致。
