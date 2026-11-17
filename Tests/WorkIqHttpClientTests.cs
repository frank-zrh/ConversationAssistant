using System.Net;
using System.Text;
using System.Text.Json;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class WorkIqHttpClientTests
{
    [TestMethod]
    public async Task DiscoversAnswerToolAndPreservesQuestionAndConversation()
    {
        var handler = new Handler();
        var client = Client(handler);
        var first = await client.AskAsync("hello \"quoted\" & not-a-shell-command", null, CancellationToken.None);
        var second = await client.AskAsync("follow up", first.ConversationId, CancellationToken.None);
        Assert.AreEqual("answer", first.Text);
        Assert.AreEqual("conversation-1", second.ConversationId);
        CollectionAssert.AreEqual(new[] { "https://example.com/evidence" }, first.Sources.ToArray());
        Assert.AreEqual(1, handler.Methods.Count(method => method == "initialize"));
        Assert.AreEqual(2, handler.Calls.Count);
        Assert.AreEqual("hello \"quoted\" & not-a-shell-command", handler.Calls[0].GetProperty("arguments").GetProperty("question").GetString());
        Assert.AreEqual("conversation-1", handler.Calls[1].GetProperty("arguments").GetProperty("conversationId").GetString());
        Assert.IsTrue(handler.Calls.All(call => call.GetProperty("name").GetString() == "ask"));
    }

    [TestMethod]
    public async Task AccessVerificationDoesNotSubmitQuestion()
    {
        var handler = new Handler();
        await Client(handler).VerifyAccessAsync(CancellationToken.None);
        Assert.AreEqual(0, handler.Calls.Count);
        CollectionAssert.AreEqual(new[] { "initialize", "notifications/initialized", "tools/list" }, handler.Methods);
    }

    [TestMethod]
    public async Task HandlesEventStreamAndIgnoresNotifications()
    {
        var handler = new Handler { EventStream = true };
        var answer = await Client(handler).AskAsync("question", null, CancellationToken.None);
        Assert.AreEqual("answer", answer.Text);
    }

    [TestMethod]
    public async Task MissingAnswerCapabilityIsNotReplacedByMutationTool()
    {
        var handler = new Handler { ToolName = "send_message" };
        await Assert.ThrowsExactlyAsync<WorkIqException>(() => Client(handler).AskAsync("question", null, CancellationToken.None));
        Assert.AreEqual(0, handler.Calls.Count);
    }

    [TestMethod]
    public async Task ReadOnlyFalseHintAloneDoesNotRejectTheDocumentedAskTool()
    {
        var handler = new Handler { ReadOnly = false };
        await Client(handler).VerifyAccessAsync(CancellationToken.None);
        Assert.AreEqual(0, handler.Calls.Count);
    }

    [TestMethod]
    [DataRow("{\"type\":[\"string\",\"null\"]}")]
    [DataRow("{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}")]
    [DataRow("{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}")]
    public async Task NullableStringAskParametersAreSupported(string schema)
    {
        var handler = new Handler { StringSchema = schema };
        var answer = await Client(handler).AskAsync("question", null, CancellationToken.None);
        Assert.AreEqual("answer", answer.Text);
        Assert.AreEqual(1, handler.Calls.Count);
    }

    [TestMethod]
    [DataRow("{\"type\":\"integer\"}")]
    [DataRow("{\"type\":[\"string\",\"integer\"]}")]
    [DataRow("{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"object\"}]}")]
    public async Task IncompatibleAskSchemaIsStillRejected(string schema)
    {
        var handler = new Handler { StringSchema = schema };
        var error = await Assert.ThrowsExactlyAsync<WorkIqException>(() => Client(handler).VerifyAccessAsync(CancellationToken.None));
        StringAssert.Contains(error.Message, "not a sign-in failure");
        Assert.AreEqual(0, handler.Calls.Count);
    }

    [TestMethod]
    public async Task AnswerToolMarkedDestructiveIsRejected()
    {
        var handler = new Handler { Destructive = true };
        await Assert.ThrowsExactlyAsync<WorkIqException>(() => Client(handler).VerifyAccessAsync(CancellationToken.None));
        Assert.AreEqual(0, handler.Calls.Count);
    }

    [TestMethod]
    public async Task ExpiredIdentityIsInvalidatedWithoutRetryOrLeakingServerBody()
    {
        var handler = new Handler { Status = HttpStatusCode.Unauthorized };
        var identity = new Identity();
        var error = await Assert.ThrowsExactlyAsync<WorkIqException>(() => Client(handler, identity).VerifyAccessAsync(CancellationToken.None));
        Assert.IsTrue(identity.Invalidated);
        Assert.IsFalse(error.Message.Contains("sensitive-server-error", StringComparison.Ordinal));
        Assert.AreEqual(1, handler.Methods.Count);
    }

    [TestMethod]
    public async Task ThrottlingDoesNotAutomaticallyResubmit()
    {
        var handler = new Handler { Status = HttpStatusCode.TooManyRequests };
        var error = await Assert.ThrowsExactlyAsync<WorkIqException>(() => Client(handler).VerifyAccessAsync(CancellationToken.None));
        StringAssert.Contains(error.Message, "throttling");
        Assert.AreEqual(1, handler.Methods.Count);
    }

    [TestMethod]
    public async Task MismatchedRpcResponseIsRejected()
    {
        var handler = new Handler { MismatchedId = true };
        await Assert.ThrowsExactlyAsync<WorkIqException>(() => Client(handler).VerifyAccessAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ToolErrorCannotBecomeAnAnswer()
    {
        var handler = new Handler { ToolError = true };
        await Assert.ThrowsExactlyAsync<WorkIqException>(() => Client(handler).AskAsync("question", null, CancellationToken.None));
        Assert.AreEqual(1, handler.Calls.Count);
    }

    [TestMethod]
    public async Task CallerCancellationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => Client(new Handler()).AskAsync("question", null, cancellation.Token));
    }

    private static WorkIqHttpClient Client(Handler handler, Identity? identity = null) =>
        new(new HttpClient(handler), identity ?? new Identity());

    private sealed class Identity : IWorkIqTokenProvider
    {
        public bool Invalidated { get; private set; }
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult("test-token");
        public void Invalidate() => Invalidated = true;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public List<JsonElement> Calls { get; } = [];
        public string ToolName { get; init; } = "ask";
        public bool Destructive { get; init; }
        public bool ReadOnly { get; init; } = true;
        public string StringSchema { get; init; } = "{\"type\":\"string\"}";
        public bool EventStream { get; init; }
        public bool MismatchedId { get; init; }
        public bool ToolError { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(WorkIqHttpClient.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("test-token", request.Headers.Authorization?.Parameter);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString()!;
            Methods.Add(method);
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { Content = new StringContent("sensitive-server-error") };
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            if (method != "initialize")
            {
                Assert.AreEqual("test-session", request.Headers.GetValues("Mcp-Session-Id").Single());
                Assert.AreEqual("2025-03-26", request.Headers.GetValues("MCP-Protocol-Version").Single());
            }
            var id = root.GetProperty("id").GetInt64() + (MismatchedId ? 1 : 0);
            object result;
            if (method == "initialize") result = new { protocolVersion = "2025-03-26", capabilities = new { tools = new { } } };
            else if (method == "tools/list") result = new
            {
                tools = new[] { new { name = ToolName, annotations = new { destructiveHint = Destructive, readOnlyHint = ReadOnly }, inputSchema = new
                { type = "object", properties = new { question = JsonSerializer.Deserialize<JsonElement>(StringSchema), conversationId = new { type = "string" } }, required = new[] { "question" } } } }
            };
            else
            {
                Calls.Add(root.GetProperty("params").Clone());
                result = new { isError = ToolError, structuredContent = new { response = "answer", conversationId = "conversation-1",
                    sources = new[] { "https://example.com/evidence", "javascript:alert(1)", "https://user:password@example.com/" } } };
            }
            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
            var body = EventStream ? "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\n" + "data: " + json + "\n\n" : json;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, EventStream ? "text/event-stream" : "application/json") };
            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "test-session");
            return response;
        }
    }
}
