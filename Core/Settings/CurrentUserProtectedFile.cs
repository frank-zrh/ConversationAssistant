using System.Security.Cryptography;
using System.Text;

namespace ConversationAssistant.Core.Settings;

public sealed class CurrentUserProtectedFile(string filePath, string purpose)
{
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes(purpose);

    public byte[]? Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!File.Exists(filePath)) return null;
        if (new FileInfo(filePath).Length > 65_536)
            throw new InvalidDataException("Encrypted app configuration is too large.");
        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(filePath), _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("无法解密当前用户的配置。请使用原 Windows 用户，或重新登录/保存设置。");
        }
    }

    public void Write(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var encrypted = ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(encrypted);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
