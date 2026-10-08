using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.Speech;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Core.Conversation;

public sealed partial class ConversationSessionManager
{
    private readonly IAudioCaptureService _audio;
    private readonly ISpeechRecognitionService _speech;
    private readonly IAuthenticationService _auth;
    private readonly INetworkStatus _network;
    private readonly IWorkIqClient _workIq;
    private readonly ITranscriptEngine _transcript;
    private readonly IContextBuilder _contextBuilder;
    private readonly TimeProvider _timeProvider;
    private readonly ConversationAnalysisSchedule _analysisSchedule = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private IConversationBuffer? _buffer;
    private WorkIqRequestQueue? _queue;
    private CancellationTokenSource? _conversationCancellation;
    private CancellationTokenSource? _analysisCancellation;
    private Task? _worker;
    private TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _paused;
    private bool _capturing;
    private bool _workIqBusy;
    private Task _speechRecovery = Task.CompletedTask;
    private bool _closing;
    private bool _committingFinal;

    public ConversationSession? Session { get; private set; }
    public ConversationSession? CurrentConversation { get; private set; }
    public ConversationUiState State { get; private set; } = ConversationUiState.Idle;
    public bool IsListening
    {
        get { lock (_gate) return Session is not null && !_paused && _capturing; }
    }
    public IReadOnlyList<TranscriptSegment> TranscriptSegments
    {
        get
        {
            lock (_gate) return CurrentConversation?.TranscriptSegments.ToArray() ?? [];
        }
    }
    public ConversationAnalysisRequest? Analysis
    {
        get { lock (_gate) return CurrentConversation?.Analysis; }
    }
    public IReadOnlyList<ConversationAnalysisRequest> Analyses
    {
        get { lock (_gate) return CurrentConversation?.Analyses.ToArray() ?? []; }
    }
    public IReadOnlyList<TranscriptGroup> Groups
    {
        get { lock (_gate) return CurrentConversation?.Groups.ToArray() ?? []; }
    }
    public IReadOnlyList<QuestionRequest> AnswerHistory
    {
        get { lock (_gate) return CurrentConversation?.Answers.ToArray() ?? []; }
    }
    public bool HasPendingWork
    {
        get
        {
            lock (_gate) return CurrentConversation is { } session &&
                session.Answers.Cast<WorkIqRequest>().Concat(session.Analyses)
                    .Any(request => request.Status is QuestionStatus.Pending or QuestionStatus.Processing);
        }
    }
    public IReadOnlyList<SuggestedQuestion> SuggestedQuestions
    {
        get { lock (_gate) return CurrentConversation?.Analysis?.Questions.ToArray() ?? []; }
    }
    public int PendingAnalysisCount
    {
        get { lock (_gate) return Session is null ? 0 : _analysisSchedule.PendingCount; }
    }
    public event Action<ConversationUiState>? StateChanged;
    public event Action<TranscriptSegment?>? TranscriptUpdated;
    public event Action<QuestionRequest>? AnswerUpdated;
    public event Action? AnalysisUpdated;
    public event Action? AudioDevicesChanged;
    public event Action<string>? ErrorOccurred;
    public event Action? ConversationEnded;
    public event Action? ConversationChanged;
    public event Action? GroupsUpdated;

    public ConversationSessionManager(IAudioCaptureService audio, ISpeechRecognitionService speech,
        IAuthenticationService auth, INetworkStatus network, IWorkIqClient workIq, ITranscriptEngine transcript,
        IContextBuilder contextBuilder, TimeProvider? timeProvider = null,
        IConversationArchiveStore? archiveStore = null)
    {
        _audio = audio;
        _speech = speech;
        _auth = auth;
        _network = network;
        _workIq = workIq;
        _transcript = transcript;
        _contextBuilder = contextBuilder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        InitializeArchive(archiveStore);
        _audio.AudioDataAvailable += OnAudio;
        _audio.AudioError += OnFailure;
        _audio.AudioDeviceChanged += () =>
        {
            AudioDevicesChanged?.Invoke();
            ErrorOccurred?.Invoke("Audio device list changed. Check input and output selections.");
        };
        _speech.PartialTranscriptReceived += OnPartial;
        _speech.FinalTranscriptReceived += OnFinal;
        _speech.RecognitionError += OnFailure;
        _transcript.Updated += segment =>
        {
            lock (_gate)
                if (_committingFinal) return;
            TranscriptUpdated?.Invoke(segment);
        };
    }

