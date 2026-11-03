# PocketNAS-PC

PocketNAS 的 Windows 桌面端合集，把原先分散的两个工具合并为一个程序，并预留后续模块位。

## 模块

| # | 模块 | 状态 | 来源 |
|---|------|------|------|
| ① | WebDAV | 已实现 | easy_webdav |
| ② | SSH | 已实现 | SshHelper |
| ③ | FTPS | 占位 | — |
| ④ | DLNA | 占位 | — |

## 目录结构

```
PocketNAS-PC/
├── easy_webdav/           # ① WebDAV 模块（完整历史）
│   ├── src/WebDAVService/         # 服务端
│   └── src/WebDAVConfigurator/    # 配置器 UI
├── SshHelper/             # ② SSH 模块（完整历史）
├── PocketNAS-PC.sln       # 统一解决方案入口
└── .gitignore
```

两个来源仓库以 `git subtree` 方式并入，各自保留完整提交历史。

## 构建

```bash
dotnet build PocketNAS-PC.sln -c Release
```

## 与上游同步

```bash
git subtree pull --prefix=easy_webdav ew master --squash
git subtree pull --prefix=SshHelper   ssh master --squash
```
