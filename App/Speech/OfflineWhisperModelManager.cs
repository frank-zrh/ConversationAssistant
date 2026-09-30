using System.Security.Cryptography;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant_App.Speech;

public sealed class OfflineWhisperModelManager
{
    private const long ModelBytes = 487601967;
    private const string ModelSha1 = "55356645c2b361a969dfd0ef2c5a50d530afd8d5";

    public string ModelPath => Path.Combine(AppContext.BaseDirectory, "Models", "ggml-small.bin");

    public IReadOnlyList<ConversationLanguage> AvailableLanguages() =>
        File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelBytes
            ? [ConversationLanguage.Chinese, ConversationLanguage.English]
            : [];

    public string Validate()
    {
        var path = ModelPath;
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Offline Whisper model is missing. Rebuild or reinstall Conversation Assistant with Models\\ggml-small.bin.", path);
        if (new FileInfo(path).Length != ModelBytes)
            throw new InvalidDataException("Offline Whisper model has an unexpected size; reinstall the model.");
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        if (hash != ModelSha1)
            throw new InvalidDataException("Offline Whisper model failed its integrity check; reinstall the model.");
        return path;
    }
}
