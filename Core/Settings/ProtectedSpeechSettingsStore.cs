using System.Security.Cryptography;
using System.Text.Json;

namespace ConversationAssistant.Core.Settings;

public interface ISpeechSettingsStore
{
    SpeechServiceConfiguration Load();
    void Save(SpeechServiceConfiguration configuration);
}

public sealed class ProtectedSpeechSettingsStore(string filePath) : ISpeechSettingsStore
{
    private readonly object _gate = new();
    // Keep the original DPAPI purpose so existing settings remain readable after the rename.
    private readonly CurrentUserProtectedFile _file = new(filePath, "MeetingCopilot.AzureSpeech.Settings.v1");

    public SpeechServiceConfiguration Load()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        lock (_gate)
        {
            var plaintext = _file.Read();
            if (plaintext is null) return new SpeechServiceConfiguration();
            try
            {
                var configuration = JsonSerializer.Deserialize<SpeechServiceConfiguration>(plaintext)
                    ?? throw new InvalidDataException("Speech 配置无效，请重新填写并保存。");
                if (!Enum.IsDefined(configuration.Provider) || configuration.ServiceUri is null ||
                    configuration.TenantId is null || configuration.ClientId is null)
                    throw new InvalidDataException("Speech 配置无效，请重新填写并保存。");
                return configuration;
            }
            catch (JsonException)
            {
                throw new InvalidDataException("Speech 配置格式无效，请重新填写并保存。");
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    public void Save(SpeechServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Enum.IsDefined(configuration.Provider)) throw new ArgumentOutOfRangeException(nameof(configuration));
        if (configuration.Provider == SpeechProvider.AzureSpeech) configuration.ValidateForAzure();
        lock (_gate)
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(configuration);
            try { _file.Write(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }
}
