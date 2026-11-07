using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PocketNAS.Shared;

/// <summary>
/// 往剪贴板写纯文本。
///
/// 用 Win32 API 而不是 <c>System.Windows.Clipboard</c>：本类库不引用 WPF，
/// 且 <c>Clipboard.SetText</c> 在剪贴板被别的进程短暂占用时直接抛异常。
/// 这里重试 10 次（每次 200ms），失败返回 false，由调用方决定怎么提示。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;

    /// <summary>写文本到剪贴板；成功返回 true，剪贴板一直被占用返回 false。</summary>
    public static bool Set(string text)
    {
        if (text == null) return false;

        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var ptr = Marshal.StringToHGlobalUni(text);
                    if (SetClipboardData(CF_UNICODETEXT, ptr) == IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(ptr);
                        return false;
                    }
                    return true; // 剪贴板接管了内存，不要 Free
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(200);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
}
