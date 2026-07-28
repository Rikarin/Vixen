// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vixen.Audio.Assets;
using Vixen.Audio.Devices;
using Vixen.Audio.Diagnostics;
using Vixen.Audio.Effects;
using Vixen.Audio.Events;
using Vixen.Audio.Mixing;
using Vixen.Audio.Parameters;
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

    /// <summary>How many voices may actually be heard at once. Zero means all of them.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What turns <see cref="VoiceCapacity" /> into a virtual voice count.</b> Set it below
    ///         the capacity and the pool becomes two numbers: how many sounds may be <em>playing</em>,
    ///         and how many of those may be <em>rendering</em>. Every frame the engine ranks by
    ///         priority and then audibility, and the ones that do not make the cut keep advancing
    ///         through their sources while producing nothing.
    ///     </para>
    ///     <para>
    ///         That is a different answer from stealing to the same question, and a better one where
    ///         it applies: a stolen sound is gone, and a virtual one comes back at the right place. A
    ///         capacity of 256 with 32 audible means a scene can have 256 things making noise and pay
    ///         for 32 of them — and a sound only ever actually dies when 256 are already going.
    ///     </para>
    ///     <para>
    ///         Zero by default, which is every voice real and no ranking pass at all, because the
    ///         behaviour only earns its keep once the capacity is large.
    ///     </para>
    /// </remarks>
    public int AudibleVoices { get; init; }

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
    readonly VoiceParameters voiceParameters;
    readonly AudioOcclusion occlusion;
    readonly DeferredSpawns deferred = new(128);
    MixControl? control;
    readonly int[] ranking;
    readonly int[] rankPriorities;
    readonly float[] rankAudibility;
    readonly int audibleVoices;
    readonly Stopwatch clock = Stopwatch.StartNew();
    TimeSpan lastUpdate;

    Published<AudioListenerSet> publishedListener;
    AudioListenerSet listeners = AudioListenerSet.Default;
    AudioListenerSet rendered = AudioListenerSet.Default;

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
        voiceParameters = new VoiceParameters(voices.Length);
        occlusion = new AudioOcclusion(voices.Length);
        ranking = new int[voices.Length];
        rankPriorities = new int[voices.Length];
        rankAudibility = new float[voices.Length];

        // Zero and anything at or above the capacity both mean "every voice is real", which is the
        // case where the ranking pass is skipped entirely rather than run and found to change nothing.
        audibleVoices = options.AudibleVoices > 0 && options.AudibleVoices < voices.Length
            ? options.AudibleVoices
            : 0;

        Streams = new AudioStreamPump();
        publishedListener.Write(listeners);

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
        ArgumentNullException.ThrowIfNull(asset);
        var result = MixerBuilder.Build(Mixer, asset);
        Snapshots = result.Snapshots;

        if (asset.Parameters.Length == 0) {
            return result.Problems;
        }

        // After the buses, sends and effects exist, because that is what the automation names. The
        // problems are concatenated rather than reported separately: from a caller's point of view
        // one asset failed to resolve in several places.
        var definitions = new AudioBusParameterDefinition[asset.Parameters.Length];

        for (var i = 0; i < definitions.Length; i++) {
            definitions[i] = asset.Parameters[i].ToDefinition();
        }

        LoadParameters(definitions, out var parameterProblems);

        if (parameterProblems.Count == 0) {
            return result.Problems;
        }

        var problems = new List<string>(result.Problems);
        problems.AddRange(parameterProblems);
        return problems;
    }

    /// <summary>Resolves an event asset against this engine, ready to be played.</summary>
    /// <param name="asset">What to build.</param>
    /// <param name="problems">Everything that did not resolve. Empty is the good case.</param>
    /// <param name="library">Where its layers are looked up, if it has any.</param>
    /// <returns>The event.</returns>
    /// <remarks>
    ///     The other half of the authoring layer, and the one gameplay touches: a mixer asset decides
    ///     what a bus does, and an event asset decides what a sound is. Both are content, so both
    ///     report their problems rather than throwing them.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="asset" /> is null.</exception>
    public AudioEvent LoadEvent(
        AudioEventAsset asset,
        out IReadOnlyList<string> problems,
        IAudioEventLibrary? library = null
    ) => AudioEventBuilder.Build(this, asset, out problems, library);

    /// <summary>The limiter on the master, if <see cref="AudioEngineOptions.MasterLimiter" /> asked for one.</summary>
    /// <remarks>
    ///     Exposed so its ceiling can be changed and its gain reduction read — the second being the
    ///     number that says the mix is too hot, which is a thing to see on the audio overlay rather
    ///     than to discover by ear.
    /// </remarks>
    public LimiterEffect? Limiter { get; }

    /// <summary>What the device is rendering.</summary>
    public AudioFormat Format => Device.Format;

    /// <summary>Where the ears are, as last set. The first of them, if there are several.</summary>
    public AudioListener Listener => listeners.Count > 0 ? listeners.Get(0) : AudioListener.Default;

    /// <summary>Everywhere the game is listening from.</summary>
    public AudioListenerSet Listeners => listeners;

    /// <summary>
    ///     The occlusion pass: what answers whether there is a wall in the way, how often it is
    ///     asked, and how quickly the answer takes effect.
    /// </summary>
    /// <remarks>
    ///     <b>Its <c>Provider</c> is null until something sets one</b>, and until then every voice's
    ///     occlusion is zero — nothing in the way. That is deliberate: this engine cannot cast a ray,
    ///     because the only thing that can binds a native physics library and a game with sound is
    ///     not required to have one. <c>Vixen.Audio.Physics</c> supplies a provider for games that
    ///     do; anything implementing <see cref="IAudioOcclusionProvider" /> does for games that
    ///     would rather answer it themselves.
    /// </remarks>
    public AudioOcclusion Occlusion => occlusion;

    /// <summary>The reverb zones in the level, and what the listener is currently standing in.</summary>
    /// <remarks>
    ///     Unlike <see cref="Occlusion" /> this needs nothing from outside the engine: a zone is a
    ///     volume test against the listener, which is arithmetic. It drives named parameters on
    ///     <see cref="Parameters" />, so a level with zones and no parameter sheet does the sums and
    ///     changes nothing.
    /// </remarks>
    public AudioReverbZones ReverbZones { get; } = new();

    /// <summary>Whether spatial sounds are panned through a head model rather than between speakers.</summary>
    /// <remarks>
    ///     <b>Headphones only.</b> Amplitude panning has a left and a right and no front, back, above
    ///     or below; a head model has all of them. Over speakers it is worse than panning, because
    ///     each ear hears both channels and the cues arrive crossed — so this belongs behind a
    ///     headphone setting and not behind a quality slider. Stereo devices only.
    /// </remarks>
    public bool UseHrtf {
        get => Mixer.UseHrtf;
        set => Mixer.UseHrtf = value;
    }

    /// <summary>How many pairs of ears there are. One, unless somebody asked for split-screen.</summary>
    public int ListenerCount => listeners.Count;

    /// <summary>What the subsystem was doing as of the last <see cref="Update()" />.</summary>
    public AudioStatistics Statistics { get; private set; }

    /// <summary>Moves the listener.</summary>
    /// <param name="value">Where the ears are now.</param>
    /// <remarks>Call it from one thread. The mixer picks it up on the next block.</remarks>
    public void SetListener(in AudioListener value) => SetListeners(AudioListenerSet.Single(value));

    /// <summary>Moves every listener at once.</summary>
    /// <param name="value">Where all the ears are now.</param>
    /// <remarks>
    ///     <para>
    ///         Split-screen, and nothing else. One set of speakers cannot represent four places
    ///         honestly, so <c>Spatializer</c> blends the direction across the listeners and takes the
    ///         level from whichever hears the sound best — see the note there for why summing and
    ///         nearest-wins were both rejected.
    ///     </para>
    ///     <para>
    ///         An empty set is not silence: it is the default listener at the origin, because a scene
    ///         that forgot its listener should be audible and wrong rather than silent and mysterious.
    ///     </para>
    /// </remarks>
    public void SetListeners(in AudioListenerSet value) {
        listeners = value.Count > 0 ? value : AudioListenerSet.Default;
        publishedListener.Write(listeners);
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

    /// <summary>What a sound's playback rate currently is.</summary>
    /// <param name="handle">Which one.</param>
    /// <returns>Its multiplier, or zero if the handle is stale.</returns>
    /// <remarks>
    ///     The pair of <see cref="GainOf" />, and wanted for the same reason: an event's variation
    ///     decides a play's pitch, so the only way to find out what a sound is actually running at is
    ///     to ask.
    /// </remarks>
    public float PitchOf(VoiceHandle handle) => TryResolve(handle, out var voice) ? voice.Pitch : 0f;

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

    /// <summary>The engine-wide parameters, if any have been loaded.</summary>
    /// <remarks>
    ///     The continuous half of the authoring layer, beside <see cref="Snapshots" />' discrete one:
    ///     a snapshot is a named mix arrived at over a duration, and a parameter is a dial held at a
    ///     position. Stepped by <see cref="Update(float)" />.
    /// </remarks>
    public MixerParameters? Parameters { get; private set; }

    /// <summary>Resolves engine-wide parameters against this engine's mixer.</summary>
    /// <param name="definitions">The parameters and what they drive.</param>
    /// <param name="problems">Everything that did not resolve. Empty is the good case.</param>
    /// <returns>The parameters, also left on <see cref="Parameters" />.</returns>
    /// <remarks>
    ///     Called after <see cref="LoadMixer" />, because the buses, sends and effects the automation
    ///     names have to exist before they can be found. <see cref="LoadMixer" /> does it itself for
    ///     the parameters an asset declares.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="definitions" /> is null.</exception>
    public MixerParameters LoadParameters(
        IReadOnlyList<AudioBusParameterDefinition> definitions,
        out IReadOnlyList<string> problems
    ) {
        Parameters = new(Mixer, definitions, out problems);
        return Parameters;
    }

    /// <summary>Gives a sound a set of parameters, at their defaults.</summary>
    /// <param name="handle">Which sound. A stale handle does nothing.</param>
    /// <param name="sheet">What parameters it has, and what moving them does.</param>
    /// <returns>Whether the handle named a live sound.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Per sound, which is what makes a parameter different from a snapshot.</b> A snapshot
    ///         is a named state of the whole mix and moves every voice on a bus together; this moves
    ///         one. "This player is underwater and that one is not" cannot be said any other way, and
    ///         it is the shape a voice-chat session actually has.
    ///     </para>
    ///     <para>
    ///         Attaching allocates nothing — the values live in a table sized for the whole pool — so
    ///         it is safe on the play of every footstep.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="sheet" /> is null.</exception>
    public bool AttachParameters(VoiceHandle handle, AudioParameterSheet sheet) {
        ArgumentNullException.ThrowIfNull(sheet);

        if (!TryResolve(handle, out _)) {
            return false;
        }

        voiceParameters.Attach(handle.Index, handle.Generation, sheet);
        return true;
    }

    /// <summary>The parameters a sound is running, if any.</summary>
    /// <param name="handle">Which sound.</param>
    /// <returns>Its sheet, or null.</returns>
    public AudioParameterSheet? ParametersOf(VoiceHandle handle) =>
        handle.IsValid && (uint)handle.Index < (uint)voices.Length
            ? voiceParameters.SheetOf(handle.Index, handle.Generation)
            : null;

    /// <summary>Points one of a sound's parameters at a value.</summary>
    /// <param name="handle">Which sound.</param>
    /// <param name="parameter">Which parameter, by its index in the sheet.</param>
    /// <param name="value">Where it should go. Clamped to the parameter's range.</param>
    /// <returns>Whether there was such a parameter on such a sound.</returns>
    /// <remarks>
    ///     <b>A target and not a position.</b> The value moves at the rate the parameter's
    ///     <c>SeekSeconds</c> asked for, stepped by <see cref="Update(float)" />. A parameter driven
    ///     by a gameplay boolean would otherwise cross its whole range in one frame, and a filter
    ///     cutoff crossing two octaves in one frame is a click.
    /// </remarks>
    public bool SetParameter(VoiceHandle handle, int parameter, float value) =>
        TryResolve(handle, out _) && voiceParameters.SetTarget(handle.Index, handle.Generation, parameter, value);

    /// <summary>Points one of a sound's parameters at a value, by name.</summary>
    /// <param name="handle">Which sound.</param>
    /// <param name="name">What the parameter is called.</param>
    /// <param name="value">Where it should go.</param>
    /// <returns>Whether there was such a parameter on such a sound.</returns>
    /// <remarks>
    ///     Resolves the name every call. Code setting a parameter every frame should ask the sheet for
    ///     the index once and use the overload that takes it.
    /// </remarks>
    public bool SetParameter(VoiceHandle handle, string name, float value) {
        var sheet = ParametersOf(handle);
        var index = sheet?.IndexOf(name) ?? -1;
        return index >= 0 && SetParameter(handle, index, value);
    }

    /// <summary>Where one of a sound's parameters currently is.</summary>
    /// <param name="handle">Which sound.</param>
    /// <param name="parameter">Which parameter.</param>
    /// <returns>Its value, which is not always the one it was last pointed at.</returns>
    public float ParameterOf(VoiceHandle handle, int parameter) =>
        handle.IsValid && (uint)handle.Index < (uint)voices.Length
            ? voiceParameters.ValueOf(handle.Index, handle.Generation, parameter)
            : 0f;

    /// <summary>Every knob in the mix, reachable by name — the runtime half of live update.</summary>
    /// <remarks>
    ///     Built on first use, because a game that never opens an editor session never needs it and a
    ///     dedicated server certainly does not.
    /// </remarks>
    public MixControl Control => control ??= new(this);

    /// <summary>How many layers are waiting on their delay.</summary>
    public int PendingLayers => deferred.Count;

    /// <summary>How many layers were never played because the pending table was full.</summary>
    public long DroppedLayers => deferred.Dropped;

    /// <summary>Holds a play until its delay has run out. Stepped by <see cref="Update(float)" />.</summary>
    internal void Defer(AudioEvent sound, in AudioEventPlayback attributes, float seconds) =>
        deferred.Schedule(sound, attributes, seconds);

    /// <summary>Drops everything waiting on one event.</summary>
    internal void CancelDeferred(AudioEvent sound) => deferred.Cancel(sound);

    /// <summary>How many frames the device has rendered since the engine was built.</summary>
    /// <remarks>
    ///     <b>The one clock in the subsystem that cannot drift.</b> Written by the audio thread as it
    ///     renders, so it counts samples that were actually produced rather than wall-clock time that
    ///     may or may not correspond to them. Everything musical is scheduled against it —
    ///     <see cref="PlaybackSettings.StartFrame" /> is a position on this line.
    /// </remarks>
    public long RenderedFrames => Interlocked.Read(ref renderedFrames);

    /// <summary>How many voices may be heard at once, or zero if every voice is real.</summary>
    public int AudibleVoices => audibleVoices;

    /// <summary>How many voices can sound at once.</summary>
    /// <remarks>Fixed at construction from <see cref="AudioEngineOptions.VoiceCapacity" />; the pool never grows.</remarks>
    public int VoiceCapacity => voices.Length;

    /// <summary>How easy a sound is to hear, counting its gain and how far away it is.</summary>
    /// <param name="handle">Which one.</param>
    /// <returns>Its audibility, or zero if the handle is stale.</returns>
    /// <remarks>
    ///     The number <see cref="TrySteal" /> ranks by, exposed because anything else deciding what to
    ///     cut — an event at its instance limit, an overlay listing what is audible — wants to rank by
    ///     the same one. Not a level in decibels and not comparable across sounds of different
    ///     loudness: it is what the mixer knows before it has rendered a block.
    /// </remarks>
    public float AudibilityOf(VoiceHandle handle) => TryResolve(handle, out var voice) ? voice.Audibility : 0f;

    /// <summary>How occluded a sound currently is: 0 clear, 1 blocked.</summary>
    /// <param name="handle">Which one.</param>
    /// <returns>Where its occlusion has got to, which lags the raycast by <see cref="AudioOcclusion.SeekSeconds" />.</returns>
    public float OcclusionOf(VoiceHandle handle) => TryResolve(handle, out var voice) ? voice.Occlusion : 0f;

    /// <summary>Points a sound's own send at a bus, at a level.</summary>
    /// <param name="handle">Which sound.</param>
    /// <param name="bus">Which bus a copy of it goes to. Out of range turns the send off.</param>
    /// <param name="level">How much, as a linear gain. Zero turns it off and costs nothing.</param>
    /// <returns>Whether the sound was still there.</returns>
    /// <remarks>
    ///     <b>Meant to be called every frame.</b> Both fields are plain scalars the audio thread
    ///     reads, on the same terms as gain — the worst outcome is a change landing one block late.
    ///     That is what lets a reverb amount follow an emitter across a room without a queue, which
    ///     is the thing a bus send could never do because everything on the bus shares its level.
    /// </remarks>
    public bool SetSend(VoiceHandle handle, int bus, float level) {
        if (!TryResolve(handle, out var voice)) {
            return false;
        }

        voice.SendBus = (uint)bus < (uint)Mixer.Buses.Count ? bus : -1;
        voice.SendLevel = MathF.Max(level, 0f);
        return true;
    }

    /// <summary>How much of a sound is going to its own send.</summary>
    /// <param name="handle">Which sound.</param>
    /// <returns>The level, or zero if it has no send or is gone.</returns>
    public float SendLevelOf(VoiceHandle handle) =>
        TryResolve(handle, out var voice) && voice.SendBus >= 0 ? voice.SendLevel : 0f;

    /// <summary>Which bus a sound's own send reaches, or −1.</summary>
    /// <param name="handle">Which sound.</param>
    public int SendBusOf(VoiceHandle handle) => TryResolve(handle, out var voice) ? voice.SendBus : -1;

    /// <summary>The authored low-pass cutoff on a sound, in hertz, or zero for none.</summary>
    /// <param name="handle">Which one.</param>
    /// <remarks>
    ///     What the parameter curves worked out, and not the air absorption — those are two filters
    ///     for two reasons and only this one is anybody's decision.
    /// </remarks>
    public float LowPassOf(VoiceHandle handle) =>
        TryResolve(handle, out var voice) ? voice.ParameterLowPassHz : 0f;

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

        // Before the parameters, so a curve drawn against occlusion reads this frame's answer rather
        // than last frame's — which for a door that just shut is the difference between the muffling
        // arriving with it and arriving after it.
        occlusion.Update(voices, listeners, deltaSeconds);

        // Before Parameters.Step for the same reason, and against listener zero: with a split screen
        // there is no single room the players are all in, and the first listener is the one whose
        // speakers the mix is going to.
        ReverbZones.Apply(listeners.Get(0).Position, Parameters);

        // Before the collection sweep below, so a voice that ended this frame is stepped one last
        // time and then dropped, rather than being dropped and then stepped against a slot somebody
        // else has already taken.
        // Before the parameters, so a layer that fired this frame has its sheet stepped in the same
        // frame it started rather than one later — which for a layer whose whole life is fifty
        // milliseconds is most of it.
        deferred.Step(deltaSeconds);

        voiceParameters.Step(voices, deltaSeconds);
        Parameters?.Step(deltaSeconds);

        // After the parameters, because a gain curve changes how audible a voice is and the ranking
        // should be against this frame's answer rather than last frame's.
        var virtualVoices = Rank();
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
            VirtualVoices = virtualVoices,
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

            // Read before the add below, so it is the frame this block *starts* at — which is what a
            // scheduled voice measures itself against.
            Mixer.Render(destination, frameCount, rendered, Interlocked.Read(ref renderedFrames));
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
            Describe(claimed, index, settings);

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
        Describe(stolen, victim, settings);

        // Published last, and read first by the audio thread: everything above it is visible by the
        // time the flag is seen.
        Volatile.Write(ref stolen.StealPending, 1);
        Interlocked.Increment(ref stolenVoices);
        return new VoiceHandle(victim, stolen.Generation);
    }

    void Describe(Voice voice, int index, in PlaybackSettings settings) {
        // Clamped rather than rejected: a bus index that no longer names a bus is a stale asset, and
        // routing it to the master is audible in a way that silently dropping the sound is not.
        voice.Bus = (uint)settings.Bus < (uint)Mixer.Buses.Count ? settings.Bus : 0;

        // A send naming a bus that no longer exists is dropped rather than clamped to the master:
        // routing a stale reverb send to the master would be an audible mistake, where losing the
        // send is only a missing effect.
        voice.SendBus = (uint)settings.SendBus < (uint)Mixer.Buses.Count ? settings.SendBus : -1;
        voice.SendLevel = MathF.Max(settings.SendLevel, 0f);
        voice.Gain = settings.Gain;
        voice.Pitch = settings.Pitch;
        voice.Pan = settings.Pan;
        voice.Priority = settings.Priority;
        voice.StartFrame = settings.StartFrame;
        voice.IsSpatial = settings.IsSpatial;
        voice.PublishSpatial(settings.Spatial);

        // A stolen slot does not go through Voice.Reset — the audio thread picks the new source up
        // where it would have retired the old one — so the automation the previous sound was running
        // would otherwise still be on it. A footstep that took an underwater voice's slot would be
        // underwater, at whatever gain that voice's curves had last worked out.
        //
        // The sheet itself is safe: it is keyed by generation, so the next Update drops it. These
        // four are what the audio thread reads directly, and it reads them before that Update.
        voice.ParameterGain = 1f;
        voice.ParameterPitch = 1f;
        voice.ParameterLowPassHz = 0f;
        voice.ParameterHighPassHz = 0f;

        // And the same argument for occlusion, in both places it is held: the voice, which the
        // parameters read, and the tracker's target, which would otherwise seek it straight back to
        // however blocked the sound it replaced was.
        voice.Occlusion = 0f;
        occlusion.Clear(index);
    }

    /// <summary>Decides which of the playing voices are heard this frame, if not all of them are.</summary>
    /// <remarks>
    ///     <para>
    ///         Priority first and audibility second — the same two keys, in the same order, as a steal
    ///         uses, because it is the same judgement about which sound matters least. What differs is
    ///         the consequence: a steal ends a sound, and this only stops rendering one.
    ///     </para>
    ///     <para>
    ///         <b>An insertion sort, which is the right one here and usually is not.</b> The ranking
    ///         barely changes between frames — a sound does not become inaudible in sixteen
    ///         milliseconds — so the array arrives nearly sorted, which is the case insertion sort is
    ///         linear on and every other sort is not.
    ///     </para>
    ///     <para>
    ///         Paused voices are left out: they render nothing already, so counting them against the
    ///         budget would silence something audible on behalf of something that is not.
    ///     </para>
    /// </remarks>
    int Rank() {
        if (audibleVoices == 0) {
            return 0;
        }

        var count = 0;

        for (var i = 0; i < voices.Length; i++) {
            var voice = voices[i];

            if ((VoiceState)Volatile.Read(ref voice.State) is not VoiceState.Playing) {
                voice.Virtual = false;
                continue;
            }

            ranking[count++] = i;
            rankPriorities[i] = voice.Priority;
            rankAudibility[i] = voice.Audibility;
        }

        for (var i = 1; i < count; i++) {
            var key = ranking[i];
            var j = i - 1;

            while (j >= 0 && Precedes(key, ranking[j])) {
                ranking[j + 1] = ranking[j];
                j--;
            }

            ranking[j + 1] = key;
        }

        for (var i = 0; i < count; i++) {
            voices[ranking[i]].Virtual = i >= audibleVoices;
        }

        return Math.Max(count - audibleVoices, 0);
    }

    bool Precedes(int a, int b) =>
        rankPriorities[a] != rankPriorities[b]
            ? rankPriorities[a] > rankPriorities[b]
            : rankAudibility[a] > rankAudibility[b];

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
