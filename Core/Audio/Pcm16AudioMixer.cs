using System.Buffers.Binary;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Audio;

public sealed class Pcm16AudioMixer
{
    public const int SampleRate = 16000;
    public const int SamplesPerFrame = SampleRate / 10;
    public const int FrameBytes = SamplesPerFrame * sizeof(short);
    public const int MaximumBufferedSamples = SampleRate * 2;
    private readonly object _gate = new();
    private readonly Dictionary<AudioDeviceKind, Queue<short>> _buffers = [];

    public Pcm16AudioMixer(AudioCaptureOptions options)
    {
        options.Validate();
        if (options.IncludesMicrophone) _buffers.Add(AudioDeviceKind.Input, new());
        if (options.IncludesSystemAudio) _buffers.Add(AudioDeviceKind.Output, new());
    }

    public void Add(AudioDeviceKind source, ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(source, out var buffer))
                throw new ArgumentOutOfRangeException(nameof(source), "This audio source was not selected.");
            if (pcm.Length % sizeof(short) != 0)
                throw new AudioCaptureException("The audio device returned an incomplete 16-bit PCM frame.");
            if (pcm.Length / sizeof(short) > MaximumBufferedSamples - buffer.Count)
                throw new AudioCaptureException("Audio capture is falling behind. Pause and resume after checking system load.");
            for (var offset = 0; offset < pcm.Length; offset += sizeof(short))
                buffer.Enqueue(BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]));
        }
    }

    public byte[] ReadFrame()
    {
        var result = new byte[FrameBytes];
        lock (_gate)
        {
            for (var index = 0; index < SamplesPerFrame; index++)
            {
                var sum = 0;
                foreach (var buffer in _buffers.Values)
                    if (buffer.TryDequeue(out var sample)) sum += sample;
                BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(index * sizeof(short)),
                    (short)Math.Clamp(sum, short.MinValue, short.MaxValue));
            }
        }
        return result;
    }

    public void Clear()
    {
        lock (_gate)
            foreach (var buffer in _buffers.Values) buffer.Clear();
    }
}
