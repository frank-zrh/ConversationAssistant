using ConversationAssistant_App.Speech;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class OfflineSpeakerModelTests
{
    [TestMethod]
    public void MissingModelsFailWithExplicitOfflineProvisioningStepsWithoutDownloading()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, $"unprovisioned-speakers-{Guid.NewGuid():N}");
        var manager = new OfflineSpeakerModelManager(directory);
        var error = Assert.ThrowsExactly<FileNotFoundException>(() => manager.Validate());
        StringAssert.Contains(error.Message, "Install-OfflineSpeakerModels.ps1");
        StringAssert.Contains(error.Message, "Listening never downloads");
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void UnexpectedModelSizeIsRejectedBeforeNativeCodeRuns()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, $"invalid-speakers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, OfflineSpeakerModelManager.SegmentationFileName), [1, 2, 3]);
            var error = Assert.ThrowsExactly<InvalidDataException>(() => new OfflineSpeakerModelManager(directory).Validate());
            StringAssert.Contains(error.Message, "unexpected size");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void CorrectSizeButCorruptModelFailsCryptographicValidation()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, $"corrupt-speakers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using (var stream = File.Create(Path.Combine(directory, OfflineSpeakerModelManager.SegmentationFileName)))
                stream.SetLength(OfflineSpeakerModelManager.SegmentationBytes);
            var error = Assert.ThrowsExactly<InvalidDataException>(() => new OfflineSpeakerModelManager(directory).Validate());
            StringAssert.Contains(error.Message, "integrity check");
        }
        finally { Directory.Delete(directory, true); }
    }
}
