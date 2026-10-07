using System.Runtime.InteropServices;
using ConversationAssistant.Core.Audio;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant_App.Audio;

public sealed class AudioCaptureService : IAudioCaptureService, IDisposable
{
    private static readonly UiText Texts = UiText.For(ConversationLanguage.English);
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(100);
    private const int MaximumFramesPerWake = Pcm16AudioMixer.MaximumBufferedSamples / Pcm16AudioMixer.SamplesPerFrame;
    private readonly object _lifecycle = new();
    private readonly ISharedAudioDeviceFactory _devices;
    private readonly TimeProvider _clock;
    private CaptureRun? _active;
    private bool _disposed;

    public AudioCaptureService() : this(new WasapiAudioDeviceFactory(), TimeProvider.System) { }

    internal AudioCaptureService(ISharedAudioDeviceFactory devices, TimeProvider clock)
    {
        _devices = devices;
        _clock = clock;
    }

    public event Action<byte[]>? AudioDataAvailable;
    public event Action<Exception>? AudioError;
    public event Action? AudioStarted;
    public event Action? AudioStopped;
    public event Action? AudioDeviceChanged;

    public IReadOnlyList<AudioDevice> ListDevices(AudioDeviceKind kind = AudioDeviceKind.Input) =>
        _devices.ListDevices(kind);

