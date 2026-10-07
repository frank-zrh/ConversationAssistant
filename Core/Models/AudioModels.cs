namespace ConversationAssistant.Core.Models;

public enum AudioCaptureMode { Microphone, SystemAudio, MicrophoneAndSystemAudio }
public enum AudioDeviceKind { Input, Output }

public sealed record AudioCaptureOptions(
    AudioCaptureMode Mode = AudioCaptureMode.Microphone,
    string? InputDeviceId = null,
    string? OutputDeviceId = null)
{
    public bool IncludesMicrophone => Mode is AudioCaptureMode.Microphone or AudioCaptureMode.MicrophoneAndSystemAudio;
    public bool IncludesSystemAudio => Mode is AudioCaptureMode.SystemAudio or AudioCaptureMode.MicrophoneAndSystemAudio;

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
    }
}