    public IReadOnlyList<AudioDevice> ListDevices(AudioDeviceKind kind = AudioDeviceKind.Input) => _audio.ListDevices(kind);
    public IReadOnlyList<ConversationLanguage> AvailableLanguages() => _speech.AvailableLanguages();

    public void StartConversation(ConversationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _lifecycle.Wait();
        try
        {
            if (Session is not null) throw new InvalidOperationException("End the current conversation first.");
            CloseConversationCoreAsync().GetAwaiter().GetResult();
            StartConversationCore(settings);
        }
        finally { _lifecycle.Release(); }
    }

    private void StartConversationCore(ConversationSettings settings)
    {
        lock (_gate)
        {
            if (Session is not null) throw new InvalidOperationException("End the current conversation first.");
            _analysisSchedule.Reset();
            _buffer = new ConversationBuffer(settings.ContextWindowDuration, settings.MaxContextCharacters);
            _queue = new WorkIqRequestQueue();
            _conversationCancellation = new CancellationTokenSource();
            _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumed.SetResult();
            _paused = false;
            _capturing = false;
            _workIqBusy = false;
            Session = new ConversationSession { Settings = settings, StartTime = _timeProvider.GetUtcNow() };
            CurrentConversation = Session;
            PersistLocked(Session);
        }
        ConversationChanged?.Invoke();
        _transcript.Clear();
        try
        {
            FlushArchiveAsync().GetAwaiter().GetResult();
            SetState(ConversationUiState.Starting);
            _speech.Start(settings.Language);
            lock (_gate)
                if (_paused) throw new SpeechConnectionException("Speech 服务在启动期间断开，请重试。");
            _audio.Start(settings.AudioCapture);
            lock (_gate)
            {
                if (_paused) throw new SpeechConnectionException("Speech 服务在启动期间断开，请重试。");
                _capturing = true;
            }
        }
        catch
        {
            WorkIqRequest[] interrupted = [];
            try { _audio.Stop(); }
            finally
            {
                try { _speech.Stop(); }
                finally
                {
                    lock (_gate)
                    {
                        if (Session is { } failed)
                        {
                            interrupted = failed.Answers.Cast<WorkIqRequest>().Concat(failed.Analyses)
                                .Where(request => request.Status is QuestionStatus.Pending or QuestionStatus.Processing)
                                .ToArray();
                            foreach (var request in interrupted) request.Status = QuestionStatus.Cancelled;
                            failed.EndTime = _timeProvider.GetUtcNow();
                            PersistLocked(failed);
                        }
                        Session = null;
                        _capturing = false;
                        _buffer?.Clear();
                        _buffer = null;
                        _conversationCancellation?.Dispose();
                        _conversationCancellation = null;
                        _queue = null;
                    }
                    SetState(ConversationUiState.Error);
                    foreach (var request in interrupted) PublishRequest(request);
                }
            }
            throw;
        }
        var session = Session!;
        var queue = _queue!;
        var token = _conversationCancellation!.Token;
        _worker = Task.Run(() => ProcessQueueAsync(session, queue, token));
        SetState(ConversationUiState.Listening);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (Session is null || _paused || !_capturing)
                throw new InvalidOperationException("Conversation is not listening.");
            _paused = true;
            _capturing = false;
            _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { _audio.Stop(); }
        finally
        {
            _speech.Stop();
            SetState(ConversationUiState.Paused);
        }
    }

