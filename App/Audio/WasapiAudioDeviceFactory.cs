using System.Diagnostics;
using System.Runtime.InteropServices;
using ConversationAssistant.Core.Audio;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ConversationAssistant_App.Audio;

internal interface ISharedAudioEndpoint : IDisposable
{
    string EndpointId { get; }
    event Action<ReadOnlyMemory<byte>>? DataAvailable;
    event Action<Exception?>? Stopped;
    void Start();
}

internal interface ISharedAudioDeviceFactory
{
    IReadOnlyList<AudioDevice> ListDevices(AudioDeviceKind kind);
    ISharedAudioEndpoint Open(AudioDeviceKind kind, string? selectedId);
}

internal sealed class WasapiAudioDeviceFactory : ISharedAudioDeviceFactory
{
    private static readonly UiText Texts = UiText.For(ConversationLanguage.English);

    public IReadOnlyList<AudioDevice> ListDevices(AudioDeviceKind kind)
    {
        var flow = GetFlow(kind);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var result = new List<AudioDevice>();
            foreach (var (id, role, key) in new[]
            {
                ("default", Role.Multimedia, kind == AudioDeviceKind.Input ? "DefaultMicrophone" : "DefaultOutput"),
                ("communications", Role.Communications,
                    kind == AudioDeviceKind.Input ? "DefaultCommunicationsMicrophone" : "DefaultCommunicationsOutput")
            })
            {
                if (!enumerator.HasDefaultAudioEndpoint(flow, role)) continue;
                using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
                result.Add(new AudioDevice(id, Texts[key], device.ID));
            }
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) result.Add(new AudioDevice(device.ID, device.FriendlyName));
            }
            return result;
        }
        catch (COMException error)
        {
            throw OpenFailure(error);
        }
    }

    public ISharedAudioEndpoint Open(AudioDeviceKind kind, string? selectedId)
    {
        var id = selectedId ?? "default";
        var selected = ListDevices(kind).FirstOrDefault(device => device.Id == id)
            ?? throw new AudioCaptureException(Texts[kind == AudioDeviceKind.Input
                ? "ErrorAudioInputUnavailable" : "ErrorAudioOutputUnavailable"]);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(selected.EffectiveId);
            WasapiCapture? capture = null;
            try
            {
                capture = kind == AudioDeviceKind.Input
                    ? new WasapiCapture(device) : new WasapiLoopbackCapture(device);
                capture.ShareMode = AudioClientShareMode.Shared;
                // WASAPI converts this client's stream, without changing the endpoint's shared mix format.
                capture.WaveFormat = new WaveFormat(Pcm16AudioMixer.SampleRate, 16, 1);
                return new SharedWasapiEndpoint(device, capture);
            }
            catch
            {
                try { capture?.Dispose(); }
                finally { device.Dispose(); }
                throw;
            }
        }
        catch (COMException error)
        {
            throw OpenFailure(error);
        }
    }

    private static DataFlow GetFlow(AudioDeviceKind kind) => kind switch
    {
        AudioDeviceKind.Input => DataFlow.Capture,
        AudioDeviceKind.Output => DataFlow.Render,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static AudioCaptureException OpenFailure(Exception error) => new(
        Texts.Format("ErrorAudioSharedOpen", $"0x{error.HResult:X8}"), error);

    private sealed class SharedWasapiEndpoint : ISharedAudioEndpoint
    {
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private Exception? _startError;
        public string EndpointId => _device.ID;
        public event Action<ReadOnlyMemory<byte>>? DataAvailable;
        public event Action<Exception?>? Stopped;

        public SharedWasapiEndpoint(MMDevice device, WasapiCapture capture)
        {
            _device = device;
            _capture = capture;
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
        }

        public void Start()
        {
            try
            {
                _capture.StartRecording();
                var wait = Stopwatch.StartNew();
                while (_capture.CaptureState == CaptureState.Starting && wait.Elapsed < TimeSpan.FromSeconds(5))
                    Thread.Sleep(10);
                if (_capture.CaptureState != CaptureState.Capturing)
                {
                    if (Volatile.Read(ref _startError) is { } error) throw OpenFailure(error);
                    throw new AudioCaptureException(Texts[_capture.CaptureState == CaptureState.Starting
                        ? "ErrorAudioStartTimeout" : "ErrorAudioStopped"]);
                }
            }
            catch (COMException error) { throw OpenFailure(error); }
        }

        private void OnData(object? sender, WaveInEventArgs args)
        {
            if (args.BytesRecorded > 0) DataAvailable?.Invoke(args.Buffer.AsMemory(0, args.BytesRecorded));
        }

        private void OnStopped(object? sender, StoppedEventArgs args)
        {
            Volatile.Write(ref _startError, args.Exception);
            Stopped?.Invoke(args.Exception);
        }

        public void Dispose()
        {
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnStopped;
            try { _capture.Dispose(); }
            finally { _device.Dispose(); }
        }
    }
}
