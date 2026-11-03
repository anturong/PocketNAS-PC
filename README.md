# PocketNAS-PC

PocketNAS 的 Windows 桌面端合集：把原先分散的 WebDAV 管理器和 SSH 配置助手合并成一个程序，并预留 FTPS / DLNA 模块位。

## 模块

| # | 模块 | 状态 | 来源 |
|---|------|------|------|
| ① | WebDAV | 已接入 | easy_webdav（v1.2.4） |
| ② | SSH | 已接入 | SshHelper（v1.04） |
| ③ | FTPS | 预留，仅占位无实现 | — |
| ④ | DLNA | 预留，仅占位无实现 | — |

## 界面

统一为「左导航 · 中内容 · 右日志」三栏，日志全局共享一份。

WebDAV 模块：

![WebDAV](docs/shots/webdav.png)

SSH 模块：

![SSH](docs/shots/ssh.png)

## 目录结构

```
PocketNAS-PC/
├── src/
│   ├── PocketNAS.PC/          # 外壳：窗口、导航、全局日志渲染
│   └── PocketNAS.Shared/      # 共享层：LogService
├── easy_webdav/               # ① WebDAV 模块（完整历史）
│   ├── src/WebDAVService/            # 服务端
│   └── src/WebDAVConfigurator/       # WebDavView（UserControl）
├── SshHelper/                 # ② SSH 模块（完整历史）→ SshView
├── docs/shots/                # 界面截图
└── PocketNAS-PC.sln
```

两个来源仓库以 `git subtree` 方式并入，各自保留完整提交历史。

## 构建与运行

```bash
dotnet build PocketNAS-PC.sln -c Release
dotnet run --project src/PocketNAS.PC
```

界面自渲染快照（调试用，无参数则正常启动）：

```bash
PocketNAS-PC.exe --shot out.png --module=ssh
```

## 关键实现说明

**日志**：各模块只调 `LogService.Write(模块名, 文本, 级别)`，外壳的右侧面板统一渲染，切换模块不清空。

**提权**：WebDAV 的安装/卸载/重启服务需要管理员权限。外壳接管了 `--install / --uninstall / --restart` 参数，
以 `runas` 重启自身执行，不再依赖独立的 WebDAVConfigurator.exe。

**二维码**：两个模块都有，但都只出现在弹窗里，主界面不留含凭据的图像。

**密码**：WebDAV 用户密码默认掩码显示，右侧眼睛按钮切换明文/掩码，旁边「随机」按钮生成
16 位强密码（大小写 + 数字 + 符号，排除 0/O、1/l/I 等易混字符，Fisher-Yates 洗牌避免位置规律）。

**部署注意**：WebDAV 的「安装服务」会在程序目录下查找 `WebDAVService.exe`，
发布时需把它一并放到 `PocketNAS-PC.exe` 同目录。

## 与上游同步

```bash
git subtree pull --prefix=easy_webdav ew master --squash
git subtree pull --prefix=SshHelper   ssh master --squash
```

> `ew` / `ssh` 两个 remote 只用于拉取：它们的 push 地址已被设为 `PUSH-DISABLED`，
> 这是刻意的——原仓库不做任何回写，误执行 push 只会报错，不会污染上游。

> 注意：并入后对两个子目录内部做了结构性改造（窗口拆为 UserControl、输出类型改 Library），
> 直接 subtree pull 上游会冲突，需要按改造后的结构手工合并。

## 首次推送到新仓库

在 GitHub 新建空仓库后（不要勾选初始化 README），执行：

```bash
cd D:/RAID_NAS备份/x/PocketNAS-PC
git remote add origin https://github.com/<你的账号>/<新仓库名>.git
git push -u origin master
```

推送前若要瘦身，可先剔除 SshHelper 历史里 62.5MB 的 `publish.zip`
（会改写 commit sha，届时 `ew` / `ssh` 的同步基线需要重新建立）。


## 已知待办

- 状态胶囊固定显示「就绪」，尚未接真实的 sshd / WebDAV 服务状态。
- 原 `easy_webdav/WebDAVService.sln` 仍存在，其中 WebDAVConfigurator 已变为类库。
