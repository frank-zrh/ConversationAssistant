using System.Security.Cryptography;
using System.Xml.Linq;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Qwen;
using ConversationAssistant_App.UI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class QwenSettingsSaveTests
{
    private const string TestCredential = "sk-ws-fake-test-token.AB_+/-~==";

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Save_CompleteWorkspaceKey_VerifiesEncryptedSettingsAndRetainsKeyWhenOnlyModelChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qwen-save-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new ProtectedQwenSettingsStore(path);
            var result = Save(store, $"\r\nAuthorization:\t Bearer \"{TestCredential}\"\r\n");

            Assert.AreEqual("QwenSaved", result.Message.Key);
            Assert.IsNotNull(result.SavedSettings);
            Assert.AreEqual(TestCredential, result.SavedSettings.RequireApiKey());

            var update = Save(store, "", result.SavedSettings, "qwen-flash");
            Assert.AreEqual("QwenSaved", update.Message.Key);
            var reloaded = new ProtectedQwenSettingsStore(path).Load();
            Assert.AreEqual(TestCredential, reloaded.RequireApiKey());
            Assert.AreEqual("qwen-flash", reloaded.AnalysisModel);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Save_ExistingSettingsFileIsLocked_PreservesTheSavedKeyAndShowsAnError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qwen-locked-{Guid.NewGuid():N}.bin");
        const string oldKey = "sk-ws-previous-fake-test-token";
        try
        {
            var store = new ProtectedQwenSettingsStore(path);
            var initial = Save(store, oldKey);
            Assert.IsNotNull(initial.SavedSettings);

            QwenSettingsSave.Result result;
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                result = Save(store, TestCredential, initial.SavedSettings);

            Assert.IsNull(result.SavedSettings);
            // Windows can report access denied rather than a sharing violation during an atomic replace.
            CollectionAssert.Contains(new[] { "ErrorQwenSettingsAccess", "ErrorQwenSettingsLocked" }, result.Message.Key);
            Assert.Contains("关闭其他应用实例", result.Message.Resolve(UiText.For(ConversationLanguage.Chinese)));
            Assert.AreEqual(oldKey, new ProtectedQwenSettingsStore(path).Load().RequireApiKey());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow("sk-ws-abc...def", "ErrorQwenKeyInvalid")]
    [DataRow("sk-ws-aa=bb", "ErrorQwenKeyInvalid")]
    [DataRow("Authorization: Bearer \r\nsk-ws-invalid", "ErrorQwenKeyInvalid")]
    [DataRow("Authorization: Bearer sk-sp-not-a-workspace-key", "ErrorQwenTokenPlanKey")]
    public void Save_InvalidInput_DoesNotAttemptToWrite(string input, string expectedMessage)
    {
        var store = new TestStore();

        var result = Save(store, input);

        Assert.AreEqual(expectedMessage, result.Message.Key);
        Assert.IsNull(result.SavedSettings);
        Assert.AreEqual(0, store.SaveCalls);
    }

    [TestMethod]
    [DataRow(TestCredential, "ErrorQwenTokenPlanKeyInvalid")]
    [DataRow("sk-sp-", "ErrorQwenTokenPlanKeyInvalid")]
    [DataRow("sk-sp-ab****cd", "ErrorQwenTokenPlanKeyInvalid")]
    public void Save_TokenPlanRejectsInvalidReplacementEvenWithPayAsYouGoConfigured(string key, string expectedMessage)
    {
        var store = new TestStore();
        var current = new QwenSettings { ApiKey = TestCredential };

        var result = QwenSettingsSave.Save(store, current, "", current.SpeechModel, current.AnalysisModel,
            NullLogger.Instance, QwenBillingMode.TokenPlan, key);

        Assert.AreEqual(expectedMessage, result.Message.Key);
        Assert.IsNull(result.SavedSettings);
        Assert.AreEqual(0, store.SaveCalls);
    }

    [TestMethod]
    public void Save_TokenPlanOnlyDoesNotRequirePayAsYouGoKey()
    {
        var store = new QwenAnalysisTests.TestStore();

        var result = QwenSettingsSave.Save(store, new QwenSettings(), "", QwenModels.DefaultSpeech,
            QwenModels.DefaultAnalysis, NullLogger.Instance, QwenBillingMode.TokenPlan, QwenAnalysisTests.TokenPlanTestKey);

        Assert.AreEqual("QwenSaved", result.Message.Key);
        var saved = store.Load();
        Assert.AreEqual(QwenBillingMode.TokenPlan, saved.BillingMode);
        Assert.IsTrue(saved.HasAnalysisApiKey);
        Assert.IsFalse(saved.HasApiKey);
        Assert.AreEqual(QwenAnalysisTests.TokenPlanTestKey, saved.TokenPlanApiKey);
    }

    [TestMethod]
    public void Save_CanPersistTheSelectedProfileBeforeItsKeyIsConfigured()
    {
        var store = new QwenAnalysisTests.TestStore();
        var current = new QwenSettings { ApiKey = TestCredential };

        var result = QwenSettingsSave.Save(store, current, "", current.SpeechModel, current.AnalysisModel,
            NullLogger.Instance, QwenBillingMode.TokenPlan);

        Assert.AreEqual("QwenSavedWithoutKey", result.Message.Key);
        var saved = store.Load();
        Assert.AreEqual(QwenBillingMode.TokenPlan, saved.BillingMode);
        Assert.IsFalse(saved.HasAnalysisApiKey);
        Assert.AreEqual(TestCredential, saved.ApiKey);
    }

    [TestMethod]
    public void AutosaveRunsOnceThreeMinutesAfterTheLastEditAndWaitsForBusyWorkspace()
    {
        var clock = new AutosaveClock();
        var autosave = new QwenAutoSave(clock);
        Assert.IsFalse(autosave.TryBegin(canSave: true));
        autosave.Edited();
        clock.Advance(TimeSpan.FromMinutes(2));
        autosave.Edited();
        clock.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromTicks(1));
        Assert.IsFalse(autosave.TryBegin(canSave: true));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.IsFalse(autosave.TryBegin(canSave: false));
        Assert.IsTrue(autosave.TryBegin(canSave: true));
        Assert.IsFalse(autosave.TryBegin(canSave: true));
    }

    [TestMethod]
    public void ManualSaveCancelsThePendingAutosaveUntilAnotherEdit()
    {
        var clock = new AutosaveClock();
        var autosave = new QwenAutoSave(clock);
        autosave.Edited();
        clock.Advance(TimeSpan.FromMinutes(2));
        autosave.Cancel();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsFalse(autosave.TryBegin(canSave: true));
        autosave.Edited();
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.IsTrue(autosave.TryBegin(canSave: true));
    }

    [TestMethod]
    public void Save_ChangingBillingModeWithoutRetypingKeysKeepsBothConfigurations()
    {
        var store = new QwenAnalysisTests.TestStore();
        var current = QwenSettings.FromInput(TestCredential, QwenModels.AlternateSpeech, "qwen-max",
            QwenBillingMode.TokenPlan, QwenAnalysisTests.TokenPlanTestKey, "qwen3.8-max", QwenModels.TokenPlanAsrFlash);

        var result = QwenSettingsSave.Save(store, current, "", current.SpeechModel, current.AnalysisModel,
            NullLogger.Instance, QwenBillingMode.PayAsYouGo, tokenPlanAnalysisModel: current.TokenPlanAnalysisModel,
            tokenPlanSpeechModel: current.TokenPlanSpeechModel);

        Assert.AreEqual("QwenSaved", result.Message.Key);
        var saved = store.Load();
        Assert.AreEqual(QwenBillingMode.PayAsYouGo, saved.BillingMode);
        Assert.AreEqual(TestCredential, saved.ApiKey);
        Assert.AreEqual(QwenAnalysisTests.TokenPlanTestKey, saved.TokenPlanApiKey);
        Assert.AreEqual("qwen-max", saved.AnalysisModel);
        Assert.AreEqual("qwen3.8-max", saved.TokenPlanAnalysisModel);
        Assert.AreEqual(QwenModels.AlternateSpeech, saved.SpeechModel);
        Assert.AreEqual(QwenModels.TokenPlanAsrFlash, saved.TokenPlanSpeechModel);
    }

    [TestMethod]
    public void Save_RejectsPayAsYouGoSpeechModelInTokenPlanProfile()
    {
        var store = new TestStore();
        var current = new QwenSettings { TokenPlanApiKey = QwenAnalysisTests.TokenPlanTestKey };
        var result = QwenSettingsSave.Save(store, current, "", current.SpeechModel, current.AnalysisModel,
            NullLogger.Instance, QwenBillingMode.TokenPlan, tokenPlanSpeechModel: QwenModels.DefaultSpeech);

        Assert.AreEqual("ErrorQwenModelInvalid", result.Message.Key);
        Assert.AreEqual(0, store.SaveCalls);
    }

    [TestMethod]
    [DataRow("ErrorQwenSettingsAccess", unchecked((int)0x80070005))]
    [DataRow("ErrorQwenSettingsDiskFull", unchecked((int)0x80070070))]
    [DataRow("ErrorQwenSettingsLocked", unchecked((int)0x80070020))]
    [DataRow("ErrorQwenSettingsEncrypt", unchecked((int)0x8009000B))]
    public void Save_WriteFails_ReportsSafeLocalizedReasonAndCode(string expectedMessage, int code)
    {
        var error = expectedMessage switch
        {
            "ErrorQwenSettingsAccess" => (Exception)new UnauthorizedAccessException(TestCredential),
            "ErrorQwenSettingsEncrypt" => new CryptographicException(code),
            _ => new IOException(TestCredential, code)
        };
        var store = new TestStore { WriteError = error };

        var result = Save(store, TestCredential);

        Assert.IsNull(result.SavedSettings);
        Assert.AreEqual(expectedMessage, result.Message.Key);
        var text = result.Message.Resolve(UiText.For(ConversationLanguage.Chinese));
        Assert.Contains($"0x{code:X8}", text);
        Assert.DoesNotContain(TestCredential, text);
        Assert.AreEqual(0, store.LoadCalls);
    }

    [TestMethod]
    public void Save_ReadBackFails_ReportsVerificationFailureInsteadOfSuccess()
    {
        var store = new TestStore { ReadError = new InvalidDataException(TestCredential) };

        var result = Save(store, TestCredential);

        Assert.AreEqual("ErrorQwenSettingsVerify", result.Message.Key);
        Assert.IsNull(result.SavedSettings);
        Assert.AreEqual(1, store.SaveCalls);
        Assert.AreEqual(1, store.LoadCalls);
        Assert.DoesNotContain(TestCredential, result.Message.Resolve(UiText.For(ConversationLanguage.Chinese)));
    }

    [TestMethod]
    public void Save_CannotReadBackExpectedKey_DoesNotReportSuccess()
    {
        var store = new TestStore { ReadResult = new QwenSettings { ApiKey = "sk-ws-old-test-key" } };

        var result = Save(store, TestCredential);

        Assert.AreEqual("ErrorQwenSettingsVerifyMismatch", result.Message.Key);
        Assert.IsNull(result.SavedSettings);
        Assert.AreEqual(1, store.SaveCalls);
        Assert.AreEqual(1, store.LoadCalls);
    }

    [TestMethod]
    public void QwenSaveFeedback_IsNotHiddenInsideTheScrollableSettings()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var flyout = page.Descendants().Single(element => (string?)element.Attribute(x + "Key") == "SettingsFlyout");
        var feedback = flyout.Descendants().Single(element => (string?)element.Attribute("Message") == "{Binding QwenSettingsStatus}");

        Assert.AreEqual("InfoBar", feedback.Name.LocalName);
        Assert.AreEqual("{Binding QwenSettingsStatusSeverity}", (string?)feedback.Attribute("Severity"));
        Assert.IsFalse(feedback.Ancestors().Any(element => element.Name.LocalName == "ScrollViewer"));
    }

    private static QwenSettingsSave.Result Save(IQwenSettingsStore store, string input,
        QwenSettings? current = null, string analysisModel = QwenModels.DefaultAnalysis) =>
        QwenSettingsSave.Save(store, current ?? new QwenSettings(), input, QwenModels.DefaultSpeech, analysisModel, NullLogger.Instance);

    private sealed class AutosaveClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class TestStore : IQwenSettingsStore
    {
        public Exception? WriteError { get; init; }
        public Exception? ReadError { get; init; }
        public QwenSettings? ReadResult { get; init; }
        public int SaveCalls { get; private set; }
        public int LoadCalls { get; private set; }

        public void Save(QwenSettings settings)
        {
            SaveCalls++;
            if (WriteError is not null) throw WriteError;
        }

        public QwenSettings Load()
        {
            LoadCalls++;
            if (ReadError is not null) throw ReadError;
            return ReadResult ?? new QwenSettings();
        }
    }
}