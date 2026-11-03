# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Debug build
dotnet build

# Release single-file self-contained (preferred deployment)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish

# Run (must be elevated — app manifest requires administrator)
dotnet run
```

The app requires **administrator privileges** (see `app.manifest` → `requireAdministrator`) because it manages the `sshd` Windows service and writes to `C:\ProgramData\ssh\`.

## Project Structure

Single-window WPF app — all logic in `MainWindow.xaml.cs`:

| File | Purpose |
|---|---|
| `MainWindow.xaml` | Two-column dark theme layout — left: buttons + RichTextBox console, right: connection info + key management + QR placeholder |
| `MainWindow.xaml.cs` | All logic — OpenSSH config, ACL, key generation, clipboard, connection testing |
| `App.xaml` / `App.xaml.cs` | Entry point, sets StartupUri to MainWindow.xaml |
| `app.manifest` | Requires admin elevation (`requireAdministrator`) |

## Architecture

Plain WPF code-behind, no MVVM, no DI.

**Pattern**: UI event handlers dispatch `Task.Run()` to background threads, disable/enable buttons around the work, use `Dispatcher.Invoke` for thread-safe logging via `AppendLog()`.

**Clipboard** (key pain point): All clipboard operations use raw Win32 `user32.dll` P/Invoke (`OpenClipboard`/`SetClipboardData`/`EmptyClipboard`/`CloseClipboard`) instead of WPF's `Clipboard.SetText`, because elevated (admin) processes hit `CLIPBRD_E_CANT_OPEN` with the WPF API. Includes 10-retry with 200ms delay. Two formats: `CF_UNICODETEXT` (13) for text and `CF_HDROP` (0xF) with `DROPFILES` struct for file drops.

**Permissions**: File ACLs set via `System.Security.AccessControl` with inheritance disabled. `TryGrantWriteAccess()` temporarily adds Write permission before overwriting files that were previously locked down to Read-only. All ACL operations are wrapped in try/catch for graceful degradation.

**External processes**: `Process.Start()` used for `ssh-keygen`, `ssh`, and `powershell`. PowerShell scripts use `-NoProfile` for clean execution.

**sshd auto-start**: The app runs `sc.exe config sshd start= auto` via PowerShell to ensure sshd starts on boot (Windows `sc` requires a space after `=`). This is done both before starting the service and as a safety check when the service is already running.

## Right Panel Buttons

| Button | Color | Action |
|--------|-------|--------|
| 👁 显示/隐藏私钥文本 | `#58a6ff` blue | Toggle private key text visibility |
| 📋 复制私钥文本 | `#3fb950` green | Win32 clipboard (`CF_UNICODETEXT`) — private key content |
| 📋 复制私钥文件 | `#d29922` gold | Win32 clipboard (`CF_HDROP`) — private key file for Explorer paste |
| 🔄 更换密钥 | `#ff7b72` red | Full key rotation flow |

## Key Behaviors

- **Administrators group quirk**: Windows OpenSSH `sshd_config` redirects admin users to `C:\ProgramData\ssh\administrators_authorized_keys` instead of `~/.ssh/authorized_keys`. App writes to **both** and detects this in `sshd_config` at runtime. Admin file ACL: no inheritance, only `SYSTEM` + `BUILTIN\Administrators` Read.
- **Key rotation flow**: `BackupKeys()` → `GenerateNewKeys()` → `DeployPublicKey()` → `TestSshConnection()` → `CopyNewPrivateKey()`. Backup creates `{exe_dir}/bak/yyyyMMdd_HHmmss.zip` of the entire `~/.ssh/` directory. `DeployPublicKey` uses `TryGrantWriteAccess` before writing to bypass prior Read-only ACL. Test runs `FindSsh()` + `ssh -o StrictHostKeyChecking=accept-new -o ConnectTimeout=5 -o BatchMode=yes -i {key} user@127.0.0.1 exit`.
- **sshd_config detection**: Reads `%ProgramFiles%/OpenSSH/sshd_config` or `%ProgramData%/ssh/sshd_config`, logs a warning if `administrators_authorized_keys` redirection is configured.

## Dependencies

- .NET 8.0-windows
- NuGet: `System.ServiceProcess.ServiceController` (for sshd service control)
- Built-in: `System.IO.Compression` (ZipFile for backups), `System.Runtime.InteropServices` (Win32 P/Invoke)