    public void Resume()
    {
        _speechRecovery.GetAwaiter().GetResult();
        ConversationSettings settings;
        lock (_gate)
        {
            if (Session is null || !_paused) throw new InvalidOperationException("Conversation is not paused.");
            settings = Session.Settings;
            _paused = false;
            _capturing = false;
        }
        SetState(ConversationUiState.Starting);
        try
        {
            _speech.Start(settings.Language);
            lock (_gate)
                if (_paused) throw new SpeechConnectionException("Speech 服务在恢复期间断开，请重试。");
            _audio.Start(settings.AudioCapture);
            lock (_gate)
            {
                if (_paused) throw new SpeechConnectionException("Speech 服务在恢复期间断开，请重试。");
                _capturing = true;
            }
        }
        catch
        {
            lock (_gate)
            {
                _paused = true;
                _capturing = false;
            }
            try { _audio.Stop(); }
            catch (InvalidOperationException cleanupError)
            {
                ErrorOccurred?.Invoke(cleanupError.Message);
            }
            finally
            {
                _speech.Stop();
                SetState(ConversationUiState.Paused);
            }
            throw;
        }
        lock (_gate)
        {
            _paused = false;
            _resumed.TrySetResult();
        }
        SetState(ConversationUiState.Listening);
    }

    public async Task EndConversationAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await CloseConversationCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task CloseConversationCoreAsync()
    {
        bool alreadyStopped;
        lock (_gate) alreadyStopped = Session is null && _worker is null;
        if (alreadyStopped)
        {
            await FlushArchiveAsync().ConfigureAwait(false);
            return;
        }
        CancellationTokenSource? cancellation;
        WorkIqRequestQueue? queue;
        Task? worker;
        bool hadAudio;
        lock (_gate)
        {
            if (CurrentConversation is null) return;
            hadAudio = Session is not null;
            if (Session is not null) Session.EndTime = _timeProvider.GetUtcNow();
            Session = null;
            _closing = true;
            cancellation = _conversationCancellation;
            queue = _queue;
            worker = _worker;
            _paused = false;
            _capturing = false;
            _workIqBusy = false;
            _resumed.TrySetResult();
        }
        cancellation?.Cancel();
        queue?.Complete();
        try
        {
            await _speechRecovery.ConfigureAwait(false);
            if (hadAudio) _audio.Stop();
        }
        finally
        {
            try { if (hadAudio) _speech.Stop(); }
            finally
            {
                try
                {
                    if (worker is not null) await worker.ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        _buffer?.Clear();
                        _buffer = null;
                        _conversationCancellation = null;
                        _queue = null;
                        _worker = null;
                        _analysisSchedule.Reset();
                        _closing = false;
                        CurrentConversation!.WorkIqConversationId = null;
                        PersistLocked(CurrentConversation!);
                    }
                    cancellation?.Dispose();
                    SetState(ConversationUiState.Idle);
                    ConversationEnded?.Invoke();
                    await FlushArchiveAsync().ConfigureAwait(false);
                }
            }
        }
    }

    public QuestionRequest AskManually(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ConversationSession session;
        string context;
        lock (_gate)
        {
            session = RequireWorkspaceLocked();
            var buffer = _buffer ?? CreateBuffer(session);
            var reference = Session is null
                ? session.TranscriptSegments.LastOrDefault()?.TimestampEnd ?? session.StartTime
                : DateTimeOffset.Now;
            context = buffer.GetRecentContext(reference);
        }
        return Enqueue(session, question.Trim(), context, true);
    }

    public void ClearTranscript()
    {
        lock (_gate)
        {
            var session = RequireWorkspaceLocked();
            foreach (var analysis in session.Analyses.Where(item =>
                item.Status is QuestionStatus.Pending or QuestionStatus.Processing))
            {
                analysis.Status = QuestionStatus.Cancelled;
                analysis.RequestExplicitly();
            }
            _analysisCancellation?.Cancel();
            session.Analysis = null;
            session.TranscriptSegments.Clear();
            session.Groups.Clear();
            _analysisSchedule.Reset();
            _buffer?.Clear();
            _transcript.Clear();
            PersistLocked(session);
        }
        GroupsUpdated?.Invoke();
        AnalysisUpdated?.Invoke();
    }

    public ConversationAnalysisRequest AnalyzeConversation()
    {
        ConversationAnalysisRequest request;
        lock (_gate)
        {
            var session = RequireWorkspaceLocked();
            if (session.TranscriptSegments.Count == 0)
                throw new InvalidOperationException("There is no conversation content to analyze yet.");
            if (session.Analyses.LastOrDefault(item => item.GroupId is null &&
                item.Status is QuestionStatus.Pending or QuestionStatus.Processing) is { } existing)
            {
                existing.RequestExplicitly();
                return existing;
            }
            request = QueueAnalysisLocked(session, ConversationAnalysisTrigger.Manual);
        }
        PublishRequest(request);
        return request;
    }

    public void AnalyzeIfDue()
    {
        ConversationAnalysisRequest request;
        lock (_gate)
        {
            if (Session is not { } session || _paused || !_capturing ||
                !session.Settings.AutomaticAnalysis ||
                session.Analyses.Any(item => item.GroupId is null &&
                    item.Status is QuestionStatus.Pending or QuestionStatus.Processing) ||
                _analysisSchedule.DueTrigger(_timeProvider.GetUtcNow()) is not { } trigger)
                return;
            request = QueueAnalysisLocked(session, trigger);
        }
        PublishRequest(request);
    }

    private ConversationAnalysisRequest QueueAnalysisLocked(ConversationSession session,
        ConversationAnalysisTrigger trigger, TranscriptGroup? group = null)
    {
        var ids = group?.TranscriptIds.ToHashSet();
        var transcript = session.TranscriptSegments.Where(segment => ids is null || ids.Contains(segment.Id)).ToArray();
        var transcriptIds = transcript.Select(segment => segment.Id).ToHashSet();
        var previous = session.Analyses.LastOrDefault(item => item.GroupId == group?.Id &&
            item.GuidanceVersion == ConversationAnalysisPrompt.CurrentGuidanceVersion &&
            item.Status == QuestionStatus.Completed && item.Transcript.All(segment => transcriptIds.Contains(segment.Id)));
        var questions = previous?.Questions.ToArray() ?? [];
        var request = new ConversationAnalysisRequest
        {
            Transcript = transcript,
            ExistingQuestions = questions.Select(question => question.Question).ToArray(),
            Questions = questions,
            Timestamp = _timeProvider.GetUtcNow(),
            Trigger = trigger,
            GuidanceVersion = ConversationAnalysisPrompt.CurrentGuidanceVersion,
            IsManual = trigger is ConversationAnalysisTrigger.Manual or ConversationAnalysisTrigger.Group,
            GroupId = group?.Id,
            GroupNumber = group?.Number,
            GroupName = group?.Name
        };
        session.Analysis = request;
        session.Analyses.Add(request);
        if (group is null) _analysisSchedule.BeginAttempt();
        QueueLocked(request);
        if (group is null && request.Status == QuestionStatus.Failed)
            _analysisSchedule.Fail(_timeProvider.GetUtcNow());
        PersistLocked(session);
        return request;
    }

    public QuestionRequest? GetAnswerForSuggestion(Guid questionId)
    {
        lock (_gate) return CurrentConversation?.Answers.FirstOrDefault(x => x.SuggestedQuestionId == questionId);
    }

    public QuestionRequest AskSuggestedQuestion(Guid questionId)
    {
        QuestionRequest request;
        var changed = false;
        lock (_gate)
        {
            var session = RequireWorkspaceLocked();
            var question = session.SuggestedQuestions.FirstOrDefault(x => x.Id == questionId)
                ?? throw new InvalidOperationException("This question has been cleared or belongs to another conversation.");
            if (session.Answers.FirstOrDefault(x => x.SuggestedQuestionId == questionId) is { } existing)
            {
                request = existing;
                changed = ReactivateRequestLocked(request);
            }
            else
            {
                request = new QuestionRequest
                {
                    Question = question.Question,
                    ContextUsed = question.ContextUsed,
                    Timestamp = _timeProvider.GetUtcNow(),
                    IsManual = true,
                    SuggestedQuestionId = question.Id
                };
                session.Answers.Add(request);
                QueueLocked(request);
                changed = true;
            }
        }
        if (changed) PublishRequest(request);
        return request;
    }

    public QuestionRequest? GetAnswerForTranscript(Guid segmentId)
    {
        lock (_gate) return CurrentConversation?.Answers.FirstOrDefault(x => x.TranscriptSegmentId == segmentId);
    }

    public QuestionRequest AskFromTranscript(Guid segmentId)
    {
        QuestionRequest request;
        var changed = false;
        lock (_gate)
        {
            var session = RequireWorkspaceLocked();
            var segments = session.TranscriptSegments;
            var segment = segments.FirstOrDefault(s => s.Id == segmentId && s.IsFinal)
                ?? throw new InvalidOperationException("这条转写已被清除或不属于当前对话。");
            var existing = session.Answers.FirstOrDefault(x => x.TranscriptSegmentId == segmentId);
            if (existing is not null)
            {
                request = existing;
                changed = ReactivateRequestLocked(request);
            }
            else
            {
                var historical = new ConversationBuffer(session.Settings.ContextWindowDuration,
                    session.Settings.MaxContextCharacters);
                foreach (var previous in segments.Where(s => s.Id != segmentId &&
                    s.TimestampEnd <= segment.TimestampStart &&
                    s.TimestampEnd >= segment.TimestampStart - session.Settings.ContextWindowDuration)
                    .OrderBy(s => s.TimestampEnd))
                    historical.Add(previous);
                request = new QuestionRequest
                {
                    Question = segment.Text,
                    ContextUsed = historical.GetContextBefore(segment.TimestampStart),
                    Timestamp = DateTimeOffset.Now,
                    IsManual = true,
                    TranscriptSegmentId = segmentId
                };
                session.Answers.Add(request);
                QueueLocked(request);
                changed = true;
            }
        }
        if (changed) PublishRequest(request);
        return request;
    }

    public bool Retry(Guid requestId)
    {
        ConversationSession session;
        QuestionRequest? request;
        lock (_gate)
        {
            session = RequireWorkspaceLocked();
            request = session.Answers.Find(x => x.Id == requestId && x.Status == QuestionStatus.Failed);
            if (request is null) return false;
            RetryLocked(request);
        }
        PublishRequest(request);
        return request.Status == QuestionStatus.Pending;
    }

    private void RetryLocked(QuestionRequest request)
    {
        request.RequestExplicitly();
        request.Status = QuestionStatus.Pending;
        request.Error = null;
        QueueLocked(request);
    }

    private bool ReactivateRequestLocked(QuestionRequest request)
    {
        request.RequestExplicitly();
        if (request.Status is not (QuestionStatus.Failed or QuestionStatus.Cancelled or QuestionStatus.Ignored))
            return false;
        RetryLocked(request);
        return true;
    }

    private void QueueLocked(WorkIqRequest request)
    {
        EnsureQueueLocked(RequireWorkspaceLocked());
        if (!_queue!.TryEnqueue(request))
        {
            request.Status = QuestionStatus.Failed;
            request.Error = "Work IQ request queue is full.";
        }
    }

    private QuestionRequest Enqueue(ConversationSession session, string question, string context, bool manual,
        Guid? transcriptSegmentId = null)
    {
        var request = new QuestionRequest
        {
            Question = question, ContextUsed = context, IsManual = manual, Timestamp = DateTimeOffset.Now,
            TranscriptSegmentId = transcriptSegmentId
        };
        lock (_gate)
        {
            if (CurrentConversation != session || _closing) throw new InvalidOperationException("Conversation has ended.");
            if (transcriptSegmentId is not null &&
                session.Answers.FirstOrDefault(x => x.TranscriptSegmentId == transcriptSegmentId) is { } existing)
                return existing;
            session.Answers.Add(request);
            QueueLocked(request);
        }
        PublishRequest(request);
        return request;
    }

    private void PublishRequest(WorkIqRequest request)
    {
        lock (_gate)
            if (CurrentConversation is { } session) PersistLocked(session);
        if (request is QuestionRequest question) AnswerUpdated?.Invoke(question);
        else AnalysisUpdated?.Invoke();
    }

    private async Task ProcessQueueAsync(ConversationSession session, WorkIqRequestQueue queue, CancellationToken token)
    {
        try
        {
            await foreach (var request in queue.ReadAllAsync(token))
            {
                if (!request.IsManual)
                {
                    Task resume;
                    lock (_gate) resume = _resumed.Task;
                    await Task.WhenAny(resume, request.ExplicitRequest).WaitAsync(token).ConfigureAwait(false);
                }
                lock (_gate)
                {
                    if (CurrentConversation != session || _closing || request.Status != QuestionStatus.Pending) continue;
                    request.Status = QuestionStatus.Processing;
                    _workIqBusy = true;
                }
                PublishRequest(request);
                if (Session == session && !_paused) SetState(ConversationUiState.Thinking);
                try
                {
                    if (!_auth.IsSignedIn)
                        throw new InvalidOperationException("Sign in with Microsoft before asking Work IQ.");
                    if (!_network.IsAvailable)
                        throw new System.Net.Http.HttpRequestException("Work IQ offline. Check your network.");
                    if (request is ConversationAnalysisRequest analysis)
                        await ProcessAnalysisAsync(session, analysis, token).ConfigureAwait(false);
                    else if (request is QuestionRequest question)
                    {
                        var prompt = question.SuggestedQuestionId is not null
                            ? _contextBuilder.BuildSuggestedQuestion(question.Question, question.ContextUsed, session.Settings)
                            : _contextBuilder.Build(question.Question, question.ContextUsed, session.Settings);
                        var answer = await WorkIqPromptSender.AskAsync(_workIq, prompt,
                            session.WorkIqConversationId, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        lock (_gate)
                        {
                            question.Answer = answer.Text;
                            question.Sources = answer.Sources;
                            session.WorkIqConversationId = answer.ConversationId ?? session.WorkIqConversationId;
                            question.Status = QuestionStatus.Completed;
                        }
                        if (Session == session && !_paused) SetState(ConversationUiState.Answering);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested ||
                    request.Status == QuestionStatus.Cancelled)
                {
                    request.Status = QuestionStatus.Cancelled;
                }
                catch (Exception ex) when (ex is WorkIqException or
                    System.Net.Http.HttpRequestException or InvalidOperationException or TimeoutException)
                {
                    lock (_gate)
                    {
                        if (request.Status != QuestionStatus.Cancelled)
                        {
                            request.Status = QuestionStatus.Failed;
                            request.Error = ex.Message;
                            if (request is ConversationAnalysisRequest { GroupId: null })
                                _analysisSchedule.Fail(_timeProvider.GetUtcNow());
                        }
                    }
                    if (request.Status == QuestionStatus.Failed) ErrorOccurred?.Invoke(ex.Message);
                }
                finally
                {
                    PublishRequest(request);
                    lock (_gate) _workIqBusy = false;
                    if (Session == session)
                        SetState(_paused ? State == ConversationUiState.Error ? ConversationUiState.Error : ConversationUiState.Paused :
                            request.Error?.Contains("offline", StringComparison.OrdinalIgnoreCase) == true ?
                            ConversationUiState.Offline : ConversationUiState.Listening);
                }
                AnalyzeIfDue();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        WorkIqRequest[] pending;
        lock (_gate)
        {
            pending = session.Answers.Cast<WorkIqRequest>()
                .Concat(session.Analyses)
                .Where(x => x.Status == QuestionStatus.Pending).ToArray();
        }
        foreach (var request in pending)
        {
            request.Status = QuestionStatus.Cancelled;
            PublishRequest(request);
        }
    }

    private async Task ProcessAnalysisAsync(ConversationSession session,
        ConversationAnalysisRequest request, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_gate)
        {
            if (CurrentConversation != session || _closing || request.Status == QuestionStatus.Cancelled)
            {
                request.Status = QuestionStatus.Cancelled;
                return;
            }
            _analysisCancellation = cancellation;
        }
        try
        {
            var prompt = ConversationAnalysisPrompt.Build(request, session.Settings);
            // Discovery has its own conversation so JSON output does not contaminate answer history.
            var answer = await WorkIqPromptSender.AskAsync(_workIq, prompt, null, cancellation.Token)
                .ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                request.Response = answer.Text;
                request.Sources = answer.Sources.ToArray();
            }
            var questions = ConversationQuestionParser.Parse(answer.Text);
            var context = ConversationAnalysisPrompt.FormatTranscript(request.Transcript);
            lock (_gate)
            {
                if (CurrentConversation != session || _closing || request.Status == QuestionStatus.Cancelled)
                {
                    request.Status = QuestionStatus.Cancelled;
                    return;
                }
                var result = request.Questions.ToList();
                var existing = result.Select(question =>
                    ConversationQuestionParser.QuestionKey(question.Question)).ToHashSet(StringComparer.Ordinal);
                foreach (var question in questions)
                {
                    if (!existing.Add(ConversationQuestionParser.QuestionKey(question.Question))) continue;
                    var suggestion = new SuggestedQuestion
                    {
                        Question = question.Question,
                        Reason = question.Reason,
                        ContextUsed = context,
                        Timestamp = request.Timestamp
                    };
                    session.SuggestedQuestions.Add(suggestion);
                    result.Add(suggestion);
                    request.AddedQuestionCount++;
                }
                request.Questions = result.ToArray();
                if (request.GroupId is null) _analysisSchedule.Complete(request.Transcript.Count);
                request.Status = QuestionStatus.Completed;
            }
        }
        finally
        {
            lock (_gate)
                if (_analysisCancellation == cancellation) _analysisCancellation = null;
        }
    }

    private void OnAudio(byte[] data)
    {
        if (IsListening) _speech.AcceptAudio(data);
    }

    private void OnPartial(SpeechText speech)
    {
        if (!IsListening || string.IsNullOrWhiteSpace(speech.Text)) return;
        _transcript.ReceivePartial(speech);
        if (!_workIqBusy) SetState(ConversationUiState.SpeechDetected);
    }

    private void OnFinal(SpeechText speech)
    {
        if (!IsListening || string.IsNullOrWhiteSpace(speech.Text)) return;
        lock (_gate)
        {
            if (Session is null || _paused || !_capturing) return;
            _committingFinal = true;
            try
            {
                var segment = _transcript.CommitFinal(speech);
                Session.TranscriptSegments.Add(segment);
                _buffer!.Add(segment);
                _analysisSchedule.RecordFinal(_timeProvider.GetUtcNow());
                PersistLocked(Session);
            }
            finally { _committingFinal = false; }
            TranscriptUpdated?.Invoke(null);
        }
        AnalysisUpdated?.Invoke();
        AnalyzeIfDue();
        if (!_workIqBusy) SetState(ConversationUiState.Listening);
    }

    private void OnFailure(Exception error)
    {
        if (error is SpeechConnectionException or AudioCaptureException)
        {
            lock (_gate)
            {
                if (Session is null) return;
                if (!_paused)
                {
                    _paused = true;
                    _capturing = false;
                    _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _speechRecovery = Task.Run(() =>
                    {
                        try { _audio.Stop(); }
                        catch (AudioCaptureException cleanupError)
                        {
                            ErrorOccurred?.Invoke(cleanupError.Message);
                        }
                        finally
                        {
                            try { _speech.Stop(); }
                            catch (SpeechConnectionException cleanupError)
                            {
                                ErrorOccurred?.Invoke(cleanupError.Message);
                            }
                        }
                    });
                }
            }
            ErrorOccurred?.Invoke(error.Message);
            SetState(ConversationUiState.Error);
            return;
        }
        ErrorOccurred?.Invoke(error.Message);
        if (Session is not null && !_paused) SetState(ConversationUiState.Error);
    }

    public void SetState(ConversationUiState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