    public void Start(AudioCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) throw new AudioCaptureException(Texts["ErrorAudioAlreadyActive"]);
            var run = new CaptureRun(options);
            _active = run;
            try
            {
                if (options.IncludesMicrophone) AddSource(run, AudioDeviceKind.Input, options.InputDeviceId);
                if (options.IncludesSystemAudio) AddSource(run, AudioDeviceKind.Output, options.OutputDeviceId);
                foreach (var source in run.Sources) source.Endpoint.Start();
                if (run.Failure is { } failure) throw failure;
                var startedAt = _clock.GetTimestamp();
                ObserveFailure(run, Task.Run(() => PumpAsync(run, startedAt)));
                AudioStarted?.Invoke();
            }
            catch (Exception startupError)
            {
                if (ReferenceEquals(_active, run))
                {
                    _active = null;
                    try { Release(run); }
                    catch (AudioCaptureException cleanupError)
                    {
                        throw new AudioCaptureException(Texts["ErrorAudioRelease"],
                            new AggregateException(startupError, cleanupError));
                    }
                }
                throw;
            }
        }
    }

    private void AddSource(CaptureRun run, AudioDeviceKind kind, string? selectedId)
    {
        var devices = _devices.ListDevices(kind);
        var endpoint = _devices.Open(kind, selectedId);
        void OnData(ReadOnlyMemory<byte> bytes)
        {
            if (run.Token.IsCancellationRequested) return;
            try { run.Mixer.Add(kind, bytes.Span); }
            catch (AudioCaptureException error) { Fail(run, error); }
        }
        void OnStopped(Exception? error) => Fail(run, error is null
            ? new AudioCaptureException(Texts["ErrorAudioStopped"])
            : new AudioCaptureException(Texts.Format("ErrorAudioStoppedCode", $"0x{error.HResult:X8}"), error));
        run.Sources.Add(new CaptureSource(kind, selectedId ?? "default", endpoint, devices, OnData, OnStopped));
        endpoint.DataAvailable += OnData;
        endpoint.Stopped += OnStopped;
    }

    private async Task PumpAsync(CaptureRun run, long startedAt)
    {
        using var timer = new PeriodicTimer(FrameDuration, _clock);
        long framesDelivered = 0;
        var lastDeviceCheck = _clock.GetUtcNow();
        try
        {
            while (!run.Token.IsCancellationRequested &&
                await timer.WaitForNextTickAsync(run.Token).ConfigureAwait(false))
            {
                // Timer ticks can be late or coalesced; sample duration follows elapsed time, not tick count.
                var elapsedFrames = _clock.GetElapsedTime(startedAt).Ticks / FrameDuration.Ticks;
                var framesDue = (int)Math.Clamp(elapsedFrames - framesDelivered, 0, MaximumFramesPerWake);
                for (var frame = 0; frame < framesDue; frame++)
                {
                    lock (_lifecycle)
                    {
                        if (run.Token.IsCancellationRequested || !ReferenceEquals(_active, run)) return;
                        // Serialize delivery with Stop/Start so a stopped run cannot feed a new speech session.
                        framesDelivered++;
                        AudioDataAvailable?.Invoke(run.Mixer.ReadFrame());
                    }
                }
                lock (_lifecycle)
                {
                    if (run.Token.IsCancellationRequested) return;
                    if (_clock.GetUtcNow() - lastDeviceCheck < TimeSpan.FromSeconds(3) ||
                        !run.DeviceCheck.IsCompletedSuccessfully) continue;
                    lastDeviceCheck = _clock.GetUtcNow();
                    run.DeviceCheck = Task.Run(() => CheckDevices(run));
                    ObserveFailure(run, run.DeviceCheck);
                }
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
    }

    private void CheckDevices(CaptureRun run)
    {
        foreach (var source in run.Sources)
        {
            if (run.Token.IsCancellationRequested) return;
            // Endpoint enumeration can take hundreds of milliseconds; never hold the delivery lock while querying it.
            var devices = _devices.ListDevices(source.Kind);
            lock (_lifecycle)
            {
                if (run.Token.IsCancellationRequested || !ReferenceEquals(_active, run)) return;
                if (!devices.SequenceEqual(source.Devices))
                {
                    source.Devices = devices;
                    AudioDeviceChanged?.Invoke();
                    if (run.Token.IsCancellationRequested) return;
                }
                if (devices.FirstOrDefault(device => device.Id == source.SelectedId)?.EffectiveId != source.Endpoint.EndpointId)
                    throw new AudioCaptureException(Texts["ErrorAudioDeviceChanged"]);
            }
        }
    }

    private void ObserveFailure(CaptureRun run, Task worker)
    {
        _ = worker.ContinueWith(task =>
        {
            var error = task.Exception!.GetBaseException();
            Fail(run, error is AudioCaptureException ? error :
                new AudioCaptureException(Texts.Format("ErrorAudioStoppedCode", $"0x{error.HResult:X8}"), error));
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void Fail(CaptureRun run, Exception error)
    {
        if (run.Token.IsCancellationRequested || Interlocked.CompareExchange(ref run.Failure, error, null) is not null)
            return;
        run.RequestStop();
        // Native Dispose joins its capture thread, so never release endpoints from their own callbacks.
        _ = Task.Run(() =>
        {
            lock (_lifecycle)
            {
                if (!ReferenceEquals(_active, run)) return;
                _active = null;
                try { Release(run); }
                catch (AudioCaptureException cleanup)
                {
                    error = new AudioCaptureException(Texts["ErrorAudioRelease"], new AggregateException(error, cleanup));
                }
                AudioError?.Invoke(error);
                if (_active is null) AudioStopped?.Invoke();
            }
        });
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (_active is not { } run) return;
            _active = null;
            try { Release(run); }
            finally { AudioStopped?.Invoke(); }
        }
    }

    private static void Release(CaptureRun run)
    {
        run.RequestStop();
        foreach (var source in run.Sources)
        {
            source.Endpoint.DataAvailable -= source.OnData;
            source.Endpoint.Stopped -= source.OnStopped;
        }
        // The pump checks cancellation under the lifecycle lock; joining it here would deadlock callback-driven Stop.
        var failures = new List<Exception>();
        try
        {
            foreach (var source in run.Sources)
            {
                try { source.Endpoint.Dispose(); }
                catch (Exception error) when (error is InvalidOperationException or COMException or IOException)
                {
                    failures.Add(error);
                }
            }
        }
        finally
        {
            run.Mixer.Clear();
            run.DisposeCancellation();
        }
        if (failures.Count > 0)
            throw new AudioCaptureException(Texts["ErrorAudioRelease"], new AggregateException(failures));
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            Stop();
        }
    }

    private sealed class CaptureRun
    {
        private readonly object _stopGate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private bool _released;
        public readonly Pcm16AudioMixer Mixer;
        public CancellationToken Token { get; }
        public readonly List<CaptureSource> Sources = [];
        public Task DeviceCheck = Task.CompletedTask;
        public Exception? Failure;

        public CaptureRun(AudioCaptureOptions options)
        {
            Mixer = new Pcm16AudioMixer(options);
            Token = _cancellation.Token;
        }

        public void RequestStop()
        {
            lock (_stopGate)
                if (!_released) _cancellation.Cancel();
        }

        public void DisposeCancellation()
        {
            lock (_stopGate)
            {
                _released = true;
                _cancellation.Dispose();
            }
        }
    }

    private sealed class CaptureSource(AudioDeviceKind kind, string selectedId, ISharedAudioEndpoint endpoint,
        IReadOnlyList<AudioDevice> devices, Action<ReadOnlyMemory<byte>> onData, Action<Exception?> onStopped)
    {
        public AudioDeviceKind Kind { get; } = kind;
        public string SelectedId { get; } = selectedId;
        public ISharedAudioEndpoint Endpoint { get; } = endpoint;
        public IReadOnlyList<AudioDevice> Devices { get; set; } = devices;
        public Action<ReadOnlyMemory<byte>> OnData { get; } = onData;
        public Action<Exception?> OnStopped { get; } = onStopped;
    }
}
