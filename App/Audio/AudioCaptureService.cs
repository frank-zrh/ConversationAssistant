using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using NAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ConversationAssistant_App.Audio;

public sealed class AudioCaptureService : IAudioCaptureService, IDisposable
{
    private readonly object _gate = new();
    private WaveInEvent? _capture;
    private Timer? _deviceTimer;
    private string _deviceSignature = "";
    private string? _selectedName;
    private string? _defaultEndpointId;

    public event Action<byte[]>? AudioDataAvailable;
    public event Action<Exception>? AudioError;
    public event Action? AudioStarted;
    public event Action? AudioStopped;
    public event Action? AudioDeviceChanged;

    public IReadOnlyList<AudioDevice> ListDevices()
    {
        if (WaveIn.DeviceCount == 0) return [];
        return [new AudioDevice("default", "Default microphone"),
            .. Enumerable.Range(0, WaveIn.DeviceCount)
                .Select(i => new AudioDevice(i.ToString(), WaveIn.GetCapabilities(i).ProductName))];
    }

    public void Start(string? deviceId)
    {
        var devices = ListDevices();
        if (devices.Count == 0) throw new InvalidOperationException(
            "No microphone found. Connect one and grant Windows microphone access.");
        var selected = deviceId is null ? devices[0] :
            devices.FirstOrDefault(x => x.Id == deviceId) ??
            throw new InvalidOperationException("Selected microphone is no longer connected.");
        var signature = GetSignature(devices);
        var defaultId = selected.Id == "default" ? GetDefaultEndpointId() : null;
        lock (_gate)
        {
            if (_capture is not null) throw new InvalidOperationException("Microphone is already active.");
            var capture = new WaveInEvent
            {
                DeviceNumber = selected.Id == "default" ? -1 : int.Parse(selected.Id),
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            try { capture.StartRecording(); }
            catch
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                throw;
            }
            _capture = capture;
            _selectedName = selected.Name;
            _defaultEndpointId = defaultId;
            _deviceSignature = signature;
            _deviceTimer = new Timer(CheckDevices, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }
        AudioStarted?.Invoke();
    }

    public void Stop()
    {
        WaveInEvent? capture;
        lock (_gate)
        {
            _deviceTimer?.Dispose();
            _deviceTimer = null;
            capture = _capture;
            _capture = null;
            _selectedName = null;
            _defaultEndpointId = null;
        }
        if (capture is null) return;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        try { capture.StopRecording(); }
        finally
        {
            capture.Dispose();
            AudioStopped?.Invoke();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var audio = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, audio, 0, audio.Length);
        try { AudioDataAvailable?.Invoke(audio); }
        catch (InvalidOperationException ex) { AudioError?.Invoke(ex); }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            AudioError?.Invoke(e.Exception);
            Stop();
        }
    }

    private void CheckDevices(object? state)
    {
        IReadOnlyList<AudioDevice> devices;
        string signature;
        try
        {
            devices = ListDevices();
            signature = GetSignature(devices);
        }
        catch (Exception ex) when (ex is MmException or System.Runtime.InteropServices.COMException)
        {
            Stop();
            AudioError?.Invoke(ex);
            return;
        }
        if (signature == _deviceSignature) return;
        _deviceSignature = signature;
        AudioDeviceChanged?.Invoke();
        if ((_defaultEndpointId is not null && _defaultEndpointId != GetDefaultEndpointId()) ||
            (_selectedName is not null && !devices.Any(x => x.Name == _selectedName)))
        {
            Stop();
            AudioError?.Invoke(new InvalidOperationException(
                "Microphone changed or disconnected. Pause and resume after selecting an input."));
        }
    }

    private static string GetSignature(IReadOnlyList<AudioDevice> devices) =>
        string.Join("\u001f", devices.Select(x => x.Name)) +
        (devices.Count > 0 ? GetDefaultEndpointId() : "");

    private static string GetDefaultEndpointId()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        return endpoint.ID;
    }

    public void Dispose() => Stop();
}
