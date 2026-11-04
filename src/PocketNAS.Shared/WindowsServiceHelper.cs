using System.Diagnostics;
using System.ServiceProcess;

namespace PocketNAS.Shared;

/// <summary>
/// 通用的 Windows 服务管理：用系统自带 sc.exe 注册/删除，不依赖 nssm 等外部工具。
/// WebDAV 与 FTPS 两个模块共用，模块特有的善后动作（防火墙、证书绑定）通过重写钩子实现。
/// </summary>
public class WindowsServiceHelper
{
    public string ServiceName { get; }
    public string DisplayName { get; }
    public string Description { get; }

    /// <summary>服务主程序文件名，例如 WebDAVService.exe</summary>
    public string ExeFileName { get; }

    public WindowsServiceHelper(string serviceName, string displayName, string description, string exeFileName)
    {
        ServiceName = serviceName;
        DisplayName = displayName;
        Description = description;
        ExeFileName = exeFileName;
    }

    /// <summary>注册完成、启动之前的额外动作（放行防火墙端口等）</summary>
    protected virtual void OnInstalled() { }

    /// <summary>卸载之前的额外动作（清理证书绑定等）</summary>
    protected virtual void OnUninstalling() { }

    public void InstallService(string baseDir)
    {
        var exePath = Path.Combine(baseDir, ExeFileName);
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"服务程序未找到: {exePath}");

        StopService();
        DeleteService();
        Thread.Sleep(2000);

        try { Process.Start("taskkill", $"/f /im {ExeFileName}")?.WaitForExit(); } catch { }
        Thread.Sleep(1000);

        RunSc($"create {ServiceName} binPath= \"{exePath}\" start= auto DisplayName= \"{DisplayName}\"");
        RunSc($"description {ServiceName} \"{Description}\"");
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");

        OnInstalled();
        StartService();
    }

    public void UninstallService()
    {
        OnUninstalling();
        StopService();
        Thread.Sleep(3000);
        DeleteService();
    }

    public void RestartService()
    {
        try { Process.Start("taskkill", $"/f /im {ExeFileName}")?.WaitForExit(); } catch { }
        Thread.Sleep(1000);
        StopService();
        Thread.Sleep(3000);
        StartService();
    }

    public bool IsServiceRunning()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    public bool WaitForRunning(int timeoutMs = 15000)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(timeoutMs));
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    protected void StartService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
        }
        catch (Exception ex) { throw new Exception($"启动服务失败: {ex.Message}"); }
    }

    protected void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception ex) { throw new Exception($"停止服务失败: {ex.Message}"); }
    }

    protected void DeleteService()
    {
        try { RunSc($"delete {ServiceName}"); }
        catch { }
    }

    private static void RunSc(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(10000);
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            if (!string.IsNullOrEmpty(error) &&
                !error.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"sc.exe 失败 (code={process.ExitCode}): {error}");
        }
    }
}
