using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ConversationAssistant.Core.Audio;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant_App.Audio;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class AudioCaptureTests
{
    [TestMethod]
    public void SystemCaptureRequiresExplicitSelection()
    {
        var settings = new ConversationSettings();
        Assert.AreEqual(AudioCaptureMode.Microphone, settings.AudioMode);
        Assert.IsTrue(settings.AudioCapture.IncludesMicrophone);
        Assert.IsFalse(settings.AudioCapture.IncludesSystemAudio);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioCaptureOptions((AudioCaptureMode)99).Validate());
    }

    [TestMethod]
    public void MixingCombinesSimultaneousSourcesWithoutDoublingDurationOrAttenuatingOneSource()
    {
        var mixer = new Pcm16AudioMixer(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        mixer.Add(AudioDeviceKind.Input, Pcm(1000));
        mixer.Add(AudioDeviceKind.Output, Pcm(2000));
        var frame = mixer.ReadFrame();
        Assert.HasCount(3200, frame);
        AssertSamples(frame, 3000);
        mixer.Add(AudioDeviceKind.Input, Pcm(1000));
        AssertSamples(mixer.ReadFrame(), 1000);
        mixer.Add(AudioDeviceKind.Output, Pcm(2000));
        AssertSamples(mixer.ReadFrame(), 2000);
        AssertSamples(mixer.ReadFrame(), 0);
    }

    [TestMethod]
    [DataRow(30000, 30000, short.MaxValue)]
    [DataRow(-30000, -30000, short.MinValue)]
    [DataRow(30000, -30000, 0)]
    public void MixingSaturatesWithoutIntegerWraparound(int first, int second, int expected)
    {
        var mixer = new Pcm16AudioMixer(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        mixer.Add(AudioDeviceKind.Input, Pcm((short)first));
        mixer.Add(AudioDeviceKind.Output, Pcm((short)second));
        AssertSamples(mixer.ReadFrame(), (short)expected);
    }

    [TestMethod]
    public void DifferentCallbackSizesPreserveSampleOrderAndFillMissingPlaybackWithSilence()
    {
        var mixer = new Pcm16AudioMixer(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        mixer.Add(AudioDeviceKind.Input, Pcm(10, 500));
        mixer.Add(AudioDeviceKind.Input, Pcm(20, 1100));
        mixer.Add(AudioDeviceKind.Output, Pcm(5, 800));
        var samples = Samples(mixer.ReadFrame());
        Assert.IsTrue(samples.Take(500).All(sample => sample == 15));
        Assert.IsTrue(samples.Skip(500).Take(300).All(sample => sample == 25));
        Assert.IsTrue(samples.Skip(800).All(sample => sample == 20));
        AssertSamples(mixer.ReadFrame(), 0);
        mixer.Add(AudioDeviceKind.Output, Pcm(40));
        AssertSamples(mixer.ReadFrame(), 40);
    }

    [TestMethod]
    public void BuffersAreBoundedAndMalformedInputFailsRatherThanDroppingAudioSilently()
    {
        var mixer = new Pcm16AudioMixer(new());
        Assert.Throws<AudioCaptureException>(() => mixer.Add(AudioDeviceKind.Input, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.Add(AudioDeviceKind.Output, [1, 2]));
        mixer.Add(AudioDeviceKind.Input, Pcm(7, Pcm16AudioMixer.MaximumBufferedSamples));
        Assert.Throws<AudioCaptureException>(() => mixer.Add(AudioDeviceKind.Input, [1, 2]));
        AssertSamples(mixer.ReadFrame(), 7);
        mixer.Clear();
        AssertSamples(mixer.ReadFrame(), 0);
    }

    [TestMethod]
    public void OneMinuteOfBothSourcesProducesExactlyOneMinuteOfSpeechPcm()
    {
        var mixer = new Pcm16AudioMixer(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        var input = Pcm(10);
        var output = Pcm(15);
        var bytes = 0;
        for (var frame = 0; frame < 600; frame++)
        {
            mixer.Add(AudioDeviceKind.Input, input);
            if (frame % 2 == 0) mixer.Add(AudioDeviceKind.Output, output);
            var mixed = mixer.ReadFrame();
            bytes += mixed.Length;
            AssertSamples(mixed, (short)(frame % 2 == 0 ? 25 : 10));
        }
        Assert.AreEqual(60 * 16000 * 2, bytes);
    }

    [TestMethod]
    [DataRow(AudioCaptureMode.Microphone, 1, 0)]
    [DataRow(AudioCaptureMode.SystemAudio, 0, 1)]
    [DataRow(AudioCaptureMode.MicrophoneAndSystemAudio, 1, 1)]
    public void OnlySelectedSourcesAreOpenedAndAllAreReleased(AudioCaptureMode mode, int microphones, int outputs)
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        service.Start(new(mode, "mic-id", "output-id"));
        Assert.AreEqual(microphones, devices.Opened.Count(endpoint => endpoint.Kind == AudioDeviceKind.Input));
        Assert.AreEqual(outputs, devices.Opened.Count(endpoint => endpoint.Kind == AudioDeviceKind.Output));
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Starts == 1));
        Assert.IsTrue(devices.Selections.All(selection => selection.Id ==
            (selection.Kind == AudioDeviceKind.Input ? "mic-id" : "output-id")));
        service.Stop();
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
    }

    [TestMethod]
    public async Task SystemOnlyWorksWithoutAMicrophoneAndStillSuppliesSilence()
    {
        var devices = new FakeDevices { MicrophonePresent = false };
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        var frame = FirstFrame(service);
        service.Start(new(AudioCaptureMode.SystemAudio));
        AssertSamples(await frame.WaitAsync(TimeSpan.FromSeconds(5)), 0);
        Assert.HasCount(1, devices.Opened);
        Assert.AreEqual(AudioDeviceKind.Output, devices.Opened.Single().Kind);
    }

    [TestMethod]
    public async Task ServiceMixesSourcesOnOneClockWithoutWaitingForLoopbackPackets()
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        var mixed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioDataAvailable += bytes =>
        {
            if (Samples(bytes).All(sample => sample == 3000)) mixed.TrySetResult(bytes);
        };
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        devices.Endpoint(AudioDeviceKind.Input).Emit(Pcm(1000));
        devices.Endpoint(AudioDeviceKind.Output).Emit(Pcm(2000));
        AssertSamples(await mixed.Task.WaitAsync(TimeSpan.FromSeconds(5)), 3000);
        var inputOnly = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioDataAvailable += bytes =>
        {
            if (Samples(bytes).All(sample => sample == 1000)) inputOnly.TrySetResult(bytes);
        };
        devices.Endpoint(AudioDeviceKind.Input).Emit(Pcm(1000));
        AssertSamples(await inputOnly.Task.WaitAsync(TimeSpan.FromSeconds(5)), 1000);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PartialStartupFailureReleasesBothSourcesAndAllowsRetry(bool failOnOpen)
    {
        var devices = new FakeDevices
        {
            FailOpen = failOnOpen ? AudioDeviceKind.Output : null,
            FailStart = failOnOpen ? null : AudioDeviceKind.Output
        };
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        var started = 0;
        service.AudioStarted += () => started++;
        Assert.Throws<AudioCaptureException>(() => service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio)));
        Assert.AreEqual(0, started);
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
        devices.FailOpen = null;
        devices.FailStart = null;
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        Assert.AreEqual(1, started);
    }

    [TestMethod]
    [DataRow(AudioDeviceKind.Input, true)]
    [DataRow(AudioDeviceKind.Output, true)]
    [DataRow(AudioDeviceKind.Output, false)]
    public async Task UnexpectedStopReleasesBothSourcesAndReportsOnceOffTheCaptureThread(AudioDeviceKind failed, bool error)
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        var failures = 0;
        service.AudioError += _ => Interlocked.Increment(ref failures);
        var failure = NextError(service);
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        devices.Endpoint(failed).EmitStopped(error ? new COMException("Simulated device loss", unchecked((int)0x88890004)) : null);
        Assert.IsInstanceOfType<AudioCaptureException>(await failure.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1 && !endpoint.DisposedOnCallbackThread));
        Assert.AreEqual(1, failures);
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
    }

    [TestMethod]
    public async Task AStopDuringStartupCannotReportSuccessfulCapture()
    {
        var devices = new FakeDevices { StopOnStart = AudioDeviceKind.Output };
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        Assert.Throws<AudioCaptureException>(() => service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio)));
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
        await Task.Delay(50);
        devices.StopOnStart = null;
        service.Start(new());
    }

    [TestMethod]
    public async Task StopIgnoresLateCallbacksAndNewCaptureDoesNotReuseOldAudio()
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        service.Start(new());
        var old = devices.Endpoint(AudioDeviceKind.Input);
        var lateData = old.DataCallback;
        var lateStop = old.StoppedCallback;
        old.Emit(Pcm(7000));
        service.Stop();
        var failures = 0;
        service.AudioError += _ => failures++;
        var frame = FirstFrame(service);
        service.Start(new());
        lateData?.Invoke(Pcm(9000));
        lateStop?.Invoke(new InvalidOperationException("Stale callback"));
        AssertSamples(await frame.WaitAsync(TimeSpan.FromSeconds(5)), 0);
        Assert.AreEqual(0, failures);
        Assert.AreEqual(1, old.Disposals);
    }

    [TestMethod]
    [DataRow(AudioDeviceKind.Input)]
    [DataRow(AudioDeviceKind.Output)]
    public async Task DefaultEndpointChangesStopBothSourcesInsteadOfSwitchingSilently(AudioDeviceKind kind)
    {
        var clock = new DeviceClock();
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, clock);
        var frame = FirstFrame(service);
        var failure = NextError(service);
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        await frame.WaitAsync(TimeSpan.FromSeconds(5));
        if (kind == AudioDeviceKind.Input) devices.DefaultInputId = "another-mic";
        else devices.DefaultOutputId = "another-output";
        clock.Advance();
        var error = await failure.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(error.Message, "changed or disconnected");
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
    }

    [TestMethod]
    public async Task DeviceRenameDoesNotInterruptAStableEndpointSelection()
    {
        var clock = new DeviceClock();
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, clock);
        var frame = FirstFrame(service);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioDeviceChanged += () => changed.TrySetResult();
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio, "mic-id", "output-id"));
        await frame.WaitAsync(TimeSpan.FromSeconds(5));
        devices.InputName = "Renamed device";
        clock.Advance();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 0));
    }

    [TestMethod]
    public void CleanupAttemptsEveryEndpointEvenWhenOneReleaseFails()
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        devices.Endpoint(AudioDeviceKind.Input).FailDispose = true;
        Assert.Throws<AudioCaptureException>(service.Stop);
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
        service.Start(new());
    }

    [TestMethod]
    public void StartingTwiceDoesNotReplaceTheActiveSources()
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        service.Start(new());
        Assert.Throws<AudioCaptureException>(() => service.Start(new(AudioCaptureMode.SystemAudio)));
        Assert.HasCount(1, devices.Opened);
        Assert.AreEqual(0, devices.Opened.Single().Disposals);
    }

    [TestMethod]
    public async Task AFrameCallbackCanStopAndRestartWithoutDeadlockingOrDeliveringOldFrames()
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextFrame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        service.AudioDataAvailable += bytes =>
        {
            if (++callbacks != 1)
            {
                nextFrame.TrySetResult(bytes);
                return;
            }
            service.Stop();
            service.Start(new(AudioCaptureMode.SystemAudio));
            restarted.TrySetResult();
        };
        service.Start(new());
        devices.Endpoint(AudioDeviceKind.Input).Emit(Pcm(7000, Pcm16AudioMixer.SamplesPerFrame * 2));
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertSamples(await nextFrame.Task.WaitAsync(TimeSpan.FromSeconds(5)), 0);
        Assert.AreEqual(1, devices.Endpoint(AudioDeviceKind.Input).Disposals);
        Assert.AreEqual(0, devices.Endpoint(AudioDeviceKind.Output).Disposals);
    }

    [TestMethod]
    public async Task ConcurrentStopAndCallbackStopDoNotWaitOnEachOther()
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var externalStopEntered = new ManualResetEventSlim();
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioDataAvailable += _ =>
        {
            callbackEntered.TrySetResult();
            Assert.IsTrue(externalStopEntered.Wait(TimeSpan.FromSeconds(5)));
            service.Stop();
            callbackCompleted.TrySetResult();
        };
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var externalStop = Task.Run(() =>
        {
            externalStopEntered.Set();
            service.Stop();
        });
        await Task.WhenAll(callbackCompleted.Task, externalStop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
    }

    [TestMethod]
    public async Task UnexpectedConsumerExceptionsAreReportedAndReleaseAllSources()
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        var failure = NextError(service);
        service.AudioDataAvailable += _ => throw new ArgumentException("Simulated consumer failure");
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        var error = await failure.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsInstanceOfType<AudioCaptureException>(error);
        Assert.IsInstanceOfType<ArgumentException>(error.InnerException);
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
    }

    [TestMethod]
    public async Task DeviceChangeCallbackCanStopWithoutAccessingReleasedEndpoints()
    {
        var devices = new FakeDevices();
        var clock = new DeviceClock();
        using var service = new AudioCaptureService(devices, clock);
        var frame = FirstFrame(service);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        service.AudioError += errors.Enqueue;
        service.AudioDeviceChanged += () =>
        {
            service.Stop();
            stopped.TrySetResult();
        };
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        await frame.WaitAsync(TimeSpan.FromSeconds(5));
        devices.InputName = "Renamed microphone";
        clock.Advance();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void StartupAndCleanupFailuresAreBothPreservedAndAllSourcesAreReleased()
    {
        var devices = new FakeDevices { FailStart = AudioDeviceKind.Output, FailDispose = AudioDeviceKind.Input };
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        var error = Assert.Throws<AudioCaptureException>(() => service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio)));
        Assert.IsInstanceOfType<AggregateException>(error.InnerException);
        var causes = ((AggregateException)error.InnerException).Flatten().InnerExceptions;
        Assert.IsTrue(causes.Any(cause => cause.Message == "Simulated start failure"));
        Assert.IsTrue(causes.Any(cause => cause.GetBaseException().Message == "Simulated release failure"));
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
        devices.FailStart = null;
        devices.FailDispose = null;
        service.Start(new());
    }

    [TestMethod]
    public async Task StartedCallbackCanDisposeWithoutLeavingARecordingOrEmittingFrames()
    {
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, TimeProvider.System);
        var frames = 0;
        service.AudioStarted += service.Dispose;
        service.AudioDataAvailable += _ => Interlocked.Increment(ref frames);
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        await Task.Delay(200);
        Assert.AreEqual(0, frames);
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
        Assert.Throws<ObjectDisposedException>(() => service.Start(new()));
    }

    [TestMethod]
    [DataRow(AudioCaptureMode.Microphone, 1000)]
    [DataRow(AudioCaptureMode.SystemAudio, 2000)]
    [DataRow(AudioCaptureMode.MicrophoneAndSystemAudio, 3000)]
    public async Task CoalescedTimerTicksPreserveFiveMinutesOfPcmWithoutBacklog(AudioCaptureMode mode, int expected)
    {
        var clock = new CoalescingAudioClock();
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, clock);
        var frames = 0;
        long bytesDelivered = 0;
        var batch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioDataAvailable += bytes =>
        {
            Assert.HasCount(Pcm16AudioMixer.FrameBytes, bytes);
            AssertSamples(bytes, (short)expected);
            bytesDelivered += bytes.Length;
            if (++frames % 5 == 0) batch.TrySetResult();
        };
        service.AudioError += error => batch.TrySetException(error);
        var options = new AudioCaptureOptions(mode);
        service.Start(options);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var microphone = Pcm(1000, Pcm16AudioMixer.SamplesPerFrame * 5);
        var playback = Pcm(2000, Pcm16AudioMixer.SamplesPerFrame * 5);
        for (var index = 0; index < 600; index++)
        {
            batch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (options.IncludesMicrophone) devices.Endpoint(AudioDeviceKind.Input).Emit(microphone);
            if (options.IncludesSystemAudio) devices.Endpoint(AudioDeviceKind.Output).Emit(playback);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await batch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual((index + 1) * 5, frames);
        }
        Assert.AreEqual(300L * Pcm16AudioMixer.SampleRate * sizeof(short), bytesDelivered);
    }

    [TestMethod]
    public async Task SlowDeviceDiscoveryDoesNotBlockAudioDelivery()
    {
        var clock = new DeviceClock();
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, clock);
        using var releaseCheck = new ManualResetEventSlim();
        var enteredCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = FirstFrame(service);
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        await frame.WaitAsync(TimeSpan.FromSeconds(5));
        devices.BeforeList = _ =>
        {
            enteredCheck.TrySetResult();
            Assert.IsTrue(releaseCheck.Wait(TimeSpan.FromSeconds(5)));
        };
        try
        {
            clock.Advance();
            await enteredCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duringCheck = FirstFrame(service);
            await duringCheck.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 0));
        }
        finally
        {
            devices.BeforeList = null;
            releaseCheck.Set();
        }
    }

    [TestMethod]
    public async Task JitterAndWallClockChangesDoNotLoseFractionalFrameTime()
    {
        var clock = new CoalescingAudioClock();
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, clock);
        var frames = 0;
        var target = 0;
        var batch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioDataAvailable += bytes =>
        {
            AssertSamples(bytes, 3000);
            if (++frames == target) batch.TrySetResult();
        };
        service.AudioError += error => batch.TrySetException(error);
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var (milliseconds, expectedFrames, utcShiftMinutes) in new[]
        {
            (350, 3, 0), (250, 6, 60), (110, 7, -120), (290, 10, 0)
        })
        {
            batch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            target = expectedFrames;
            var samples = Pcm16AudioMixer.SampleRate * milliseconds / 1000;
            devices.Endpoint(AudioDeviceKind.Input).Emit(Pcm(1000, samples));
            devices.Endpoint(AudioDeviceKind.Output).Emit(Pcm(2000, samples));
            clock.ShiftUtc(TimeSpan.FromMinutes(utcShiftMinutes));
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            await batch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(expectedFrames, frames);
        }
        Assert.AreEqual(10, frames);
    }

    [TestMethod]
    public async Task SilenceCatchUpIsBoundedPerWakeWithoutLosingElapsedDuration()
    {
        var clock = new CoalescingAudioClock();
        using var service = new AudioCaptureService(new FakeDevices(), clock);
        var frames = 0;
        var target = 0;
        var batch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioDataAvailable += bytes =>
        {
            AssertSamples(bytes, 0);
            if (++frames == target) batch.TrySetResult();
        };
        service.AudioError += error => batch.TrySetException(error);
        service.Start(new(AudioCaptureMode.SystemAudio));
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var (milliseconds, expectedFrames) in new[] { (5000, 20), (100, 40), (100, 52) })
        {
            batch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            target = expectedFrames;
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            await batch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
            Assert.AreEqual(expectedFrames, frames);
        }
    }

    [TestMethod]
    public async Task StoppingInsideACatchUpBatchDoesNotDeliverTheRemainingFrames()
    {
        var clock = new CoalescingAudioClock();
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, clock);
        var frames = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioDataAvailable += _ =>
        {
            Interlocked.Increment(ref frames);
            service.Stop();
            stopped.TrySetResult();
        };
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        devices.Endpoint(AudioDeviceKind.Input).Emit(Pcm(1000, Pcm16AudioMixer.SamplesPerFrame * 5));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(20);
        Assert.AreEqual(1, frames);
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
    }

    [TestMethod]
    public async Task BlockedDeviceDiscoveryDoesNotOverlapOrDelayStopAndCannotAffectANewRun()
    {
        var clock = new DeviceClock();
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, clock);
        using var releaseCheck = new ManualResetEventSlim();
        var enteredCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var changes = 0;
        var queries = 0;
        service.AudioError += errors.Enqueue;
        service.AudioDeviceChanged += () => Interlocked.Increment(ref changes);
        var frame = FirstFrame(service);
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        await frame.WaitAsync(TimeSpan.FromSeconds(5));
        devices.BeforeList = _ =>
        {
            Interlocked.Increment(ref queries);
            enteredCheck.TrySetResult();
            Assert.IsTrue(releaseCheck.Wait(TimeSpan.FromSeconds(5)));
        };
        try
        {
            clock.Advance();
            await enteredCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance();
            await FirstFrame(service).WaitAsync(TimeSpan.FromSeconds(1));
            await FirstFrame(service).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, queries);
            await Task.Run(service.Stop).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
            devices.BeforeList = null;
            devices.DefaultInputId = "replacement-mic";
            var restarted = FirstFrame(service);
            service.Start(new());
            releaseCheck.Set();
            await restarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsEmpty(errors);
            Assert.AreEqual(0, changes);
        }
        finally
        {
            devices.BeforeList = null;
            releaseCheck.Set();
        }
    }

    [TestMethod]
    public async Task BackgroundDiscoveryFailuresAreReportedAndReleaseBothSources()
    {
        var clock = new DeviceClock();
        var devices = new FakeDevices();
        using var service = new AudioCaptureService(devices, clock);
        var error = NextError(service);
        var frame = FirstFrame(service);
        service.Start(new(AudioCaptureMode.MicrophoneAndSystemAudio));
        await frame.WaitAsync(TimeSpan.FromSeconds(5));
        devices.BeforeList = _ => throw new COMException("Simulated device enumeration failure");
        clock.Advance();
        var failure = await error.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsInstanceOfType<AudioCaptureException>(failure);
        Assert.IsInstanceOfType<COMException>(failure.InnerException);
        Assert.IsTrue(devices.Opened.All(endpoint => endpoint.Disposals == 1));
        devices.BeforeList = null;
        var restarted = FirstFrame(service);
        service.Start(new());
        await restarted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task<byte[]> FirstFrame(AudioCaptureService service)
    {
        var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioDataAvailable += bytes => result.TrySetResult(bytes);
        return result.Task;
    }

    private static Task<Exception> NextError(AudioCaptureService service)
    {
        var result = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioError += error => result.TrySetResult(error);
        return result.Task;
    }

    private static byte[] Pcm(short value, int count = Pcm16AudioMixer.SamplesPerFrame)
    {
        var bytes = new byte[count * sizeof(short)];
        for (var index = 0; index < count; index++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short)), value);
        return bytes;
    }

    private static short[] Samples(byte[] bytes) => Enumerable.Range(0, bytes.Length / sizeof(short))
        .Select(index => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * sizeof(short)))).ToArray();

    private static void AssertSamples(byte[] bytes, short value) =>
        Assert.IsTrue(Samples(bytes).All(sample => sample == value));

    private sealed class DeviceClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance() => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(4).Ticks);
    }

    private sealed class CoalescingAudioClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        private long _utcShift;
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp() + Interlocked.Read(ref _utcShift));
        public void ShiftUtc(TimeSpan shift) => Interlocked.Add(ref _utcShift, shift.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate) _timers.Add(timer);
            TimerCreated.TrySetResult();
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            var now = Interlocked.Add(ref _ticks, elapsed.Ticks);
            ManualTimer[] timers;
            lock (_gate) timers = _timers.ToArray();
            foreach (var timer in timers) timer.Fire(now);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly object _gate = new();
            private readonly CoalescingAudioClock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _nextTick;
            private long _period;
            private bool _disposed;

            public ManualTimer(CoalescingAudioClock clock, TimerCallback callback, object? state,
                TimeSpan dueTime, TimeSpan period)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
                Change(dueTime, period);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    if (_disposed) return false;
                    _nextTick = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue :
                        _clock.GetTimestamp() + dueTime.Ticks;
                    _period = period.Ticks;
                    return true;
                }
            }

            public void Fire(long now)
            {
                lock (_gate)
                {
                    if (_disposed || now < _nextTick) return;
                    _nextTick = _period > 0 ? _nextTick + ((now - _nextTick) / _period + 1) * _period : long.MaxValue;
                }
                _callback(_state);
            }

            public void Dispose()
            {
                lock (_gate) _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeDevices : ISharedAudioDeviceFactory
    {
        public bool MicrophonePresent { get; set; } = true;
        public string DefaultInputId { get; set; } = "mic-id";
        public string DefaultOutputId { get; set; } = "output-id";
        public string InputName { get; set; } = "Microphone";
        public AudioDeviceKind? FailOpen { get; set; }
        public AudioDeviceKind? FailStart { get; set; }
        public AudioDeviceKind? FailDispose { get; set; }
        public AudioDeviceKind? StopOnStart { get; set; }
        public Action<AudioDeviceKind>? BeforeList { get; set; }
        public ConcurrentQueue<FakeEndpoint> Opened { get; } = new();
        public ConcurrentQueue<(AudioDeviceKind Kind, string? Id)> Selections { get; } = new();

        public IReadOnlyList<AudioDevice> ListDevices(AudioDeviceKind kind)
        {
            BeforeList?.Invoke(kind);
            if (kind == AudioDeviceKind.Input && !MicrophonePresent) return [];
            var id = kind == AudioDeviceKind.Input ? "mic-id" : "output-id";
            var name = kind == AudioDeviceKind.Input ? InputName : "Speakers";
            var defaultId = kind == AudioDeviceKind.Input ? DefaultInputId : DefaultOutputId;
            return [new("default", "Default device", defaultId), new(id, name)];
        }

        public ISharedAudioEndpoint Open(AudioDeviceKind kind, string? selectedId)
        {
            if (kind == FailOpen) throw new AudioCaptureException("Simulated open failure");
            var device = ListDevices(kind).Single(value => value.Id == (selectedId ?? "default"));
            var endpoint = new FakeEndpoint(kind, device.EffectiveId, kind == FailStart, kind == StopOnStart)
            {
                FailDispose = kind == FailDispose
            };
            Opened.Enqueue(endpoint);
            Selections.Enqueue((kind, selectedId));
            return endpoint;
        }

        public FakeEndpoint Endpoint(AudioDeviceKind kind) => Opened.Last(endpoint => endpoint.Kind == kind);
    }

    private sealed class FakeEndpoint(AudioDeviceKind kind, string id, bool failStart, bool stopOnStart) : ISharedAudioEndpoint
    {
        private int _callbackThread;
        public AudioDeviceKind Kind => kind;
        public string EndpointId => id;
        public int Starts { get; private set; }
        public int Disposals { get; private set; }
        public bool FailDispose { get; set; }
        public bool DisposedOnCallbackThread { get; private set; }
        public event Action<ReadOnlyMemory<byte>>? DataAvailable;
        public event Action<Exception?>? Stopped;
        public Action<ReadOnlyMemory<byte>>? DataCallback => DataAvailable;
        public Action<Exception?>? StoppedCallback => Stopped;
        public void Start()
        {
            Starts++;
            if (failStart) throw new AudioCaptureException("Simulated start failure");
            if (stopOnStart) EmitStopped(null);
        }
        public void Emit(byte[] bytes) => DataAvailable?.Invoke(bytes);
        public void EmitStopped(Exception? error)
        {
            _callbackThread = Environment.CurrentManagedThreadId;
            try { Stopped?.Invoke(error); }
            finally { _callbackThread = 0; }
        }
        public void Dispose()
        {
            DisposedOnCallbackThread |= _callbackThread == Environment.CurrentManagedThreadId;
            Disposals++;
            if (FailDispose) throw new InvalidOperationException("Simulated release failure");
        }
    }
}
