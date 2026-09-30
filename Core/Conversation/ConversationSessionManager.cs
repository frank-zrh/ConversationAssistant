using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.QuestionDetection;
using ConversationAssistant.Core.Speech;
using ConversationAssistant.Core.Transcript;

namespace ConversationAssistant.Core.Conversation;

public sealed class ConversationSessionManager
{
    private readonly IAudioCaptureService _audio;
    private readonly ISpeechRecognitionService _speech;
    private readonly IAuthenticationService _auth;
    private readonly INetworkStatus _network;
    private readonly IWorkIqClient _workIq;
    private readonly ITranscriptEngine _transcript;
    private readonly IQuestionDetector _detector;
    private readonly IContextBuilder _contextBuilder;
    private readonly DuplicateQuestionFilter _duplicates = new();
    private readonly object _gate = new();
    private IConversationBuffer? _buffer;
    private QuestionQueue? _queue;
    private CancellationTokenSource? _conversationCancellation;
    private Task? _worker;
    private TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _paused;
    private bool _capturing;
    private bool _workIqBusy;
    private Task _speechRecovery = Task.CompletedTask;

    public ConversationSession? Session { get; private set; }
    public ConversationUiState State { get; private set; } = ConversationUiState.Idle;
    public bool IsListening
    {
        get { lock (_gate) return Session is not null && !_paused && _capturing; }
    }
    public IReadOnlyList<TranscriptSegment> TranscriptSegments => _transcript.Segments;
    public event Action<ConversationUiState>? StateChanged;
    public event Action<TranscriptSegment?>? TranscriptUpdated;
    public event Action<QuestionRequest>? AnswerUpdated;
    public event Action? QuestionSuppressed;
    public event Action? AudioDevicesChanged;
    public event Action<string>? ErrorOccurred;
    public event Action? ConversationEnded;

    public ConversationSessionManager(IAudioCaptureService audio, ISpeechRecognitionService speech,
        IAuthenticationService auth, INetworkStatus network, IWorkIqClient workIq, ITranscriptEngine transcript,
        IQuestionDetector detector, IContextBuilder contextBuilder)
    {
        _audio = audio;
        _speech = speech;
        _auth = auth;
        _network = network;
        _workIq = workIq;
        _transcript = transcript;
        _detector = detector;
        _contextBuilder = contextBuilder;
        _audio.AudioDataAvailable += OnAudio;
        _audio.AudioError += OnFailure;
        _audio.AudioDeviceChanged += () =>
        {
            AudioDevicesChanged?.Invoke();
            ErrorOccurred?.Invoke("Audio device list changed. Check input selection.");
        };
        _speech.PartialTranscriptReceived += OnPartial;
        _speech.FinalTranscriptReceived += OnFinal;
        _speech.RecognitionError += OnFailure;
        _transcript.Updated += segment => TranscriptUpdated?.Invoke(segment);
    }

    public IReadOnlyList<AudioDevice> ListDevices() => _audio.ListDevices();
    public IReadOnlyList<ConversationLanguage> AvailableLanguages() => _speech.AvailableLanguages();

