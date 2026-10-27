using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant_App.UI;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class ConversationHistoryTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize() => _directory = Path.Combine(Path.GetTempPath(), $"conversation-history-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestMethod]
    public async Task MissingHistoryIsEmptyAndDoesNotCreateStorage()
    {
        var result = await new JsonConversationArchiveStore(_directory).ListAsync();
        Assert.IsEmpty(result.Entries);
        Assert.AreEqual(0, result.UnavailableCount);
        Assert.IsFalse(Directory.Exists(_directory));
    }

    [TestMethod]
    public async Task ListsEverySavedRecordNewestFirstWithoutModifyingFiles()
    {
        var store = new JsonConversationArchiveStore(_directory);
        var start = DateTimeOffset.Parse("2026-04-03T09:17:23+08:00", CultureInfo.InvariantCulture);
        var sessions = Enumerable.Range(0, 32).Select(index => Session(start.AddMinutes(index))).ToArray();
        foreach (var session in sessions.Reverse()) await store.SaveAsync(ConversationArchive.Capture(session));
        var original = await File.ReadAllBytesAsync(store.GetPath(sessions[0].SessionId));
        var result = await store.ListAsync();
        CollectionAssert.AreEqual(sessions.Reverse().Select(item => item.SessionId).ToArray(),
            result.Entries.Select(item => item.SessionId).ToArray());
        Assert.AreEqual(0, result.UnavailableCount);
        var entry = result.Entries.Single(item => item.SessionId == sessions[0].SessionId);
        Assert.AreEqual(start, entry.StartTime);
        Assert.AreEqual(start.AddSeconds(47), entry.EndTime);
        Assert.AreEqual("", entry.Title);
        Assert.AreEqual(store.GetPath(entry.SessionId), entry.Path);
        Assert.AreEqual(entry.SessionId, (await store.LoadAsync(entry.Path)).SessionId);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(entry.Path));
        Assert.HasCount(32, Directory.GetFiles(_directory));
    }

    [TestMethod]
    public async Task TitlesUseFirstNonemptyTranscriptLineWithoutChangingArchives()
    {
        var store = new JsonConversationArchiveStore(_directory);
        var session = Session(DateTimeOffset.UtcNow.AddHours(-1));
        session.TranscriptSegments.AddRange([
            new TranscriptSegment(Guid.NewGuid(), session.StartTime, session.StartTime.AddSeconds(1), " \r\n ", "Speech", true),
            new TranscriptSegment(Guid.NewGuid(), session.StartTime.AddSeconds(2), session.StartTime.AddSeconds(3),
                "\r\n  确认部署范围与预算  \r\nFurther discussion", "Speech", true),
            new TranscriptSegment(Guid.NewGuid(), session.StartTime.AddSeconds(4), session.StartTime.AddSeconds(5),
                "Later transcript", "Speech", true)
        ]);
        var path = await store.SaveAsync(ConversationArchive.Capture(session));
        var original = await File.ReadAllBytesAsync(path);
        Assert.AreEqual("确认部署范围与预算", (await store.ListAsync()).Entries.Single().Title);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task UsesLatestCompletedNonemptySavedIntentRegardlessOfAnalysisOrder()
    {
        var store = new JsonConversationArchiveStore(_directory);
        var session = Session(DateTimeOffset.UtcNow.AddHours(-1));
        session.Analyses.AddRange([
            Analysis(session.StartTime.AddSeconds(20), QuestionStatus.Completed, "确认部署范围与预算。"),
            Analysis(session.StartTime.AddSeconds(10), QuestionStatus.Completed, "Older intent"),
            Analysis(session.StartTime.AddSeconds(30), QuestionStatus.Failed, "Failed analysis must not replace intent"),
            Analysis(session.StartTime.AddSeconds(40), QuestionStatus.Completed, " ")
        ]);
        await store.SaveAsync(ConversationArchive.Capture(session));
        Assert.AreEqual("确认部署范围与预算。", (await store.ListAsync()).Entries.Single().IntentSummary);
    }

    [TestMethod]
    public async Task LegacyRecordsWithoutIntentStillAppear()
    {
        var store = new JsonConversationArchiveStore(_directory);
        var session = Session(DateTimeOffset.UtcNow.AddHours(-1));
        session.Analyses.Add(Analysis(session.StartTime.AddSeconds(10), QuestionStatus.Completed, "Old summary"));
        var path = await store.SaveAsync(ConversationArchive.Capture(session));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["analyses"]![0]!.AsObject().Remove("intentSummary");
        await File.WriteAllTextAsync(path, document.ToJsonString());
        var result = await store.ListAsync();
        Assert.HasCount(1, result.Entries);
        Assert.AreEqual("", result.Entries.Single().IntentSummary);
    }

    [TestMethod]
    public async Task InvalidRecordsAreReportedWithoutBlockingValidHistoryOrReadingTemporaryFiles()
    {
        var store = new JsonConversationArchiveStore(_directory);
        var path = await store.SaveAsync(ConversationArchive.Capture(Session(DateTimeOffset.UtcNow.AddHours(-1))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "broken.json"), "{");
        await File.WriteAllTextAsync(Path.Combine(_directory, "invalid.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_directory, "writing.tmp"), "{");
        await File.WriteAllTextAsync(Path.Combine(_directory, "session.workiq.jsonl"), "{}");
        var result = await store.ListAsync();
        Assert.HasCount(1, result.Entries);
        Assert.AreEqual(path, result.Entries.Single().Path);
        Assert.AreEqual(2, result.UnavailableCount);
        Assert.AreEqual("{", await File.ReadAllTextAsync(Path.Combine(_directory, "broken.json")));
    }

    [TestMethod]
    public async Task LockedArchiveDoesNotBlockOtherRecords()
    {
        var store = new JsonConversationArchiveStore(_directory);
        var lockedPath = await store.SaveAsync(ConversationArchive.Capture(Session(DateTimeOffset.UtcNow.AddHours(-2))));
        var readablePath = await store.SaveAsync(ConversationArchive.Capture(Session(DateTimeOffset.UtcNow.AddHours(-1))));
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await store.ListAsync();
        Assert.AreEqual(readablePath, result.Entries.Single().Path);
        Assert.AreEqual(1, result.UnavailableCount);
    }

    [TestMethod]
    public async Task CancelledListingDoesNotCreateOrModifyStorage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new JsonConversationArchiveStore(_directory).ListAsync(cancellation.Token));
        Assert.IsFalse(Directory.Exists(_directory));
    }

    [TestMethod]
    [DataRow(ConversationLanguage.English)]
    [DataRow(ConversationLanguage.Chinese)]
    public void RowsShowSeparateMinuteLevelLocalTimesAndSavedIntent(ConversationLanguage language)
    {
        var start = DateTimeOffset.Parse("2026-04-03T09:17:23+08:00", CultureInfo.InvariantCulture);
        var end = start.AddMinutes(7).AddSeconds(19);
        var texts = UiText.For(language);
        var entry = new ConversationHistoryEntry(Guid.NewGuid(), "record.json", start, end, "Confirm deployment scope")
        {
            Title = "Deployment planning"
        };
        var row = new ConversationHistoryRow(entry, texts);
        Assert.AreEqual(start.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), row.StartTimeText);
        Assert.AreEqual(end.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), row.EndTimeText);
        Assert.AreEqual("Confirm deployment scope", row.IntentSummary);
        Assert.IsFalse(row.AutomationLabel.Contains(entry.Title, StringComparison.Ordinal));
        StringAssert.Contains(row.AutomationLabel, texts.Format("HistoryStartedAt", row.StartTimeText));
        StringAssert.Contains(row.AutomationLabel, texts.Format("HistoryEndedAt", row.EndTimeText));
        StringAssert.Contains(row.AutomationLabel, row.IntentSummary);
    }

    [TestMethod]
    public void SeparateTimestampCellsKeepMinutePrecision()
    {
        var start = new DateTimeOffset(new DateTime(2026, 10, 26, 14, 0, 57, DateTimeKind.Local));
        var row = new ConversationHistoryRow(new(Guid.NewGuid(), "record.json", start,
            start.AddMinutes(90).AddSeconds(2), ""), UiText.For(ConversationLanguage.Chinese));
        Assert.AreEqual("2026-10-26 14:00", row.StartTimeText);
        Assert.AreEqual("2026-10-26 15:30", row.EndTimeText);
    }

    [TestMethod]
    public void SeparateTimestampCellsIncludeDatesWhenCrossingMidnight()
    {
        var start = new DateTimeOffset(new DateTime(2026, 10, 26, 23, 59, 55, DateTimeKind.Local));
        var row = new ConversationHistoryRow(new(Guid.NewGuid(), "record.json", start,
            start.AddMinutes(2), ""), UiText.For(ConversationLanguage.English));
        Assert.AreEqual("2026-10-26 23:59", row.StartTimeText);
        Assert.AreEqual("2026-10-27 00:01", row.EndTimeText);
    }

    [TestMethod]
    public void ChineseTabAndColumnLabelsMatchTheRequestedWording()
    {
        var texts = UiText.For(ConversationLanguage.Chinese);
        Assert.AreEqual("当前会话", texts["LiveTranscript"]);
        Assert.AreEqual("历史记录", texts["HistoryTitle"]);
        Assert.AreEqual("开始时间", texts["HistoryColumnStartTime"]);
        Assert.AreEqual("结束时间", texts["HistoryColumnEndTime"]);
        Assert.AreEqual("意图", texts["HistoryIntent"]);
    }

    [TestMethod]
    public void RowFallbacksUpdateWhenDisplayLanguageChanges()
    {
        var english = UiText.For(ConversationLanguage.English);
        var chinese = UiText.For(ConversationLanguage.Chinese);
        var row = new ConversationHistoryRow(new(Guid.NewGuid(), "record.json", DateTimeOffset.UtcNow, null, ""), english);
        Assert.AreEqual(english["HistoryNoIntent"], row.IntentSummary);
        Assert.AreEqual(english["HistoryEndNotSaved"], row.EndTimeText);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        row.UpdateLanguage(chinese);
        Assert.AreEqual(chinese["HistoryNoIntent"], row.IntentSummary);
        Assert.AreEqual(chinese["HistoryEndNotSaved"], row.EndTimeText);
        CollectionAssert.Contains(changed, nameof(ConversationHistoryRow.AutomationLabel));
        CollectionAssert.Contains(changed, nameof(ConversationHistoryRow.StartTimeText));
        CollectionAssert.Contains(changed, nameof(ConversationHistoryRow.EndTimeText));
    }

    [TestMethod]
    public void LeftPaneHasFixedLiveAndHistoryTabsWithoutLiveImport()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        var tabs = Named("TranscriptTabs");
        var live = Named("LiveTranscriptTab");
        var history = Named("HistoryTab");
        Assert.AreEqual("False", (string?)tabs.Attribute("IsAddTabButtonVisible"));
        Assert.HasCount(2, tabs.Elements().Where(element => element.Name.LocalName == "TabViewItem"));
        Assert.AreEqual("False", (string?)live.Attribute("IsClosable"));
        Assert.AreEqual("False", (string?)history.Attribute("IsClosable"));
        Assert.IsTrue(Named("TranscriptScroll").Ancestors().Contains(live));
        Assert.IsTrue(Named("SaveRecordMenuItem").Ancestors().Contains(live));
        Assert.IsFalse(live.Descendants().Any(element =>
            (string?)element.Attribute("Click") == "ImportConversation_Click" ||
            (string?)element.Attribute(x + "Name") == "ImportConversationButton"));
        var list = Named("ConversationHistoryList");
        Assert.IsTrue(list.Ancestors().Contains(history));
        Assert.AreEqual("{Binding ConversationHistory}", (string?)list.Attribute("ItemsSource"));
        Assert.AreEqual("{Binding CanImportConversation}", (string?)list.Attribute("IsItemClickEnabled"));
        Assert.AreEqual("HistoryRecord_Click", (string?)list.Attribute("ItemClick"));
        Assert.IsFalse(list.Ancestors().Any(element => element.Name.LocalName == "ScrollViewer"));
        Assert.AreEqual("Disabled", (string?)list.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.AreEqual("Disabled", (string?)list.Attribute("ScrollViewer.HorizontalScrollMode"));
        Assert.AreEqual("Stretch", (string?)list.Attribute("HorizontalContentAlignment"));
        var header = Named("HistoryColumnsHeader");
        var row = list.Elements().Single(element => element.Name.LocalName == "ListView.ItemTemplate")
            .Descendants().Single(element => element.Name.LocalName == "DataTemplate").Elements().Single();
        Assert.AreEqual("Grid", row.Name.LocalName);
        string[] Widths(XElement grid) => grid.Elements().Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements().Select(element => (string)element.Attribute("Width")!).ToArray();
        CollectionAssert.AreEqual(new[] { "2*", "2*", "6*" }, Widths(header));
        CollectionAssert.AreEqual(Widths(header), Widths(row));
        Assert.IsNull(header.Attribute("MinWidth"));
        Assert.IsNull(row.Attribute("MinWidth"));
        CollectionAssert.AreEqual(new[] { "{Binding Texts[HistoryColumnStartTime]}", "{Binding Texts[HistoryColumnEndTime]}",
            "{Binding Texts[HistoryIntent]}" }, header.Elements().Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => (string)element.Attribute("Text")!).ToArray());
        var cells = row.Elements().Where(element => element.Name.LocalName == "TextBlock").ToArray();
        CollectionAssert.AreEqual(new[] { "{Binding StartTimeText}", "{Binding EndTimeText}", "{Binding IntentSummary}" },
            cells.Select(element => (string)element.Attribute("Text")!).ToArray());
        Assert.AreEqual("Wrap", (string?)cells[0].Attribute("TextWrapping"));
        Assert.AreEqual("Wrap", (string?)cells[1].Attribute("TextWrapping"));
        Assert.IsNull(cells[0].Attribute("Grid.Column"));
        Assert.AreEqual("1", (string?)cells[1].Attribute("Grid.Column"));
        Assert.AreEqual("2", (string?)cells[2].Attribute("Grid.Column"));
    }

    private static ConversationSession Session(DateTimeOffset start)
    {
        var session = new ConversationSession { Settings = new ConversationSettings() };
        Set(session, nameof(ConversationSession.StartTime), start);
        Set(session, nameof(ConversationSession.EndTime), start.AddSeconds(47));
        return session;
    }

    private static ConversationAnalysisRequest Analysis(DateTimeOffset time, QuestionStatus status, string intent)
    {
        var analysis = new ConversationAnalysisRequest
        {
            Timestamp = time, Trigger = ConversationAnalysisTrigger.Manual, IsManual = true,
            Transcript = [], ExistingQuestions = []
        };
        Set(analysis, nameof(WorkIqRequest.Status), status);
        Set(analysis, nameof(ConversationAnalysisRequest.IntentSummary), intent);
        return analysis;
    }

    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);
}
