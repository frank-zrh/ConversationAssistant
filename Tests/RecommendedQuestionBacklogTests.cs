using System.Text.Json;
using System.Xml.Linq;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.UI;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class RecommendedQuestionBacklogTests
{
    [TestMethod]
    public void KeepsNewestTenUnansweredInProvidedRankingOrder()
    {
        var candidates = Enumerable.Range(1, 15).Select(index => Question($"Question {index}?")).Reverse().ToArray();
        var result = RecommendedQuestionBacklog.Select(candidates, []);
        Assert.HasCount(RecommendedQuestionBacklog.MaximumUnansweredQuestions, result);
        CollectionAssert.AreEqual(candidates.Take(10).Select(item => item.Id).ToArray(), result.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void CompletedAnswersDoNotConsumeUnansweredSlots()
    {
        var candidates = Enumerable.Range(1, 30).Select(index => Question($"Question {index}?")).ToArray();
        var completed = candidates.Take(20).Select(item => Answer(item, QuestionStatus.Completed)).ToArray();
        var result = RecommendedQuestionBacklog.Select(candidates, completed);
        Assert.HasCount(30, result);
        CollectionAssert.AreEqual(candidates.Skip(20).Select(item => item.Id).ToArray(), result.Take(10).Select(item => item.Id).ToArray());
        Assert.IsTrue(completed.All(answer => result.Any(question => question.Id == answer.SuggestedQuestionId)));
    }

    [TestMethod]
    public void FailedPendingAndCancelledResultsStillCountAsUnanswered()
    {
        var candidates = Enumerable.Range(1, 14).Select(index => Question($"Question {index}?")).ToArray();
        var statuses = new[] { QuestionStatus.Pending, QuestionStatus.Processing, QuestionStatus.Failed,
            QuestionStatus.Cancelled, QuestionStatus.Ignored };
        var answers = candidates.Select((question, index) => Answer(question, statuses[index % statuses.Length])).ToArray();
        Assert.HasCount(10, RecommendedQuestionBacklog.Select(candidates, answers));
    }

    [TestMethod]
    public void DuplicateTextPrefersQuestionWithSavedResult()
    {
        var unanswered = Question("Explain DLP!");
        var completed = Question("EXPLAIN   DLP?");
        var another = Question("Which policy applies?");
        var result = RecommendedQuestionBacklog.Select([unanswered, another, completed, completed],
            [Answer(completed, QuestionStatus.Completed)]);
        Assert.HasCount(2, result);
        Assert.AreSame(another, result[0]);
        Assert.AreSame(completed, result[1]);
    }

    [TestMethod]
    public void RowNotifiesAnsweredStateOnlyAfterSuccessfulCompletion()
    {
        var question = Question("Which license applies?");
        var row = new SuggestedQuestionRow(question, ConversationLanguage.English);
        var changes = new List<string?>();
        row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Assert.IsFalse(row.IsAnswered);
        row.UpdateRequest(Answer(question, QuestionStatus.Processing));
        Assert.IsFalse(row.IsAnswered);
        row.UpdateRequest(Answer(question, QuestionStatus.Failed));
        Assert.IsFalse(row.IsAnswered);
        changes.Clear();
        row.UpdateRequest(Answer(question, QuestionStatus.Completed));
        Assert.IsTrue(row.IsAnswered);
        CollectionAssert.Contains(changes, nameof(SuggestedQuestionRow.IsAnswered));
        row.UpdateLanguage(UiText.For(ConversationLanguage.Chinese));
        Assert.IsTrue(row.IsAnswered);
    }

    [TestMethod]
    public void RecommendationQuestionBindsFontWeightToSavedAnswerState()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.IsTrue(page.Descendants().Any(item =>
            (string?)item.Attribute(x + "Key") == "QuestionAnswerFontWeightConverter"));
        var label = page.Descendants().Single(item => (string?)item.Attribute("Text") == "{Binding Question}" &&
            ((string?)item.Attribute("FontWeight"))?.Contains("QuestionAnswerFontWeightConverter", StringComparison.Ordinal) == true);
        StringAssert.Contains((string)label.Attribute("FontWeight")!, "Binding IsAnswered");
        Assert.IsTrue(label.Ancestors().Any(item => (string?)item.Attribute("Click") == "SuggestedQuestion_Click"));
    }

    private static SuggestedQuestion Question(string text) => new()
    {
        Question = text, Reason = "Relevant follow-up", ContextUsed = "Current conversation", Timestamp = DateTimeOffset.UtcNow
    };

    private static QuestionRequest Answer(SuggestedQuestion question, QuestionStatus status)
    {
        var answer = new QuestionRequest
        {
            Question = question.Question, ContextUsed = question.ContextUsed,
            Timestamp = DateTimeOffset.UtcNow, SuggestedQuestionId = question.Id, IsManual = true
        };
        typeof(WorkIqRequest).GetProperty(nameof(WorkIqRequest.Status))!.SetValue(answer, status);
        return answer;
    }
}

public sealed partial class PipelineTests
{
    [TestMethod]
    public async Task BacklogRetainsTenUnansweredAndOpensCompletedResultWithoutNewCalls()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal("Discuss the relevant deployment tradeoffs.", fake.Clock.GetUtcNow());
            fake.Work.AnalysisReply = BacklogReply("Batch 1A?", "Batch 1B?", "Batch 1C?");
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            var completedQuestion = first.Questions[0];
            var completedAnswer = fake.Conversation.AskSuggestedQuestion(completedQuestion.Id);
            await WaitUntil(() => completedAnswer.Status == QuestionStatus.Completed);
            ConversationAnalysisRequest latest = first;
            for (var batch = 2; batch <= 5; batch++)
            {
                fake.Work.AnalysisReply = BacklogReply($"Batch {batch}A?", $"Batch {batch}B?", $"Batch {batch}C?");
                latest = fake.Conversation.AnalyzeConversation();
                await WaitUntil(() => latest.Status == QuestionStatus.Completed);
            }
            var backlog = fake.Conversation.SuggestedQuestions;
            Assert.HasCount(11, backlog);
            Assert.AreEqual(10, backlog.Count(item => fake.Conversation.GetAnswerForSuggestion(item.Id)?.Status != QuestionStatus.Completed));
            CollectionAssert.AreEqual(latest.Questions.Select(item => item.Id).ToArray(), backlog.Take(3).Select(item => item.Id).ToArray());
            Assert.IsTrue(backlog.Any(item => item.Id == completedQuestion.Id));
            Assert.IsFalse(backlog.Any(item => item.Id == first.Questions[1].Id || item.Id == first.Questions[2].Id));
            Assert.HasCount(3, first.Questions);
            Assert.HasCount(15, fake.Conversation.CurrentConversation!.SuggestedQuestions);
            await fake.Conversation.EndConversationAsync();
            var calls = fake.Work.Calls.Count;
            for (var click = 0; click < 5; click++)
                Assert.AreSame(completedAnswer, fake.Conversation.AskSuggestedQuestion(completedQuestion.Id));
            Assert.AreSame(completedAnswer, fake.Conversation.GetAnswerForSuggestion(completedQuestion.Id));
            Assert.HasCount(calls, fake.Work.Calls);
            Assert.AreEqual(QuestionStatus.Completed, completedAnswer.Status);
            Assert.IsFalse(fake.Conversation.HasPendingWork);
            Assert.IsFalse(fake.Conversation.IsListening);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task ImportedBacklogPreservesCompletedLinksWithoutAutomaticRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backlog-import-{Guid.NewGuid():N}");
        var fake = new Harness(new JsonConversationArchiveStore(directory));
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            fake.Speech.EmitFinal("Review licensing and governance.", fake.Clock.GetUtcNow());
            fake.Work.AnalysisReply = BacklogReply("Which rule applies?", "Which boundary matters?");
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            var question = first.Questions[0];
            var answer = fake.Conversation.AskSuggestedQuestion(question.Id);
            await WaitUntil(() => answer.Status == QuestionStatus.Completed);
            fake.Work.AnalysisReply = BacklogReply("What changes next?");
            var second = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => second.Status == QuestionStatus.Completed);
            await fake.Conversation.EndConversationAsync();
            var source = fake.Conversation.RecordPath!;
            var originalBytes = await File.ReadAllBytesAsync(source);
            var calls = fake.Work.Calls.Count;
            await fake.Conversation.ImportConversationAsync(source);
            Assert.HasCount(3, fake.Conversation.SuggestedQuestions);
            var saved = fake.Conversation.AskSuggestedQuestion(question.Id);
            Assert.AreEqual(answer.Id, saved.Id);
            Assert.AreEqual(answer.Answer, saved.Answer);
            Assert.AreEqual(QuestionStatus.Completed, saved.Status);
            Assert.HasCount(calls, fake.Work.Calls);
            Assert.IsFalse(fake.Conversation.IsListening);
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(source));
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task BacklogRespectsAnalysisScopeHistoryAndTranscriptClear()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal("Discuss the first group topic.", fake.Clock.GetUtcNow());
            fake.Work.AnalysisReply = BacklogReply("Whole-chat question?");
            var full = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => full.Status == QuestionStatus.Completed);
            var firstGroup = fake.Conversation.CreateTranscriptGroup();
            fake.Work.AnalysisReply = BacklogReply("First-group question?");
            var first = fake.Conversation.AnalyzeGroup(firstGroup.Id);
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            fake.Speech.EmitFinal("Discuss a different second-group topic.", fake.Clock.GetUtcNow());
            var secondGroup = fake.Conversation.CreateTranscriptGroup();
            fake.Work.AnalysisReply = BacklogReply("Second-group question?");
            var second = fake.Conversation.AnalyzeGroup(secondGroup.Id);
            await WaitUntil(() => second.Status == QuestionStatus.Completed);
            Assert.AreEqual("Second-group question?", fake.Conversation.SuggestedQuestions.Single().Question);
            Assert.AreEqual("Whole-chat question?", fake.Conversation.GetSuggestedQuestionsForAnalysis(full.Id).Single().Question);
            fake.Work.AnalysisReply = BacklogReply("Another first-group question?");
            var revisited = fake.Conversation.AnalyzeGroup(firstGroup.Id);
            await WaitUntil(() => revisited.Status == QuestionStatus.Completed);
            CollectionAssert.AreEqual(new[] { "Another first-group question?", "First-group question?" },
                fake.Conversation.SuggestedQuestions.Select(item => item.Question).ToArray());
            Assert.AreEqual("First-group question?", fake.Conversation.GetSuggestedQuestionsForAnalysis(first.Id).Single().Question);
            fake.Conversation.ClearTranscript();
            Assert.IsEmpty(fake.Conversation.SuggestedQuestions);
            Assert.HasCount(1, first.Questions);
            fake.Speech.EmitFinal("Start fresh after clearing.", fake.Clock.GetUtcNow());
            fake.Work.AnalysisReply = BacklogReply("Fresh question?");
            var fresh = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => fresh.Status == QuestionStatus.Completed);
            Assert.AreEqual("Fresh question?", fake.Conversation.SuggestedQuestions.Single().Question);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    private static string BacklogReply(params string[] questions) => JsonSerializer.Serialize(new
    {
        questions = questions.Select(question => new { question, reason = "Useful for the current discussion." }).ToArray()
    });
}