    public void StartConversation(ConversationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (Session is not null) throw new InvalidOperationException("End the current conversation first.");
            _transcript.Clear();
            _duplicates.Clear();
            _buffer = new ConversationBuffer(settings.ContextWindowDuration, settings.MaxContextCharacters);
            _queue = new QuestionQueue();
            _conversationCancellation = new CancellationTokenSource();
            _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumed.SetResult();
            _paused = false;
            _capturing = false;
            _workIqBusy = false;
            Session = new ConversationSession { Settings = settings };
        }
        try
        {
            SetState(ConversationUiState.Starting);
            _speech.Start(settings.Language);
            lock (_gate)
                if (_paused) throw new SpeechConnectionException("Speech 服务在启动期间断开，请重试。");
            _audio.Start(settings.InputDeviceId);
            lock (_gate)
            {
                if (_paused) throw new SpeechConnectionException("Speech 服务在启动期间断开，请重试。");
                _capturing = true;
                Session!.StartTime = DateTimeOffset.Now;
            }
        }
        catch
        {
            try { _audio.Stop(); }
            finally
            {
                try { _speech.Stop(); }
                finally
                {
                    lock (_gate)
                    {
                        Session = null;
                        _capturing = false;
                        _buffer?.Clear();
                        _buffer = null;
                        _conversationCancellation?.Dispose();
                        _conversationCancellation = null;
                        _queue = null;
                    }
                    SetState(ConversationUiState.Error);
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
            _audio.Start(settings.InputDeviceId);
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
        CancellationTokenSource? cancellation;
        QuestionQueue? queue;
        Task? worker;
        lock (_gate)
        {
            if (Session is null) return;
            Session.EndTime = DateTimeOffset.Now;
            Session = null;
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
            _audio.Stop();
        }
        finally
        {
            try { _speech.Stop(); }
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
                        _duplicates.Clear();
                        _transcript.Clear();
                    }
                    cancellation?.Dispose();
                    SetState(ConversationUiState.Idle);
                    ConversationEnded?.Invoke();
                }
            }
        }
    }

    public QuestionRequest AskManually(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ConversationSession session;
        IConversationBuffer buffer;
        lock (_gate)
        {
            session = Session ?? throw new InvalidOperationException("Start a conversation first.");
            buffer = _buffer!;
        }
        return Enqueue(session, question.Trim(), buffer.GetRecentContext(DateTimeOffset.Now), true);
    }

    public void ClearTranscript()
    {
        lock (_gate)
        {
            if (Session is null) throw new InvalidOperationException("Start a conversation first.");
            Session.TranscriptSegments.Clear();
            _buffer!.Clear();
            _transcript.Clear();
        }
    }

    public QuestionRequest? GetAnswerForTranscript(Guid segmentId)
    {
        lock (_gate) return Session?.Answers.FirstOrDefault(x => x.TranscriptSegmentId == segmentId);
    }

    public QuestionRequest AskFromTranscript(Guid segmentId)
    {
        QuestionRequest request;
        var changed = false;
        lock (_gate)
        {
            var session = Session ?? throw new InvalidOperationException("Start a conversation first.");
            var segments = _transcript.Segments;
            var segment = segments.FirstOrDefault(s => s.Id == segmentId && s.IsFinal)
                ?? throw new InvalidOperationException("这条转写已被清除或不属于当前对话。");
            var existing = session.Answers.FirstOrDefault(x => x.TranscriptSegmentId == segmentId);
            if (existing is not null)
            {
                request = existing;
                request.RequestExplicitly();
                if (request.Status is QuestionStatus.Failed or QuestionStatus.Cancelled or QuestionStatus.Ignored)
                {
                    RetryLocked(request);
                    changed = true;
                }
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
        if (changed) AnswerUpdated?.Invoke(request);
        return request;
    }

    public bool Retry(Guid requestId)
    {
        ConversationSession session;
        QuestionRequest? request;
        lock (_gate)
        {
            session = Session ?? throw new InvalidOperationException("Start a conversation first.");
            request = session.Answers.Find(x => x.Id == requestId && x.Status == QuestionStatus.Failed);
            if (request is null) return false;
            RetryLocked(request);
        }
        AnswerUpdated?.Invoke(request);
        return request.Status == QuestionStatus.Pending;
    }

    private void RetryLocked(QuestionRequest request)
    {
        request.RequestExplicitly();
        request.Status = QuestionStatus.Pending;
        request.Error = null;
        QueueLocked(request);
    }

    private void QueueLocked(QuestionRequest request)
    {
        if (!_queue!.TryEnqueue(request))
        {
            request.Status = QuestionStatus.Failed;
            request.Error = "Question queue is full.";
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
            if (Session != session) throw new InvalidOperationException("Conversation has ended.");
            if (transcriptSegmentId is not null &&
                session.Answers.FirstOrDefault(x => x.TranscriptSegmentId == transcriptSegmentId) is { } existing)
                return existing;
            session.Answers.Add(request);
            QueueLocked(request);
        }
        AnswerUpdated?.Invoke(request);
        return request;
    }

    private async Task ProcessQueueAsync(ConversationSession session, QuestionQueue queue, CancellationToken token)
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
                if (request.Status != QuestionStatus.Pending) continue;
                request.Status = QuestionStatus.Processing;
                AnswerUpdated?.Invoke(request);
                lock (_gate) _workIqBusy = true;
                if (Session == session && !_paused) SetState(ConversationUiState.Thinking);
                try
                {
                    if (!_auth.IsSignedIn)
                        throw new InvalidOperationException("Sign in with Microsoft before asking Work IQ.");
                    if (!_network.IsAvailable)
                        throw new System.Net.Http.HttpRequestException("Work IQ offline. Check your network.");
                    var prompt = _contextBuilder.Build(request.Question, request.ContextUsed, session.Settings);
                    var answer = await _workIq.AskAsync(prompt, session.WorkIqConversationId, token)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(answer.Text))
                        throw new WorkIqException("Work IQ returned an empty answer. Retry this question.");
                    request.Answer = answer.Text;
                    request.Sources = answer.Sources;
                    session.WorkIqConversationId = answer.ConversationId ?? session.WorkIqConversationId;
                    request.Status = QuestionStatus.Completed;
                    if (Session == session && !_paused) SetState(ConversationUiState.Answering);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    request.Status = QuestionStatus.Cancelled;
                }
                catch (Exception ex) when (ex is WorkIqException or
                    System.Net.Http.HttpRequestException or InvalidOperationException or TimeoutException)
                {
                    request.Status = QuestionStatus.Failed;
                    request.Error = ex.Message;
                    ErrorOccurred?.Invoke(ex.Message);
                }
                finally
                {
                    AnswerUpdated?.Invoke(request);
                    lock (_gate) _workIqBusy = false;
                    if (Session == session)
                        SetState(_paused ? State == ConversationUiState.Error ? ConversationUiState.Error : ConversationUiState.Paused :
                            request.Error?.Contains("offline", StringComparison.OrdinalIgnoreCase) == true ?
                            ConversationUiState.Offline : ConversationUiState.Listening);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        foreach (var request in session.Answers.Where(x => x.Status == QuestionStatus.Pending))
        {
            request.Status = QuestionStatus.Cancelled;
            AnswerUpdated?.Invoke(request);
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
        ConversationSession session;
        IConversationBuffer buffer;
        lock (_gate)
        {
            if (Session is null || _paused) return;
            session = Session;
            buffer = _buffer!;
        }
        var context = buffer.GetContextBefore(speech.Start);
        var segment = _transcript.CommitFinal(speech);
        if (Session != session) return;
        session.TranscriptSegments.Add(segment);
        buffer.Add(segment);
        if (session.Settings.AutomaticQuestions)
        {
            var detected = _detector.Detect(segment, context, session.Settings.Sensitivity,
                session.Settings.Language);
            if (detected.IsQuestion)
            {
                session.DetectedQuestions.Add(detected);
                if (_duplicates.Accept(detected.QuestionText, detected.Timestamp))
                {
                    SetState(ConversationUiState.QuestionDetected);
                    var relevantContext = string.Join(Environment.NewLine,
                        new[] { context, detected.ContextPrefix }
                            .Where(part => !string.IsNullOrWhiteSpace(part)));
                    if (relevantContext.Length > session.Settings.MaxContextCharacters)
                        relevantContext = relevantContext[^session.Settings.MaxContextCharacters..];
                    Enqueue(session, detected.QuestionText, relevantContext, false, segment.Id);
                    return;
                }
                QuestionSuppressed?.Invoke();
            }
        }
        if (!_workIqBusy) SetState(ConversationUiState.Listening);
    }

    private void OnFailure(Exception error)
    {
        if (error is SpeechConnectionException)
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
