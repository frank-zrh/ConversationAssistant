using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class ConversationArchiveStoreTests
{
    private string _directory = null!;

    [TestInitialize]
    public void CreateOwnedDirectory()
    {
        _directory = Path.Combine(Directory.GetCurrentDirectory(), $".conversation-archive-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void RemoveOwnedDirectory()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task RoundTripPreservesAllHistoryAndSemanticIdsInReadableUnicodeJson()
    {
        var original = FullSession();
        var store = new JsonConversationArchiveStore(_directory);
        var path = await store.SaveAsync(ConversationArchive.Capture(original));
        var json = await File.ReadAllTextAsync(path);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.AreEqual((byte)'{', bytes[0]);
        StringAssert.Contains(json, "\n");
        StringAssert.Contains(json, "需求确认");
        StringAssert.Contains(json, "\"version\": 1");
        StringAssert.Contains(json, "\"trigger\": \"Group\"");
        foreach (var excluded in new[]
        {
            "cloud-conversation-secret", "microphone-secret", "output-device-secret",
            "workIqConversationId", "inputDeviceId", "outputDeviceId", "explicitRequest",
            "audioCapture", "accessToken", "refreshToken"
        })
            Assert.IsFalse(json.Contains(excluded, StringComparison.OrdinalIgnoreCase), excluded);

        var archive = await store.LoadAsync(path);
        Assert.AreEqual(original.SessionId, archive.SessionId);
        Assert.AreEqual(archive.SessionId, archive.Id);
        Assert.AreEqual(ConversationArchive.CurrentVersion, archive.Version);
        var restored = archive.Restore();
        Assert.AreNotSame(original, restored);
        Assert.AreEqual(original.SessionId, restored.SessionId);
        Assert.AreEqual(original.StartTime, restored.StartTime);
        Assert.AreEqual(original.EndTime, restored.EndTime);
        Assert.AreEqual(original.Settings.Language, restored.Settings.Language);
        Assert.AreEqual(original.Settings.AnswerLanguage, restored.Settings.AnswerLanguage);
        Assert.AreEqual(original.Settings.AnswerStyle, restored.Settings.AnswerStyle);
        Assert.AreEqual(original.Settings.ContextWindowDuration, restored.Settings.ContextWindowDuration);
        Assert.AreEqual(original.Settings.MaxContextCharacters, restored.Settings.MaxContextCharacters);
        Assert.IsFalse(restored.Settings.AutomaticAnalysis);
        Assert.IsNull(restored.WorkIqConversationId);
        Assert.IsNull(restored.Settings.InputDeviceId);
        Assert.IsNull(restored.Settings.OutputDeviceId);
        Assert.AreEqual(original.NextGroupNumber, restored.NextGroupNumber);
        CollectionAssert.AreEqual(original.TranscriptSegments, restored.TranscriptSegments);
        Assert.AreNotSame(original.TranscriptSegments[0], restored.TranscriptSegments[0]);
        Assert.AreEqual(2, restored.TranscriptSegments[0].SpeakerNumber);
        Assert.IsNull(restored.TranscriptSegments[1].SpeakerNumber);

        Assert.HasCount(2, restored.Groups);
        for (var index = 0; index < original.Groups.Count; index++)
        {
            var expected = original.Groups[index];
            var actual = restored.Groups[index];
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Number, actual.Number);
            Assert.AreEqual(expected.Name, actual.Name);
            Assert.AreEqual(expected.Timestamp, actual.Timestamp);
            Assert.AreEqual(expected.IsExpanded, actual.IsExpanded);
            CollectionAssert.AreEqual(expected.TranscriptIds.ToArray(), actual.TranscriptIds.ToArray());
        }
        Assert.HasCount(2, restored.Analyses);
        Assert.AreSame(restored.Analyses[1], restored.Analysis);
        for (var index = 0; index < original.Analyses.Count; index++)
        {
            var expected = original.Analyses[index];
            var actual = restored.Analyses[index];
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Timestamp, actual.Timestamp);
            Assert.AreEqual(expected.Trigger, actual.Trigger);
            Assert.AreEqual(expected.GuidanceVersion, actual.GuidanceVersion);
            Assert.AreEqual(expected.IsManual, actual.IsManual);
            Assert.AreEqual(expected.Status, actual.Status);
            Assert.AreEqual(expected.Error, actual.Error);
            Assert.AreEqual(expected.GroupId, actual.GroupId);
            Assert.AreEqual(expected.GroupNumber, actual.GroupNumber);
            Assert.AreEqual(expected.GroupName, actual.GroupName);
            Assert.AreEqual(expected.AddedQuestionCount, actual.AddedQuestionCount);
            Assert.AreEqual(expected.Response, actual.Response);
            CollectionAssert.AreEqual(expected.Transcript.ToArray(), actual.Transcript.ToArray());
            CollectionAssert.AreEqual(expected.ExistingQuestions.ToArray(), actual.ExistingQuestions.ToArray());
            CollectionAssert.AreEqual(expected.Questions.Select(item => item.Id).ToArray(), actual.Questions.Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(expected.Sources.ToArray(), actual.Sources.ToArray());
        }
        Assert.AreSame(restored.SuggestedQuestions[0], restored.Analyses[0].Questions[0]);
        Assert.AreSame(restored.Analyses[0].Questions[0], restored.Analyses[1].Questions[0]);
        Assert.AreEqual(original.SuggestedQuestions[0].Reason, restored.SuggestedQuestions[0].Reason);
        Assert.AreEqual(original.SuggestedQuestions[0].ContextUsed, restored.SuggestedQuestions[0].ContextUsed);
        Assert.AreEqual(original.SuggestedQuestions[0].Timestamp, restored.SuggestedQuestions[0].Timestamp);
        Assert.HasCount(2, restored.Answers);
        for (var index = 0; index < original.Answers.Count; index++)
        {
            var expected = original.Answers[index];
            var actual = restored.Answers[index];
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Timestamp, actual.Timestamp);
            Assert.AreEqual(expected.IsManual, actual.IsManual);
            Assert.AreEqual(expected.Status, actual.Status);
            Assert.AreEqual(expected.Error, actual.Error);
            Assert.AreEqual(expected.Question, actual.Question);
            Assert.AreEqual(expected.ContextUsed, actual.ContextUsed);
            Assert.AreEqual(expected.TranscriptSegmentId, actual.TranscriptSegmentId);
            Assert.AreEqual(expected.SuggestedQuestionId, actual.SuggestedQuestionId);
            Assert.AreEqual(expected.Answer, actual.Answer);
            CollectionAssert.AreEqual(expected.Sources.ToArray(), actual.Sources.ToArray());
        }

        var expectedDocument = JsonNode.Parse(json)!;
        expectedDocument["settings"]!["automaticAnalysis"] = false;
        await store.SaveAsync(ConversationArchive.Capture(restored));
        var actualDocument = JsonNode.Parse(await File.ReadAllTextAsync(path));
        Assert.IsTrue(JsonNode.DeepEquals(expectedDocument, actualDocument));
    }

    [TestMethod]
    public async Task CaptureAndEveryRestoreOwnTheirMutableCollectionsAndSettings()
    {
        var original = FullSession();
        var snapshot = ConversationArchive.Capture(original);
        var expectedGroupId = original.Groups[0].Id;
        var expectedTranscriptId = original.Groups[0].TranscriptIds[0];
        var expectedResponse = original.Analyses[0].Response;
        original.Settings.AnswerLanguage = ConversationLanguage.Auto;
        Set(original.Groups[0], nameof(TranscriptGroup.Name), "Changed");
        ((Guid[])original.Groups[0].TranscriptIds)[0] = Guid.NewGuid();
        ((string[])original.Analyses[0].Sources)[0] = "changed-source";
        ((string[])original.Analyses[0].ExistingQuestions)[0] = "changed-question";
        Set(original.Analyses[0], nameof(ConversationAnalysisRequest.Response), "Changed");
        Set(original.Answers[0], nameof(QuestionRequest.Answer), "Changed");
        original.TranscriptSegments.Clear();
        original.Groups.Clear();
        original.Analyses.Clear();
        original.SuggestedQuestions.Clear();
        original.Answers.Clear();

        var store = new JsonConversationArchiveStore(_directory);
        var restored = (await store.LoadAsync(await store.SaveAsync(snapshot))).Restore();
        Assert.HasCount(4, restored.TranscriptSegments);
        Assert.HasCount(2, restored.Analyses);
        Assert.HasCount(2, restored.Answers);
        Assert.AreEqual(ConversationLanguage.English, restored.Settings.AnswerLanguage);
        Assert.AreEqual(expectedGroupId, restored.Groups[0].Id);
        Assert.AreEqual(expectedTranscriptId, restored.Groups[0].TranscriptIds[0]);
        Assert.AreEqual("需求确认", restored.Groups[0].Name);
        Assert.AreEqual(expectedResponse, restored.Analyses[0].Response);
        Assert.AreEqual("https://example.test/doc/第一章", restored.Analyses[0].Sources[0]);
        Assert.AreEqual("Earlier need?", restored.Analyses[0].ExistingQuestions[0]);
        Assert.AreEqual("答复：保留全部历史。", restored.Answers[0].Answer);

        restored.Settings.AnswerLanguage = ConversationLanguage.Auto;
        ((Guid[])restored.Groups[0].TranscriptIds)[0] = Guid.NewGuid();
        ((string[])restored.Analyses[0].Sources)[0] = "changed-again";
        restored.Analyses.Clear();
        var anotherRestore = snapshot.Restore();
        Assert.HasCount(2, anotherRestore.Analyses);
        Assert.AreEqual(ConversationLanguage.English, anotherRestore.Settings.AnswerLanguage);
        Assert.AreEqual(expectedTranscriptId, anotherRestore.Groups[0].TranscriptIds[0]);
        Assert.AreEqual("https://example.test/doc/第一章", anotherRestore.Analyses[0].Sources[0]);
        Assert.AreNotSame(restored.SuggestedQuestions[0], anotherRestore.SuggestedQuestions[0]);
    }

    [TestMethod]
    public async Task ClearedCurrentAnalysisKeepsStandaloneSnapshotsAndResolvableHistoricalSuggestions()
    {
        var session = FullSession();
        Set<ConversationAnalysisRequest?>(session, nameof(ConversationSession.Analysis), null);
        session.TranscriptSegments.Clear();
        session.Groups.Clear();
        session.SuggestedQuestions.Clear();
        var store = new JsonConversationArchiveStore(_directory);
        var restored = (await store.LoadAsync(await store.SaveAsync(ConversationArchive.Capture(session)))).Restore();
        Assert.IsNull(restored.Analysis);
        Assert.IsEmpty(restored.TranscriptSegments);
        Assert.IsEmpty(restored.Groups);
        Assert.HasCount(2, restored.Analyses);
        Assert.HasCount(2, restored.Analyses[0].Transcript);
        Assert.HasCount(2, restored.SuggestedQuestions);
        Assert.AreEqual(restored.SuggestedQuestions[0].Id, restored.Answers[0].SuggestedQuestionId);
        Assert.AreSame(restored.SuggestedQuestions[0], restored.Analyses[0].Questions[0]);
        Assert.IsNotNull(restored.Analyses[0].GroupId);
    }

    [TestMethod]
    public async Task InFlightImportBecomesEndedOfflineAndNeverMutatesTheArchiveSnapshot()
    {
        var session = FullSession();
        Set<DateTimeOffset?>(session, nameof(ConversationSession.EndTime), null);
        Set(session.Analyses[0], nameof(WorkIqRequest.Status), QuestionStatus.Pending);
        Set(session.Analyses[1], nameof(WorkIqRequest.Status), QuestionStatus.Processing);
        Set(session.Answers[0], nameof(WorkIqRequest.Status), QuestionStatus.Processing);
        Set(session.Answers[1], nameof(WorkIqRequest.Status), QuestionStatus.Pending);
        var latest = session.StartTime.AddDays(1);
        session.TranscriptSegments.Add(new TranscriptSegment(Guid.NewGuid(), latest.AddSeconds(-1), latest,
            "Last local content", "Speech", true));
        var store = new JsonConversationArchiveStore(_directory);
        var path = await store.SaveAsync(ConversationArchive.Capture(session));
        var originalBytes = await File.ReadAllBytesAsync(path);
        var archive = await store.LoadAsync(path);
        var restored = archive.Restore();
        Assert.AreEqual(latest, restored.EndTime);
        Assert.IsFalse(restored.Settings.AutomaticAnalysis);
        Assert.IsNull(restored.WorkIqConversationId);
        foreach (var request in restored.Analyses.Cast<WorkIqRequest>().Concat(restored.Answers))
        {
            Assert.AreEqual(QuestionStatus.Cancelled, request.Status);
            StringAssert.Contains(request.Error!, ConversationArchive.InterruptedRequestDiagnostic);
        }
        StringAssert.Contains(restored.Answers[1].Error!, "Offline answer failure");
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        await store.SaveAsync(archive);
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        File.Delete(path);
        Assert.AreEqual(latest, archive.Restore().EndTime);
        Assert.AreEqual(latest, archive.Fork().Restore().EndTime);
        Assert.IsEmpty(Directory.GetFiles(_directory));
    }

    [TestMethod]
    public async Task CancellingImportedRequestPreservesMaximumLengthErrorAndCanBeSavedAgain()
    {
        var session = FullSession();
        var originalError = new string('x', ConversationArchiveLimits.MaximumTextLength);
        Set(session.Answers[0], nameof(WorkIqRequest.Status), QuestionStatus.Processing);
        Set(session.Answers[0], nameof(WorkIqRequest.Error), originalError);
        var store = new JsonConversationArchiveStore(_directory);
        var restored = ConversationArchive.Capture(session).Restore();
        Assert.AreEqual(ConversationArchive.InterruptedRequestDiagnostic + "\n" + originalError, restored.Answers[0].Error);
        var path = await store.SaveAsync(ConversationArchive.Capture(restored));
        Assert.AreEqual(restored.Answers[0].Error, (await store.LoadAsync(path)).Restore().Answers[0].Error);
    }

    [TestMethod]
    public async Task AcousticTimestampsOutsideSessionLifetimeRemainValidIndependentSnapshots()
    {
        var session = FullSession();
        session.TranscriptSegments[0] = session.TranscriptSegments[0] with
        {
            TimestampStart = session.StartTime.AddDays(-1),
            TimestampEnd = session.StartTime.AddDays(-1).AddSeconds(1)
        };
        var historicalTranscript = (TranscriptSegment[])session.Analyses[0].Transcript;
        historicalTranscript[0] = historicalTranscript[0] with
        {
            TimestampStart = session.EndTime!.Value.AddDays(1),
            TimestampEnd = session.EndTime.Value.AddDays(1).AddSeconds(1)
        };
        var store = new JsonConversationArchiveStore(_directory);
        var path = await store.SaveAsync(ConversationArchive.Capture(session));
        var restored = (await store.LoadAsync(path)).Restore();
        Assert.AreEqual(session.EndTime, restored.EndTime);
        Assert.AreEqual(session.TranscriptSegments[0], restored.TranscriptSegments[0]);
        Assert.AreEqual(historicalTranscript[0], restored.Analyses[0].Transcript[0]);
    }

    [TestMethod]
    public async Task NewSessionsAndForkedImportsUseIsolatedGuidFilesWithoutChangingTheSource()
    {
        var sourceStore = new JsonConversationArchiveStore(Path.Combine(_directory, "source"));
        var source = ConversationArchive.Capture(FullSession());
        var sourcePath = await sourceStore.SaveAsync(source);
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        var localStore = new JsonConversationArchiveStore(Path.Combine(_directory, "local"));
        var loaded = await localStore.LoadAsync(sourcePath);
        var copy = loaded.Fork();
        Assert.AreNotEqual(source.SessionId, copy.SessionId);
        Assert.AreEqual(loaded.Restore().EndTime, copy.Restore().EndTime);
        CollectionAssert.AreEqual(loaded.Restore().Analyses.Select(item => item.Id).ToArray(),
            copy.Restore().Analyses.Select(item => item.Id).ToArray());
        var copyPath = await localStore.SaveAsync(copy);
        var next = ConversationArchive.Capture(FullSession());
        var nextPath = await localStore.SaveAsync(next);
        Assert.AreNotEqual(copyPath, nextPath);
        Assert.AreEqual($"{copy.SessionId:N}.json", Path.GetFileName(copyPath));
        Assert.AreEqual(copyPath, localStore.GetPath(copy.SessionId));
        Assert.HasCount(2, Directory.GetFiles(Path.Combine(_directory, "local"), "*.json"));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.ThrowsExactly<ArgumentException>(() => localStore.GetPath(Guid.Empty));
    }

    [TestMethod]
    public async Task ForkKeepsClosedStateAndAllIndependentHistoryAfterClear()
    {
        var session = FullSession();
        var historicalOnlySuggestion = new SuggestedQuestion
        {
            Timestamp = session.StartTime.AddMinutes(5),
            Question = "Historical suggestion outside the current analysis?",
            Reason = "Retain every prior suggestion.",
            ContextUsed = "Historical context"
        };
        session.SuggestedQuestions.Add(historicalOnlySuggestion);
        Set<ConversationAnalysisRequest?>(session, nameof(ConversationSession.Analysis), null);
        session.Groups.Clear();
        session.TranscriptSegments.Clear();
        var source = ConversationArchive.Capture(session);
        var fork = source.Fork();
        var original = source.Restore();
        var restored = fork.Restore();
        Assert.AreNotEqual(source.SessionId, fork.SessionId);
        Assert.AreEqual(session.EndTime, restored.EndTime);
        Assert.IsNotNull(restored.EndTime);
        Assert.IsNull(restored.Analysis);
        Assert.HasCount(2, restored.Analyses);
        Assert.HasCount(3, restored.SuggestedQuestions);
        Assert.AreEqual(historicalOnlySuggestion.Id, restored.SuggestedQuestions[2].Id);
        Assert.AreEqual(session.Analyses[0].GroupId, restored.Analyses[0].GroupId);
        Assert.AreEqual(session.Answers[1].TranscriptSegmentId, restored.Answers[1].TranscriptSegmentId);
        Assert.AreNotSame(original.Settings, restored.Settings);
        Assert.AreNotSame(original.Analyses[0], restored.Analyses[0]);
        Assert.AreNotSame(original.Analyses[0].Transcript[0], restored.Analyses[0].Transcript[0]);
        Assert.AreNotSame(original.Analyses[0].Questions, restored.Analyses[0].Questions);
        Assert.AreNotSame(original.SuggestedQuestions[0], restored.SuggestedQuestions[0]);
        Assert.AreNotSame(original.Answers[0], restored.Answers[0]);
        ((string[])restored.Analyses[0].Sources)[0] = "Changed fork source";
        Set(restored.Answers[0], nameof(QuestionRequest.Answer), "Changed fork answer");
        restored.Analyses.RemoveAt(1);
        restored.SuggestedQuestions.RemoveAt(2);
        var store = new JsonConversationArchiveStore(_directory);
        await store.SaveAsync(ConversationArchive.Capture(restored));
        var sourceAgain = source.Restore();
        Assert.HasCount(2, sourceAgain.Analyses);
        Assert.HasCount(3, sourceAgain.SuggestedQuestions);
        Assert.AreEqual(original.Analyses[0].Sources[0], sourceAgain.Analyses[0].Sources[0]);
        Assert.AreEqual(original.Answers[0].Answer, sourceAgain.Answers[0].Answer);
        Assert.HasCount(2, fork.Restore().Analyses);
        Assert.HasCount(3, fork.Restore().SuggestedQuestions);
        Assert.IsFalse(File.Exists(store.GetPath(source.SessionId)));
    }

    [TestMethod]
    public async Task UnknownFieldsCannotIntroducePathsDevicesAuthenticationOrRuntimeTypes()
    {
        var sourceStore = new JsonConversationArchiveStore(Path.Combine(_directory, "source"));
        var path = await sourceStore.SaveAsync(ConversationArchive.Capture(FullSession()));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["path"] = Path.Combine(_directory, "external.json");
        json["$type"] = "System.Diagnostics.Process, System.Diagnostics.Process";
        json["workIqConversationId"] = "untrusted-cloud-secret";
        json["accessToken"] = "untrusted-access-secret";
        json["refreshToken"] = "untrusted-refresh-secret";
        json["settings"]!["inputDeviceId"] = "untrusted-input-secret";
        json["settings"]!["outputDeviceId"] = "untrusted-output-secret";
        json["settings"]!["automaticAnalysis"] = true;
        json["audio"] = new JsonArray(1, 2, 3);
        json["embeddings"] = new JsonArray(0.25, 0.5);
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var sourceBytes = await File.ReadAllBytesAsync(path);
        var store = new JsonConversationArchiveStore(Path.Combine(_directory, "local"));
        var archive = (await store.LoadAsync(path)).Fork();
        var restored = archive.Restore();
        Assert.IsNull(restored.WorkIqConversationId);
        Assert.IsNull(restored.Settings.InputDeviceId);
        Assert.IsNull(restored.Settings.OutputDeviceId);
        Assert.IsFalse(restored.Settings.AutomaticAnalysis);
        var localPath = await store.SaveAsync(archive);
        Assert.AreEqual(Path.Combine(_directory, "local", $"{archive.SessionId:N}.json"), localPath);
        var saved = await File.ReadAllTextAsync(localPath);
        Assert.IsFalse(saved.Contains("untrusted-", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("$type", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "external.json")));
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("session-id")]
    [DataRow("duplicate-record-id")]
    [DataRow("duplicate-transcript-id")]
    [DataRow("duplicate-group-number")]
    [DataRow("enum")]
    [DataRow("auto-language")]
    [DataRow("numeric-enum")]
    [DataRow("trigger")]
    [DataRow("timestamp")]
    [DataRow("end-before-start")]
    [DataRow("transcript-time")]
    [DataRow("null-list")]
    [DataRow("null-item")]
    [DataRow("null-settings")]
    [DataRow("null-text")]
    [DataRow("missing-field")]
    [DataRow("long-text")]
    [DataRow("long-source")]
    [DataRow("long-group-name")]
    [DataRow("empty-group-name")]
    [DataRow("group-membership")]
    [DataRow("unknown-group-transcript")]
    [DataRow("historical-only-group-transcript")]
    [DataRow("group-analysis-membership")]
    [DataRow("group-analysis-number")]
    [DataRow("group-analysis-trigger")]
    [DataRow("group-metadata")]
    [DataRow("next-group-number")]
    [DataRow("unknown-current-analysis")]
    [DataRow("unknown-analysis-question")]
    [DataRow("unknown-answer-question")]
    [DataRow("transcript-link-to-answer")]
    [DataRow("empty-transcript-link")]
    [DataRow("invalid-speaker")]
    [DataRow("invalid-question-count")]
    [DataRow("invalid-guidance-version")]
    [DataRow("invalid-context-window")]
    [DataRow("zero-context-window")]
    [DataRow("invalid-context-limit")]
    [DataRow("too-many-items")]
    public async Task InvalidArchivesAreRejectedWithoutChangingAnyExistingRecord(string invalidCase)
    {
        var store = new JsonConversationArchiveStore(_directory);
        var path = await store.SaveAsync(ConversationArchive.Capture(FullSession()));
        var bytes = await File.ReadAllBytesAsync(path);
        var json = JsonNode.Parse(bytes)!;
        switch (invalidCase)
        {
            case "version": json["version"] = 99; break;
            case "session-id": json["sessionId"] = Guid.Empty; break;
            case "duplicate-record-id": json["answers"]![0]!["id"] = json["analyses"]![0]!["id"]!.DeepClone(); break;
            case "duplicate-transcript-id": json["transcriptSegments"]![1]!["id"] = json["transcriptSegments"]![0]!["id"]!.DeepClone(); break;
            case "duplicate-group-number": json["groups"]![1]!["number"] = json["groups"]![0]!["number"]!.DeepClone(); break;
            case "enum": json["settings"]!["language"] = "Klingon"; break;
            case "auto-language": json["settings"]!["language"] = "Auto"; break;
            case "numeric-enum": json["settings"]!["language"] = 1; break;
            case "trigger": json["analyses"]![0]!["trigger"] = "NewTrigger"; break;
            case "timestamp": json["startTime"] = "not-a-date"; break;
            case "end-before-start": json["endTime"] = "2000-01-01T00:00:00Z"; break;
            case "transcript-time": json["transcriptSegments"]![0]!["timestampEnd"] = "2000-01-01T00:00:00Z"; break;
            case "null-list": json["analyses"] = null; break;
            case "null-item": json["answers"]![0] = null; break;
            case "null-settings": json["settings"] = null; break;
            case "null-text": json["transcriptSegments"]![0]!["text"] = null; break;
            case "missing-field": json.AsObject().Remove("startTime"); break;
            case "long-text": json["answers"]![0]!["answer"] = new string('x', ConversationArchiveLimits.MaximumTextLength + 1); break;
            case "long-source": json["answers"]![0]!["sources"]![0] = new string('x', ConversationArchiveLimits.MaximumSourceLength + 1); break;
            case "long-group-name": json["groups"]![0]!["name"] = new string('x', ConversationArchiveLimits.MaximumGroupNameLength + 1); break;
            case "empty-group-name": json["groups"]![0]!["name"] = "  "; break;
            case "group-membership": json["groups"]![1]!["transcriptIds"]![0] = json["groups"]![0]!["transcriptIds"]![0]!.DeepClone(); break;
            case "unknown-group-transcript": json["groups"]![0]!["transcriptIds"]![0] = Guid.NewGuid(); break;
            case "historical-only-group-transcript": json["transcriptSegments"] = new JsonArray(); break;
            case "group-analysis-membership": json["analyses"]![0]!["transcript"]![0] = json["transcriptSegments"]![0]!.DeepClone(); break;
            case "group-analysis-number": json["analyses"]![0]!["groupNumber"] = 9; break;
            case "group-analysis-trigger": json["analyses"]![0]!["trigger"] = "Manual"; break;
            case "group-metadata": json["analyses"]![0]!["groupNumber"] = null; break;
            case "next-group-number": json["nextGroupNumber"] = 1; break;
            case "unknown-current-analysis": json["analysisId"] = Guid.NewGuid(); break;
            case "unknown-analysis-question": json["analyses"]![0]!["questionIds"]![0] = Guid.NewGuid(); break;
            case "unknown-answer-question": json["answers"]![0]!["suggestedQuestionId"] = Guid.NewGuid(); break;
            case "transcript-link-to-answer": json["answers"]![0]!["transcriptSegmentId"] = json["answers"]![1]!["id"]!.DeepClone(); break;
            case "empty-transcript-link": json["answers"]![0]!["transcriptSegmentId"] = Guid.Empty; break;
            case "invalid-speaker": json["transcriptSegments"]![0]!["speakerNumber"] = 0; break;
            case "invalid-question-count": json["analyses"]![0]!["addedQuestionCount"] = 100; break;
            case "invalid-guidance-version": json["analyses"]![0]!["guidanceVersion"] = -1; break;
            case "invalid-context-window": json["settings"]!["contextWindowDuration"] = "-00:01:00"; break;
            case "zero-context-window": json["settings"]!["contextWindowDuration"] = "00:00:00"; break;
            case "invalid-context-limit": json["settings"]!["maxContextCharacters"] = 0; break;
            case "too-many-items":
                json["answers"]![0]!["sources"] = new JsonArray(Enumerable.Range(0, ConversationArchiveLimits.MaximumListLength + 1)
                    .Select(_ => (JsonNode?)JsonValue.Create("")).ToArray());
                break;
            default: Assert.Fail($"Unknown invalid test case: {invalidCase}"); break;
        }
        var invalidPath = Path.Combine(_directory, "invalid.json");
        await File.WriteAllTextAsync(invalidPath, json.ToJsonString());
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync(invalidPath));
        var message = UiMessage.FromDiagnostic(error.Message)!;
        Assert.AreNotEqual("DiagnosticUnknown", message.Key, invalidCase);
        Assert.AreNotEqual(message.Resolve(UiText.For(ConversationLanguage.Chinese)),
            message.Resolve(UiText.For(ConversationLanguage.English)));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{\"version\":1,\"version\":1}")]
    public async Task MalformedJsonIsRejected(string json)
    {
        var path = Path.Combine(_directory, "invalid.json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new JsonConversationArchiveStore(_directory).LoadAsync(path));
    }

    [TestMethod]
    public async Task ExcessiveDepthAndInvalidUtf8AreRejected()
    {
        var store = new JsonConversationArchiveStore(_directory);
        var path = await store.SaveAsync(ConversationArchive.Capture(FullSession()));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        JsonNode nesting = JsonValue.Create(1)!;
        for (var index = 0; index < ConversationArchiveLimits.MaximumJsonDepth; index++)
            nesting = new JsonObject { ["value"] = nesting };
        json["unknown"] = nesting;
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync(path));
        await File.WriteAllBytesAsync(path, [(byte)'{', (byte)'"', 0xff, (byte)'"', (byte)':', (byte)'1', (byte)'}']);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync(path));
    }

    [TestMethod]
    public async Task OversizedReadFailsBeforeParsingAndDoesNotCreateAnyLocalRecord()
    {
        var path = Path.Combine(_directory, "oversized.json");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(ConversationArchiveLimits.MaximumFileBytes + 1);
        var store = new JsonConversationArchiveStore(Path.Combine(_directory, "local"));
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync(path));
        StringAssert.Contains(error.Message, "64 MiB");
        Assert.IsFalse(Directory.Exists(Path.Combine(_directory, "local")));
        Assert.AreEqual(ConversationArchiveLimits.MaximumFileBytes + 1, new FileInfo(path).Length);
    }

    [TestMethod]
    public async Task OversizedWriteAndPreCancelledSavePreserveTheLastValidFileAndRemoveStagingFiles()
    {
        var session = FullSession();
        var store = new JsonConversationArchiveStore(_directory);
        var path = await store.SaveAsync(ConversationArchive.Capture(session));
        var bytes = await File.ReadAllBytesAsync(path);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
                store.SaveAsync(ConversationArchive.Capture(session), cancellation.Token));
        }
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        var repeatedText = new string('x', ConversationArchiveLimits.MaximumTextLength);
        session.Analyses.Add(new ConversationAnalysisRequest
        {
            Timestamp = session.StartTime.AddMinutes(1),
            Trigger = ConversationAnalysisTrigger.Manual,
            Transcript = [],
            ExistingQuestions = Enumerable.Repeat(repeatedText, 65).ToArray()
        });
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.SaveAsync(ConversationArchive.Capture(session)));
        StringAssert.Contains(error.Message, "64 MiB");
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.HasCount(1, Directory.GetFiles(_directory));
    }

    [TestMethod]
    public async Task FailedAtomicReplacementPreservesTheValidFileAndAllowsRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file sharing makes this replacement failure deterministic.");
            return;
        }
        var session = FullSession();
        var store = new JsonConversationArchiveStore(_directory);
        var path = await store.SaveAsync(ConversationArchive.Capture(session));
        var bytes = await File.ReadAllBytesAsync(path);
        Set(session.Groups[0], nameof(TranscriptGroup.Name), "Updated");
        var updated = ConversationArchive.Capture(session);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(updated));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        }
        Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"));
        await store.SaveAsync(updated);
        Assert.AreEqual("Updated", (await store.LoadAsync(path)).Restore().Groups[0].Name);
    }

    [TestMethod]
    public async Task WriterCoalescesToTheNewestPendingSnapshotWithOneActiveSave()
    {
        var session = FullSession();
        var store = new GatedStore();
        var writer = new ConversationArchiveWriter(store);
        var savedEvents = new ConcurrentQueue<string>();
        writer.Saved += savedEvents.Enqueue;
        writer.Queue(NamedSnapshot(session, "first"));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        writer.Queue(NamedSnapshot(session, "second"));
        writer.Queue(NamedSnapshot(session, "latest"));
        var flush = writer.FlushAsync();
        Assert.IsFalse(flush.IsCompleted);
        store.Release.TrySetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(new[] { "first", "latest" }, store.Saves.Select(item => item.Restore().Groups[0].Name).ToArray());
        Assert.AreEqual(1, store.MaximumConcurrency);
        Assert.HasCount(2, savedEvents);
    }

    [TestMethod]
    public async Task WriterNeverSilentlyDropsADifferentConversation()
    {
        var first = ConversationArchive.Capture(FullSession());
        var second = ConversationArchive.Capture(FullSession());
        var store = new GatedStore();
        var writer = new ConversationArchiveWriter(store);
        writer.Queue(first);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.ThrowsExactly<InvalidOperationException>(() => writer.Queue(second));
        store.Release.TrySetResult();
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        writer.Queue(second);
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(new[] { first.SessionId, second.SessionId }, store.Saves.Select(item => item.SessionId).ToArray());
    }

    [TestMethod]
    public async Task FailedWriterRetainsNewestSnapshotReportsFailureAndRetriesOnlyWhenRequested()
    {
        var session = FullSession();
        var store = new GatedStore { Failure = new IOException("Simulated disk failure.") };
        var writer = new ConversationArchiveWriter(store);
        var failures = new ConcurrentQueue<Exception>();
        var savedEvents = new ConcurrentQueue<string>();
        writer.Failed += failures.Enqueue;
        writer.Saved += savedEvents.Enqueue;
        writer.Queue(NamedSnapshot(session, "first"));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        writer.Queue(NamedSnapshot(session, "superseded"));
        writer.Queue(NamedSnapshot(session, "newest"));
        var failedFlush = writer.FlushAsync();
        store.Release.TrySetResult();
        await Assert.ThrowsExactlyAsync<IOException>(() => failedFlush.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.HasCount(1, failures);
        Assert.HasCount(1, store.Attempts);
        Assert.IsEmpty(savedEvents);
        Assert.ThrowsExactly<InvalidOperationException>(() => writer.Queue(ConversationArchive.Capture(FullSession())));
        store.Failure = null;
        await Assert.ThrowsExactlyAsync<IOException>(() => writer.FlushAsync());
        await Assert.ThrowsExactlyAsync<IOException>(() => writer.FlushAsync());
        Assert.HasCount(1, store.Attempts);
        Assert.IsEmpty(store.Saves);
        writer.Queue(NamedSnapshot(session, "newest"));
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(2, store.Attempts);
        Assert.HasCount(1, store.Saves);
        Assert.AreEqual("newest", store.Saves.Single().Restore().Groups[0].Name);
        Assert.HasCount(1, savedEvents);
        await writer.FlushAsync();
        Assert.HasCount(2, store.Attempts);
    }

    [TestMethod]
    public async Task WriterNormalizesExpectedFailuresAndNewQueueCanRetry()
    {
        var store = new GatedStore { Failure = new UnauthorizedAccessException("Simulated access denial.") };
        var writer = new ConversationArchiveWriter(store);
        var session = FullSession();
        writer.Queue(NamedSnapshot(session, "old"));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var failedFlush = writer.FlushAsync();
        store.Release.TrySetResult();
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => failedFlush.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsInstanceOfType<UnauthorizedAccessException>(error.InnerException);
        store.Failure = null;
        writer.Queue(NamedSnapshot(session, "retry"));
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("retry", store.Saves.Single().Restore().Groups[0].Name);
    }

    private static ConversationArchive NamedSnapshot(ConversationSession session, string name)
    {
        Set(session.Groups[0], nameof(TranscriptGroup.Name), name);
        return ConversationArchive.Capture(session);
    }

    private static ConversationSession FullSession()
    {
        var start = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.FromHours(8));
        var session = new ConversationSession
        {
            Settings = new ConversationSettings
            {
                Language = ConversationLanguage.Chinese,
                AnswerLanguage = ConversationLanguage.English,
                AnswerStyle = AnswerStyle.Detailed,
                AutomaticAnalysis = true,
                ContextWindowDuration = TimeSpan.FromMinutes(7),
                MaxContextCharacters = 12_345,
                InputDeviceId = "microphone-secret",
                OutputDeviceId = "output-device-secret"
            }
        };
        Set(session, nameof(ConversationSession.StartTime), start);
        Set<DateTimeOffset?>(session, nameof(ConversationSession.EndTime), start.AddHours(1));
        Set(session, nameof(ConversationSession.WorkIqConversationId), "cloud-conversation-secret");
        Set(session, nameof(ConversationSession.NextGroupNumber), 8);
        var older = new[]
        {
            new TranscriptSegment(Guid.NewGuid(), start.AddSeconds(1), start.AddSeconds(2), "历史讨论：需求 🗣", "Speech", true, 1),
            new TranscriptSegment(Guid.NewGuid(), start.AddSeconds(3), start.AddSeconds(4), "Historical turn", "Speech", true, 2)
        };
        session.TranscriptSegments.Add(new TranscriptSegment(Guid.NewGuid(), start.AddSeconds(10), start.AddSeconds(12),
            "当前讨论：确认方案。", "Speech", true, 2));
        session.TranscriptSegments.Add(new TranscriptSegment(Guid.NewGuid(), start.AddSeconds(14), start.AddSeconds(15),
            "A retained partial", "Speech", false));
        session.TranscriptSegments.AddRange(older);
        var historicalGroup = new TranscriptGroup
        {
            Number = 3,
            Timestamp = start.AddSeconds(5),
            TranscriptIds = older.Select(item => item.Id).ToArray()
        };
        Set(historicalGroup, nameof(TranscriptGroup.Name), "需求确认");
        Set(historicalGroup, nameof(TranscriptGroup.IsExpanded), false);
        session.Groups.Add(historicalGroup);
        session.Groups.Add(new TranscriptGroup
        {
            Number = 7,
            Timestamp = start.AddSeconds(16),
            TranscriptIds = [session.TranscriptSegments[0].Id]
        });
        var firstSuggestion = new SuggestedQuestion
        {
            Timestamp = start.AddSeconds(20),
            Question = "如何保留历史？",
            Reason = "需要完整记录。",
            ContextUsed = "历史讨论：需求 🗣"
        };
        var secondSuggestion = new SuggestedQuestion
        {
            Timestamp = start.AddSeconds(30),
            Question = "Who owns delivery?",
            Reason = "Owner is not explicit.",
            ContextUsed = "当前讨论：确认方案。"
        };
        session.SuggestedQuestions.AddRange([firstSuggestion, secondSuggestion]);
        var firstAnalysis = new ConversationAnalysisRequest
        {
            Timestamp = start.AddSeconds(18),
            IsManual = true,
            Trigger = ConversationAnalysisTrigger.Group,
            GuidanceVersion = 1,
            Transcript = older,
            ExistingQuestions = new[] { "Earlier need?" },
            GroupId = historicalGroup.Id,
            GroupNumber = historicalGroup.Number,
            GroupName = "分析时的名称"
        };
        Set(firstAnalysis, nameof(WorkIqRequest.Status), QuestionStatus.Completed);
        Set(firstAnalysis, nameof(ConversationAnalysisRequest.Questions), new[] { firstSuggestion });
        Set(firstAnalysis, nameof(ConversationAnalysisRequest.AddedQuestionCount), 1);
        Set(firstAnalysis, nameof(ConversationAnalysisRequest.Response), """{"questions":[{"question":"如何保留历史？","reason":"需要完整记录。"}]}""");
        Set(firstAnalysis, nameof(ConversationAnalysisRequest.Sources), new[] { "https://example.test/doc/第一章", "Source title" });
        var secondAnalysis = new ConversationAnalysisRequest
        {
            Timestamp = start.AddSeconds(28),
            IsManual = false,
            Trigger = ConversationAnalysisTrigger.Interval,
            Transcript = session.TranscriptSegments.Where(item => item.IsFinal).ToArray(),
            ExistingQuestions = [firstSuggestion.Question]
        };
        Set(secondAnalysis, nameof(WorkIqRequest.Status), QuestionStatus.Failed);
        Set(secondAnalysis, nameof(WorkIqRequest.Error), "Historical analysis failure");
        Set(secondAnalysis, nameof(ConversationAnalysisRequest.Questions), new[] { firstSuggestion, secondSuggestion });
        Set(secondAnalysis, nameof(ConversationAnalysisRequest.AddedQuestionCount), 1);
        Set(secondAnalysis, nameof(ConversationAnalysisRequest.Response), "Raw non-JSON response 保留");
        Set(secondAnalysis, nameof(ConversationAnalysisRequest.Sources), new[] { "Analysis source" });
        session.Analyses.AddRange([firstAnalysis, secondAnalysis]);
        Set(session, nameof(ConversationSession.Analysis), secondAnalysis);
        var firstAnswer = new QuestionRequest
        {
            Timestamp = start.AddSeconds(35),
            IsManual = false,
            Question = firstSuggestion.Question,
            ContextUsed = firstSuggestion.ContextUsed,
            TranscriptSegmentId = older[0].Id,
            SuggestedQuestionId = firstSuggestion.Id
        };
        Set(firstAnswer, nameof(WorkIqRequest.Status), QuestionStatus.Completed);
        Set(firstAnswer, nameof(QuestionRequest.Answer), "答复：保留全部历史。");
        Set(firstAnswer, nameof(QuestionRequest.Sources), new[] { "https://example.test/policy", "政策 / 原始来源" });
        var secondAnswer = new QuestionRequest
        {
            Timestamp = start.AddSeconds(40),
            IsManual = true,
            Question = "A manual follow-up?",
            ContextUsed = "Standalone cleared transcript context",
            TranscriptSegmentId = Guid.NewGuid(),
            SuggestedQuestionId = secondSuggestion.Id
        };
        Set(secondAnswer, nameof(WorkIqRequest.Status), QuestionStatus.Failed);
        Set(secondAnswer, nameof(WorkIqRequest.Error), "Offline answer failure");
        Set(secondAnswer, nameof(QuestionRequest.Answer), "Preserved partial answer");
        session.Answers.AddRange([firstAnswer, secondAnswer]);
        return session;
    }

    private static void Set<T>(object target, string propertyName, T value) =>
        target.GetType().GetProperty(propertyName)!.SetValue(target, value);

    private sealed class GatedStore : IConversationArchiveStore
    {
        private int _active;
        private int _maximumConcurrency;
        private int _attemptCount;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ConversationArchive> Attempts { get; } = new();
        public ConcurrentQueue<ConversationArchive> Saves { get; } = new();
        public Exception? Failure { get; set; }
        public int MaximumConcurrency => _maximumConcurrency;

        public string GetPath(Guid sessionId) => $"{sessionId:N}.json";
        public Task<ConversationArchive> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<string> SaveAsync(ConversationArchive archive, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            if (active > _maximumConcurrency) Interlocked.Exchange(ref _maximumConcurrency, active);
            try
            {
                Attempts.Enqueue(archive);
                var attempt = Interlocked.Increment(ref _attemptCount);
                Entered.TrySetResult();
                if (attempt == 1) await Release.Task.WaitAsync(cancellationToken);
                if (Failure is { } failure) throw failure;
                Saves.Enqueue(archive);
                return GetPath(archive.SessionId);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
