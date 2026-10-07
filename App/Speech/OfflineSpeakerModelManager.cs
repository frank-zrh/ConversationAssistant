using System.Security.Cryptography;

namespace ConversationAssistant_App.Speech;

public sealed record OfflineSpeakerModelPaths(string Segmentation, string Embedding);

public sealed class OfflineSpeakerModelManager(string? modelDirectory = null)
{
    public const string SegmentationFileName = "pyannote-segmentation-3.0.onnx";
    public const string EmbeddingFileName = "3dspeaker-eres2net.onnx";
    public const long SegmentationBytes = 5992913;
    public const long EmbeddingBytes = 39593761;
    public const string SegmentationSha256 = "220AD67CA923BEF2FA91F2390C786097BF305BCEB5E261D4AF67B38E938E1079";
    public const string EmbeddingSha256 = "1A331345F04805BADBB495C775A6DDFFCDD1A732567D5EC8B3D5749E3C7A5E4B";

    public string ModelDirectory { get; } = modelDirectory ??
        Path.Combine(AppContext.BaseDirectory, "Models", "Speakers");

    public OfflineSpeakerModelPaths Validate()
    {
        var segmentation = Path.Combine(ModelDirectory, SegmentationFileName);
        var embedding = Path.Combine(ModelDirectory, EmbeddingFileName);
        ValidateFile(segmentation, SegmentationBytes, SegmentationSha256);
        ValidateFile(embedding, EmbeddingBytes, EmbeddingSha256);
        return new OfflineSpeakerModelPaths(segmentation, embedding);
    }

    private static void ValidateFile(string path, long size, string expectedHash)
    {
        const string setup = "Run App\\Speech\\Install-OfflineSpeakerModels.ps1, then rebuild or reinstall with Models\\Speakers. Listening never downloads models.";
        if (!File.Exists(path))
            throw new FileNotFoundException($"Offline speaker model is missing. {setup}", path);
        if (new FileInfo(path).Length != size)
            throw new InvalidDataException($"Offline speaker model has an unexpected size. {setup}");
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Offline speaker model failed its integrity check. {setup}");
    }
}
