// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vixen.Audio.Assets;
using Vixen.Audio.Devices;
using Vixen.Audio.Diagnostics;
using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Vixen.Audio.Sources;
using Vixen.Audio.Spatial;
using Vixen.Audio.Streaming;

namespace Vixen.Audio;

/// <summary>How to build an <see cref="AudioEngine" />.</summary>
/// <remarks>
///     Written <c>new AudioEngineOptions()</c> and never <c>default</c>: the defaults live in the
///     parameterless constructor, and a <c>default</c> would ask for a mixer with no voices.
/// </remarks>
public readonly record struct AudioEngineOptions() {
    /// <summary>How many sounds can play at once.</summary>
    public int VoiceCapacity { get; init; } = 64;

    /// <summary>What to ask the device for.</summary>
    public AudioDeviceOptions Device { get; init; } = new();

    /// <summary>Whether the engine runs the streaming decoder on its own thread.</summary>
    /// <remarks>
    ///     Off means <see cref="AudioEngine.Streams" />' <c>Pump</c> must be called by somebody —
    ///     a test, or a single-threaded platform's own loop. See <see cref="AudioStreamPump" />.
    /// </remarks>
    public bool StreamOnOwnThread { get; init; } = true;

    /// <summary>Whether the device starts pulling as soon as the engine is built.</summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>Whether a <see cref="LimiterEffect" /> is put on the master bus.</summary>
    /// <remarks>
    ///     <b>On, and the default is the interesting decision.</b> Most engines ship without one and
    ///     let a busy scene clip, because a limiter is a mastering tool and mastering is the sound
    ///     designer's job. The counter-argument won here: the master ends in a clamp whatever
    ///     happens, so the choice is not "limiter or nothing" but "limiter or hard clipping", and
    ///     nobody prefers hard clipping. Turn it off to mix a scene without a safety net underneath —
    ///     which is a real thing to want while balancing levels, because a limiter hides the problem
    ///     you are trying to hear.
    /// </remarks>
    public bool MasterLimiter { get; init; } = true;
}

/// <summary>The front door: what a game holds, and the only audio type most code touches.</summary>
/// <remarks>
///     <para>
///         <b>Two threads, and no lock between them.</b> The game thread starts and stops sounds and
///         moves them about; the device's thread renders. They meet at three kinds of shared state
///         and each kind has its own mechanism, all of them lock-free:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>A voice's life</b> — free, playing, paused, stopping, finished — is one
///             <see cref="int" /> moved with a compare-and-swap. A stop that races a natural end
///             resolves to whichever got there first, and the loser does nothing.
///         </item>
///         <item>
///             <b>Scalar parameters</b> — gain, pitch, pan, a bus's volume — are written straight in.
///             The CLR writes a <see cref="float" /> atomically, so the worst case is a change taking
///             effect one block later than it was made.
///         </item>
///         <item>
///             <b>Whole structs</b> — a source's spatial settings, the listener — go through
///             <c>Published&lt;T&gt;</c>, a sequence lock. Neither side ever waits, and a reader that
///             catches a write in progress keeps the value it already had for one block.
///         </item>
///     </list>
///     <para>
///         There is no command queue, and that is the point: a queue would allocate in the frame loop
///         for every moving emitter in the scene.
///     </para>
///     <para>
///         <b><see cref="Update()" /> must be called once a frame.</b> It is where a finished voice
///         goes back to the pool, where a stream is handed back to the pump, and where the counters
///         the audio thread wrote become statistics and log lines. An engine that is never updated
///         plays its first sixty-four sounds and then goes quiet.
///     </para>
/// </remarks>
public sealed class AudioEngine : IAudioRenderSource, IDisposable {
    readonly ILogger logger;
    readonly Voice[] voices;
    readonly FadeState[] fades;
    readonly float[] silence;
    readonly Stopwatch clock = Stopwatch.StartNew();
    TimeSpan lastUpdate;

    Published<AudioListener> publishedListener;
    AudioListener listener = AudioListener.Default;
    AudioListener rendered = AudioListener.Default;

    long renderedFrames;
    long droppedRequests;
    long stolenVoices;
    long reportedDrops;
    long reportedStreamUnderruns;
    long reportedDeviceUnderruns;
    long lastRenderTicks;
    double load;
    bool disposed;
    Exception? renderFailure;

