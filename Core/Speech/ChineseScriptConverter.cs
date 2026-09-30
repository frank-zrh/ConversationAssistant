using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ConversationAssistant.Core.Speech;

public static class ChineseScriptConverter
{
    private const uint SimplifiedChinese = 0x02000000;

    public static string ToSimplified(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return text;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var length = LCMapStringEx("zh-CN", SimplifiedChinese, text, text.Length,
            null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (length == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var buffer = new char[length];
        var written = LCMapStringEx("zh-CN", SimplifiedChinese, text, text.Length,
            buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (written == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new string(buffer, 0, written);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int LCMapStringEx(string localeName, uint mapFlags,
        string source, int sourceLength, [Out] char[]? destination, int destinationLength,
        IntPtr versionInformation, IntPtr reserved, IntPtr sortHandle);
}