    /// <summary>An engine on a device.</summary>
    /// <param name="device">Where the samples go. Owned by the engine and disposed with it.</param>
    /// <param name="options">How to build the mixer.</param>
    /// <param name="logger">Where to report. Nothing is logged from the audio thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public AudioEngine(IAudioDevice device, in AudioEngineOptions options, ILogger? logger = null) {
        ArgumentNullException.ThrowIfNull(device);

        Device = device;
        this.logger = logger ?? NullLogger.Instance;
        Mixer = new AudioMixer(options.VoiceCapacity);
        voices = Mixer.Voices;
        fades = new FadeState[voices.Length];
        silence = new float[device.BufferFrames * device.Format.Channels];

        Streams = new AudioStreamPump();
        publishedListener.Write(listener);

        // Prepare before Start, so the first block the device asks for finds sized buffers. The
        // device calls Prepare too; doing it here as well means a caller can build an effect chain
        // between construction and AutoStart without tripping over a bus that has no format yet.
        Mixer.Prepare(device.Format, device.BufferFrames);

        if (options.MasterLimiter) {
            Limiter = new LimiterEffect();
            Master.AddEffect(Limiter);
        }

        if (options.StreamOnOwnThread) {
            Streams.Start();
        }

        if (options.AutoStart) {
            Start();
        }
    }

    /// <summary>Opens a device on a backend, and falls back to silence if it will not open.</summary>
    /// <param name="backend">The backend to try.</param>
    /// <param name="options">How to build the engine.</param>
    /// <param name="logger">Where to report.</param>
    /// <returns>The engine, on a real device or on <see cref="NullAudioBackend" />.</returns>
    /// <remarks>
    ///     <b>No audio is not a failure.</b> A CI runner, a container, a machine whose sound card is
    ///     held exclusively by something else — all of them should run the game. What must not happen
    ///     is the mixer being skipped: a voice that never finishes because nothing is rendering it
    ///     will strand whatever gameplay was waiting on it, so the fallback still runs the whole
    ///     pipeline and discards the result.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="backend" /> is null.</exception>
    public static AudioEngine Create(IAudioBackend backend, in AudioEngineOptions options, ILogger? logger = null) {
        ArgumentNullException.ThrowIfNull(backend);
        var log = logger ?? NullLogger.Instance;

        if (backend.IsAvailable) {
            try {
                var device = backend.OpenDevice(options.Device);
                AudioLog.DeviceOpened(
                    log,
                    backend.Name,
                    device.Info.Name,
                    device.Format.SampleRate,
                    device.Format.Channels,
                    device.BufferFrames
                );

                return new AudioEngine(device, options, logger);
            } catch (AudioDeviceException exception) {
                AudioLog.DeviceUnavailable(log, backend.Name, exception.Message);
            }
        } else {
            AudioLog.DeviceUnavailable(log, backend.Name, "the backend reported itself unavailable");
        }

        var fallback = new NullAudioBackend { Paced = true };
        return new AudioEngine(fallback.OpenDevice(options.Device), options, logger);
    }

    /// <summary>An engine on a backend, with the default options.</summary>
    /// <param name="backend">The backend to try.</param>
    /// <param name="logger">Where to report.</param>
    /// <returns>The engine.</returns>
    public static AudioEngine Create(IAudioBackend backend, ILogger? logger = null) =>
        Create(backend, new AudioEngineOptions(), logger);

    /// <summary>Where the samples go.</summary>
    public IAudioDevice Device { get; }

    /// <summary>The bus tree and the voice pool.</summary>
    public AudioMixer Mixer { get; }

    /// <summary>The thread keeping streaming voices fed.</summary>
    public AudioStreamPump Streams { get; }

    /// <summary>The bus everything reaches.</summary>
    public AudioBus Master => Mixer.Master;

    /// <summary>The named mix states from the last <see cref="LoadMixer" />, or <see langword="null" />.</summary>
    public MixerSnapshots? Snapshots { get; private set; }

    /// <summary>Builds a mixer asset's buses, effects, sends and sidechains onto this engine.</summary>
    /// <param name="asset">What to build.</param>
    /// <returns>Everything that did not resolve, in the order it was found. Empty is the good case.</returns>
    /// <remarks>
    ///     <para>
    ///         The front door for the authoring layer: a sound designer edits the asset, and gameplay
    ///         code says <c>engine.Snapshots?.TransitionTo("Underwater", …)</c> without ever naming a
    ///         bus. That indirection is the whole point — a mix that lives in an asset can be changed
    ///         without a programmer, and one that lives in C# cannot.
    ///     </para>
    ///     <para>
    ///         Problems are returned rather than thrown, because a mixer asset is content: a level
    ///         whose ambience bus lost its reverb send should still be playable while somebody works
    ///         out why.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="asset" /> is null.</exception>
    public IReadOnlyList<string> LoadMixer(MixerAsset asset) {
        var result = MixerBuilder.Build(Mixer, asset);
        Snapshots = result.Snapshots;
        return result.Problems;
    }

    /// <summary>The limiter on the master, if <see cref="AudioEngineOptions.MasterLimiter" /> asked for one.</summary>
    /// <remarks>
    ///     Exposed so its ceiling can be changed and its gain reduction read — the second being the
    ///     number that says the mix is too hot, which is a thing to see on the audio overlay rather
    ///     than to discover by ear.
    /// </remarks>
    public LimiterEffect? Limiter { get; }

    /// <summary>What the device is rendering.</summary>
    public AudioFormat Format => Device.Format;

    /// <summary>Where the ears are, as last set.</summary>
    public AudioListener Listener => listener;

    /// <summary>What the subsystem was doing as of the last <see cref="Update()" />.</summary>
    public AudioStatistics Statistics { get; private set; }

    /// <summary>Moves the listener.</summary>
    /// <param name="value">Where the ears are now.</param>
    /// <remarks>Call it from one thread. The mixer picks it up on the next block.</remarks>
    public void SetListener(in AudioListener value) {
        listener = value;
        publishedListener.Write(value);
    }

    /// <summary>Adds a bus.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="parent">What it sums into. The master by default.</param>
    /// <returns>The bus.</returns>
    public AudioBus CreateBus(string name, AudioBus? parent = null) => Mixer.CreateBus(name, parent);

    /// <summary>Finds a bus.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The bus, or <see langword="null" />.</returns>
    public AudioBus? FindBus(string name) => Mixer.FindBus(name);

    /// <summary>Plays a clip at full volume on the master bus.</summary>
    /// <param name="clip">The clip.</param>
    /// <returns>A handle, or <see cref="VoiceHandle.None" /> if every voice was busy.</returns>
    public VoiceHandle Play(AudioClip clip) => Play(clip, new PlaybackSettings());

    /// <summary>Plays a clip.</summary>
    /// <param name="clip">The clip. Not copied — it must outlive the sound.</param>
    /// <param name="settings">How to play it.</param>
    /// <returns>A handle, or <see cref="VoiceHandle.None" /> if every voice was busy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clip" /> is null.</exception>
    public VoiceHandle Play(AudioClip clip, in PlaybackSettings settings) {
        ArgumentNullException.ThrowIfNull(clip);
        return Play(new ClipSampleProvider(clip, settings.Loop), settings, ownsSource: true);
    }

    /// <summary>Plays whatever a caller can produce samples from.</summary>
    /// <param name="source">The provider. Not disposed by the engine.</param>
    /// <param name="settings">How to play it.</param>
    /// <returns>A handle, or <see cref="VoiceHandle.None" /> if every voice was busy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    public VoiceHandle Play(IAudioSampleProvider source, in PlaybackSettings settings) =>
        Play(source, settings, ownsSource: false);

    /// <summary>Plays a track without loading all of it, decoding as it goes.</summary>
    /// <param name="decoder">The decoder. Owned by the engine from here on and disposed when the voice ends.</param>
    /// <param name="settings">How to play it.</param>
    /// <returns>A handle, or <see cref="VoiceHandle.None" /> if every voice was busy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decoder" /> is null.</exception>
    /// <remarks>
    ///     The first block is decoded on the calling thread before this returns, so a track never
    ///     starts with the gap it would have while the pump caught up.
    /// </remarks>
    public VoiceHandle PlayStream(IAudioStreamDecoder decoder, in PlaybackSettings settings) {
        ArgumentNullException.ThrowIfNull(decoder);

        var provider = new StreamingSampleProvider(decoder, settings.Loop);
        Streams.Register(provider);
        var handle = Play(provider, settings, ownsSource: true);

        if (!handle.IsValid) {
            Streams.Unregister(provider);
            provider.Dispose();
        }

        return handle;
    }

    /// <summary>Stops a sound, over one block so it does not click.</summary>
    /// <param name="handle">Which one. A stale handle does nothing.</param>
    public void Stop(VoiceHandle handle) {
        if (!TryResolve(handle, out var voice)) {
            return;
        }

        var state = (VoiceState)Volatile.Read(ref voice.State);

        if (state is VoiceState.Playing or VoiceState.Paused) {
            Interlocked.CompareExchange(ref voice.State, (int)VoiceState.Stopping, (int)state);
        }
    }

    /// <summary>Stops everything.</summary>
    public void StopAll() => Mixer.StopAll();

    /// <summary>Holds a sound where it is.</summary>
    /// <param name="handle">Which one.</param>
    public void Pause(VoiceHandle handle) {
        if (TryResolve(handle, out var voice)) {
            Interlocked.CompareExchange(ref voice.State, (int)VoiceState.Paused, (int)VoiceState.Playing);
        }
    }

    /// <summary>Lets a paused sound carry on.</summary>
    /// <param name="handle">Which one.</param>
    public void Resume(VoiceHandle handle) {
        if (TryResolve(handle, out var voice)) {
            Interlocked.CompareExchange(ref voice.State, (int)VoiceState.Playing, (int)VoiceState.Paused);
        }
    }

    /// <summary>Changes a sound's volume.</summary>
    /// <param name="handle">Which one.</param>
    /// <param name="gain">The new linear gain.</param>
    /// <remarks>
    ///     Takes effect over the next block rather than instantly, because the mixer ramps a voice's
    ///     gain across the block it is applied in. That is what stops a volume change being a click.
    /// </remarks>
    public void SetGain(VoiceHandle handle, float gain) {
        if (TryResolve(handle, out var voice)) {
            voice.Gain = gain;
        }
    }

    /// <summary>What a sound's gain currently is.</summary>
    /// <param name="handle">Which one.</param>
    /// <returns>Its gain, or zero if the handle is stale.</returns>
    /// <remarks>
    ///     Worth having because a fade moves it: a caller that set 1.0 and started a fade-out cannot
    ///     otherwise find out where the fade has got to, and an audio overlay wants to show it.
    /// </remarks>
    public float GainOf(VoiceHandle handle) => TryResolve(handle, out var voice) ? voice.Gain : 0f;

    /// <summary>Changes a sound's playback rate.</summary>
    /// <param name="handle">Which one.</param>
    /// <param name="pitch">The new multiplier.</param>
    public void SetPitch(VoiceHandle handle, float pitch) {
        if (TryResolve(handle, out var voice)) {
            voice.Pitch = pitch;
        }
    }

    /// <summary>Moves a non-spatial sound between the speakers.</summary>
    /// <param name="handle">Which one.</param>
    /// <param name="pan">−1 left, 0 centre, +1 right.</param>
    public void SetPan(VoiceHandle handle, float pan) {
        if (TryResolve(handle, out var voice)) {
            voice.Pan = pan;
        }
    }

    /// <summary>Moves a sound in the world.</summary>
    /// <param name="handle">Which one.</param>
    /// <param name="settings">Where it is and how it behaves there.</param>
    /// <remarks>Does nothing to a voice that was not started spatial; a sound is one or the other for its life.</remarks>
    public void SetSpatial(VoiceHandle handle, in SpatialSettings settings) {
        if (TryResolve(handle, out var voice)) {
            voice.PublishSpatial(settings);
        }
    }

    /// <summary>What a sound is doing.</summary>
    /// <param name="handle">Which one.</param>
    /// <returns>Its state, or <see cref="VoiceState.Free" /> if the handle is stale.</returns>
    public VoiceState StateOf(VoiceHandle handle) =>
        TryResolve(handle, out var voice) ? (VoiceState)Volatile.Read(ref voice.State) : VoiceState.Free;

    /// <summary>Whether a sound is still going.</summary>
    /// <param name="handle">Which one.</param>
    /// <returns>Whether it is playing, paused or stopping.</returns>
    public bool IsPlaying(VoiceHandle handle) =>
        StateOf(handle) is VoiceState.Playing or VoiceState.Paused or VoiceState.Stopping;

    /// <summary>Takes a sound's gain somewhere else over time.</summary>
    /// <param name="handle">Which sound. A stale handle does nothing.</param>
    /// <param name="gain">Where its gain is going.</param>
    /// <param name="duration">How long to take. Zero or less arrives at once.</param>
    /// <param name="curve">Which way. Decibels by default.</param>
    /// <remarks>
    ///     Stepped by <see cref="Update(float)" /> on game time, so a fade under a paused game stops
    ///     and a fade under slow motion slows down. A second call replaces the one in progress from
    ///     wherever it had got to.
    /// </remarks>
    public void FadeTo(
        VoiceHandle handle,
        float gain,
        TimeSpan duration,
        AudioFadeCurve curve = AudioFadeCurve.Decibel
    ) => StartFade(handle, gain, duration, curve, stopAtEnd: false);

    /// <summary>Fades a sound out and stops it when it gets there.</summary>
    /// <param name="handle">Which sound.</param>
    /// <param name="duration">How long to take.</param>
    /// <param name="curve">Which way.</param>
    /// <remarks>
    ///     The other half of every music system. <see cref="Stop" /> alone fades over one block —
    ///     enough not to click, and nothing like a musical fade-out.
    /// </remarks>
    public void FadeOutAndStop(
        VoiceHandle handle,
        TimeSpan duration,
        AudioFadeCurve curve = AudioFadeCurve.Decibel
    ) => StartFade(handle, 0f, duration, curve, stopAtEnd: true);

    /// <summary>Whether a fade is running on a sound.</summary>
    /// <param name="handle">Which sound.</param>
    /// <returns>Whether it is fading.</returns>
    public bool IsFading(VoiceHandle handle) =>
        (uint)handle.Index < (uint)fades.Length
        && fades[handle.Index].Active
        && fades[handle.Index].Generation == handle.Generation;

    /// <summary>Collects finished voices, steps the fades, and gathers the frame's numbers.</summary>
    /// <remarks>
    ///     Measures the time since the previous call. A game that already has a frame delta should
    ///     pass it to <see cref="Update(float)" /> instead — a wall clock does not stop when the game
    ///     is paused, and a fade that kept running under a pause menu is a bug somebody will spend an
    ///     afternoon on.
    /// </remarks>
    public void Update() {
        var now = clock.Elapsed;
        var delta = now - lastUpdate;
        lastUpdate = now;
        Update((float)delta.TotalSeconds);
    }

    /// <summary>Collects finished voices, steps the fades, and gathers the frame's numbers.</summary>
    /// <param name="deltaSeconds">How much game time has passed since the last call.</param>
    /// <remarks>Once a frame, on the game thread. See the note on the class about what happens if it is not.</remarks>
    public void Update(float deltaSeconds) {
        StepFades(deltaSeconds);
        var streamUnderruns = 0L;

        foreach (var voice in voices) {
            if (voice.Source is StreamingSampleProvider streaming) {
                streamUnderruns += streaming.Underruns;
            }

            Retire(voice);

            if (Volatile.Read(ref voice.State) != (int)VoiceState.Finished) {
                continue;
            }

            Collect(voice);
        }

        var drops = Interlocked.Read(ref droppedRequests);
        var deviceUnderruns = Device.Underruns;
        var elapsed = TimeSpan.FromTicks(
            (long)(Volatile.Read(ref lastRenderTicks) * (10_000_000.0 / Stopwatch.Frequency))
        );

        Statistics = new AudioStatistics {
            ActiveVoices = Mixer.ActiveVoices,
            VoiceCapacity = Mixer.VoiceCapacity,
            MasterPeak = Master.PeakLevel,
            RenderedFrames = Interlocked.Read(ref renderedFrames),
            LastRenderTime = elapsed,
            Load = load,
            DeviceUnderruns = deviceUnderruns,
            StreamUnderruns = streamUnderruns,
            StreamCount = Streams.StreamCount,
            DroppedRequests = drops,
            StolenVoices = Interlocked.Read(ref stolenVoices)
        };

        if (drops > reportedDrops) {
            reportedDrops = drops;
            AudioLog.VoicePoolExhausted(logger, drops, Mixer.VoiceCapacity);
        }

        if (streamUnderruns > reportedStreamUnderruns) {
            reportedStreamUnderruns = streamUnderruns;
            AudioLog.StreamUnderrun(logger, streamUnderruns);
        }

        if (deviceUnderruns > reportedDeviceUnderruns) {
            reportedDeviceUnderruns = deviceUnderruns;
            AudioLog.DeviceUnderrun(logger, deviceUnderruns);
        }

        if (Interlocked.Exchange(ref renderFailure, null) is { } failure) {
            AudioLog.RenderFailed(logger, failure);
        }
    }

    void StartFade(VoiceHandle handle, float gain, TimeSpan duration, AudioFadeCurve curve, bool stopAtEnd) {
        if (!TryResolve(handle, out var voice)) {
            return;
        }

        var seconds = (float)duration.TotalSeconds;

        if (seconds <= 0f) {
            voice.Gain = gain;
            fades[handle.Index].Active = false;

            if (stopAtEnd) {
                Stop(handle);
            }

            return;
        }

        fades[handle.Index] = new FadeState {
            Active = true,
            Generation = handle.Generation,
            From = voice.Gain,
            To = gain,
            Duration = seconds,
            Curve = curve,
            StopAtEnd = stopAtEnd
        };
    }

    void StepFades(float deltaSeconds) {
        // Snapshots first: a transition cancels any manual fade on the buses it names, so stepping
        // it before them keeps a fade started this frame from being applied and then overwritten.
        Snapshots?.Step(deltaSeconds);

        foreach (var bus in Mixer.Buses) {
            bus.StepFade(deltaSeconds);
        }

        for (var i = 0; i < fades.Length; i++) {
            ref var fade = ref fades[i];

            if (!fade.Active) {
                continue;
            }

            var voice = voices[i];

            // The slot moved on — the sound finished, or was stolen — so the fade is about something
            // that is no longer there. Left running it would drive the gain of whatever took the slot.
            if (voice.Generation != fade.Generation) {
                fade.Active = false;
                continue;
            }

            var finished = fade.Step(deltaSeconds, out var gain);
            voice.Gain = gain;

            if (!finished) {
                continue;
            }

            fade.Active = false;

            if (fade.StopAtEnd) {
                Stop(new VoiceHandle(i, fade.Generation));
            }
        }
    }

    /// <summary>Starts the device.</summary>
    public void Start() {
        if (!Device.IsRunning) {
            Device.Start(this);
        }
    }

    /// <summary>Stops the device without losing what is playing.</summary>
    /// <remarks>
    ///     What a phone's <c>ILifecycle</c> suspend calls. Voices keep their position, so
    ///     <see cref="Start" /> carries on where it left off rather than restarting the level's music
    ///     from the top.
    /// </remarks>
    public void Suspend() => Device.Stop();

    /// <summary>Checks the engine's own invariants and says what is wrong.</summary>
    /// <returns>Everything that does not hold, or empty if all is well.</returns>
    /// <remarks>
    ///     <c>docs/plan/13</c> asks every subsystem for one of these, callable from the debug console.
    ///     Cheap to write beside the data structure and worth far more than the alternative, which is
    ///     working out from a click in the speakers which invariant broke.
    /// </remarks>
    public IReadOnlyList<string> Validate() {
        var problems = new List<string>();

        if (!Format.IsValid) {
            problems.Add($"The device format {Format} is not renderable.");
        }

        if (Mixer.Format != Format) {
            problems.Add($"The mixer is prepared for {Mixer.Format} and the device is {Format}.");
        }

        for (var i = 0; i < voices.Length; i++) {
            var voice = voices[i];
            var state = (VoiceState)Volatile.Read(ref voice.State);

            if (state is not VoiceState.Free && voice.Source is null) {
                problems.Add($"Voice {i} is {state} with no source.");
            }

            if (state is VoiceState.Free && voice.Source is not null) {
                problems.Add($"Voice {i} is free and still holds a source, so it will not be collected.");
            }

            if ((uint)voice.Bus >= (uint)Mixer.Buses.Count) {
                problems.Add($"Voice {i} names bus {voice.Bus}, and there are {Mixer.Buses.Count}.");
            }
        }

        foreach (var bus in Mixer.Buses) {
            if (bus.Parent is null && bus != Master) {
                problems.Add($"Bus '{bus.Name}' has no parent and is not the master.");
            }
        }

        return problems;
    }

    /// <inheritdoc />
    void IAudioRenderSource.Prepare(in AudioFormat format, int maxFrames) => Mixer.Prepare(format, maxFrames);

    /// <inheritdoc />
    void IAudioRenderSource.Render(Span<float> destination, int frameCount) {
        var started = Stopwatch.GetTimestamp();

        try {
            publishedListener.TryRead(ref rendered);
            Mixer.Render(destination, frameCount, rendered);
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            // An exception escaping onto a driver's callback thread is not recoverable — OpenAL and
            // WebAudio both call this from native code that has no idea what a managed exception is,
            // and the process goes down. Silence and a counter are the only survivable answer; the
            // log line comes from Update, on a thread that is allowed to take a lock.
            destination[..(frameCount * Format.Channels)].Clear();
            Interlocked.Exchange(ref renderFailure, exception);
        }

        var ticks = Stopwatch.GetTimestamp() - started;
        Volatile.Write(ref lastRenderTicks, ticks);
        Interlocked.Add(ref renderedFrames, frameCount);

        var budget = (double)frameCount / Format.SampleRate;
        load = budget > 0 ? ticks / (double)Stopwatch.Frequency / budget : 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Device.Stop();
        Streams.Dispose();

        foreach (var voice in voices) {
            if (voice.OwnsSource && voice.Source is IDisposable owned) {
                owned.Dispose();
            }

            voice.Reset();
            Volatile.Write(ref voice.State, (int)VoiceState.Free);
        }

        Device.Dispose();
    }

    VoiceHandle Play(IAudioSampleProvider source, in PlaybackSettings settings, bool ownsSource) {
        ArgumentNullException.ThrowIfNull(source);

        if (TryClaim(out var index)) {
            var claimed = voices[index];
            claimed.Source = source;
            claimed.OwnsSource = ownsSource;
            Describe(claimed, settings);

            // Priming the interpolator reads the first two frames, which for a clip is a memory copy
            // and for a stream is a read from a ring the pump has already filled. It happens here, on
            // the calling thread, because the audio thread must not be the one to discover that a
            // provider was empty.
            claimed.Begin();

            // The state write is what publishes everything above it to the audio thread: it is a
            // release, and the render loop only looks at a voice it has seen playing.
            Volatile.Write(
                ref claimed.State,
                (int)(settings.StartPaused ? VoiceState.Paused : VoiceState.Playing)
            );

            return new VoiceHandle(index, claimed.Generation);
        }

        if (!TrySteal(settings.Priority, out var victim)) {
            Interlocked.Increment(ref droppedRequests);
            return VoiceHandle.None;
        }

        // The victim is or was being rendered, so nothing here may touch its render state. Only the
        // pending fields and the scalars — and the scalars only affect a block that is fading to
        // silence anyway.
        var stolen = voices[victim];
        stolen.PendingSource = source;
        stolen.PendingOwnsSource = ownsSource;
        stolen.PendingPaused = settings.StartPaused;
        Describe(stolen, settings);

        // Published last, and read first by the audio thread: everything above it is visible by the
        // time the flag is seen.
        Volatile.Write(ref stolen.StealPending, 1);
        Interlocked.Increment(ref stolenVoices);
        return new VoiceHandle(victim, stolen.Generation);
    }

    void Describe(Voice voice, in PlaybackSettings settings) {
        // Clamped rather than rejected: a bus index that no longer names a bus is a stale asset, and
        // routing it to the master is audible in a way that silently dropping the sound is not.
        voice.Bus = (uint)settings.Bus < (uint)Mixer.Buses.Count ? settings.Bus : 0;
        voice.Gain = settings.Gain;
        voice.Pitch = settings.Pitch;
        voice.Pan = settings.Pan;
        voice.Priority = settings.Priority;
        voice.IsSpatial = settings.IsSpatial;
        voice.PublishSpatial(settings.Spatial);
    }

    /// <summary>Finds the voice most worth displacing, and asks it to stop.</summary>
    /// <param name="priority">What the incoming sound is worth.</param>
    /// <param name="index">Which slot was taken.</param>
    /// <returns>Whether anything was worth taking.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Quietest of the least important.</b> Priority is the first key because it is what a
    ///         designer set deliberately; audibility is the tie-break because among sounds nobody
    ///         ranked, the one nobody can hear is the one to lose. A sound already stopping or
    ///         finished is taken first of all, since it was on its way out regardless.
    ///     </para>
    ///     <para>
    ///         <b>Nothing more important than the newcomer is ever taken.</b> A pool full of dialogue
    ///         refuses a footstep rather than making room for it, which is the behaviour that makes
    ///         priority worth setting at all.
    ///     </para>
    ///     <para>
    ///         The scan is over the whole pool — sixty-four slots — and happens only when the pool is
    ///         full, which is exactly when a few hundred nanoseconds is affordable.
    ///     </para>
    /// </remarks>
    bool TrySteal(int priority, out int index) {
        var best = -1;
        var bestPriority = int.MaxValue;
        var bestAudibility = float.MaxValue;

        for (var i = 0; i < voices.Length; i++) {
            var voice = voices[i];
            var state = (VoiceState)Volatile.Read(ref voice.State);

            // A slot mid-steal already has a sound waiting for it; taking it again would strand the
            // handle already handed out for it.
            if (Volatile.Read(ref voice.StealPending) != 0) {
                continue;
            }

            if (state is not (VoiceState.Playing or VoiceState.Paused)) {
                continue;
            }

            if (voice.Priority > priority) {
                continue;
            }

            var audibility = voice.Audibility;

            if (voice.Priority > bestPriority || (voice.Priority == bestPriority && audibility >= bestAudibility)) {
                continue;
            }

            best = i;
            bestPriority = voice.Priority;
            bestAudibility = audibility;
        }

        if (best < 0) {
            index = -1;
            return false;
        }

        var chosen = voices[best];
        var chosenState = (VoiceState)Volatile.Read(ref chosen.State);

        if (Interlocked.CompareExchange(
                ref chosen.State,
                (int)VoiceState.Stopping,
                (int)chosenState
            ) != (int)chosenState) {
            // It finished on its own between the scan and here. Rare, and the answer is to try the
            // whole thing again rather than to steal a slot whose state moved underneath us.
            index = -1;
            return false;
        }

        // Bumped while the slot is Stopping, which invalidates the displaced sound's handle
        // immediately and makes the one about to be returned the only live reference to the slot.
        // Only the game thread reads it.
        chosen.Generation++;
        index = best;
        return true;
    }

    bool TryClaim(out int index) {
        for (var i = 0; i < voices.Length; i++) {
            if (Interlocked.CompareExchange(
                    ref voices[i].State,
                    (int)VoiceState.Claimed,
                    (int)VoiceState.Free
                ) == (int)VoiceState.Free) {
                // Safe unguarded now: the slot is Claimed, and only this thread touches a claimed
                // slot until the state moves on.
                voices[i].Generation++;
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    bool TryResolve(VoiceHandle handle, out Voice voice) {
        if ((uint)handle.Index < (uint)voices.Length) {
            var candidate = voices[handle.Index];

            if (candidate.Generation == handle.Generation) {
                voice = candidate;
                return true;
            }
        }

        voice = null!;
        return false;
    }

    /// <summary>Disposes what a steal displaced, on a thread that is allowed to.</summary>
    void Retire(Voice voice) {
        if (Interlocked.Exchange(ref voice.RetiredSource, null) is not { } retired) {
            return;
        }

        if (retired is StreamingSampleProvider streaming) {
            Streams.Unregister(streaming);
        }

        (retired as IDisposable)?.Dispose();
    }

    void Collect(Voice voice) {
        if (voice.Source is StreamingSampleProvider streaming) {
            Streams.Unregister(streaming);
        }

        if (voice.OwnsSource && voice.Source is IDisposable owned) {
            owned.Dispose();
        }

        voice.Reset();
        Volatile.Write(ref voice.State, (int)VoiceState.Free);
    }
}
