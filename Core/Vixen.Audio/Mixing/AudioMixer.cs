// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Spatial;

namespace Vixen.Audio.Mixing;

/// <summary>The bus tree, the voice pool, and the loop that turns them into a block of samples.</summary>
/// <remarks>
///     <para>
///         <b>Vixen mixes in software and hands the result to the device.</b> That is the decision
///         everything else here follows from, and it was not the obvious one: OpenAL will spatialise
///         and mix for you, WebAudio has a whole node graph with panners and convolvers in it, and
///         both were rejected. Three reasons.
///     </para>
///     <para>
///         <b>One, the same sound on every platform.</b> OpenAL Soft's panner, a browser's panner and
///         a phone's mixer disagree about attenuation curves, about what a cone does at its edge, and
///         about how a stereo source is placed. A game mixed on a desktop would have to be re-mixed
///         for the web. Here the backend receives finished interleaved frames and its only job is to
///         get them to the hardware.
///     </para>
///     <para>
///         <b>Two, it is testable.</b> <c>docs/plan/12</c> says audio correctness is tested at buffer
///         level, and that is only possible if there is a buffer to test — <see cref="Render" /> is a
///         function from a world state to samples, and every claim in this assembly's tests is an
///         assertion about numbers it returned.
///     </para>
///     <para>
///         <b>Three, effects and buses would have to be written twice otherwise</b>, once against
///         EFX and once against WebAudio's node graph, and neither maps onto the other.
///     </para>
///     <para>
///         The cost is CPU: a hundred voices at 48 kHz is a few per cent of one core, which is the
///         price every engine that owns its mixer pays and none of them regret.
///     </para>
/// </remarks>
public sealed class AudioMixer {
    readonly Voice[] voices;
    readonly Lock gate = new();
    readonly List<AudioBus> buses = [];

    AudioBus[] byIndex = [];
    AudioBus[] renderOrder = [];
    AudioFormat format;
    int maxFrames;
    int activeVoices;

    /// <summary>A mixer with a fixed number of voices.</summary>
    /// <param name="voiceCapacity">
    ///     How many sounds can play at once. Sixty-four, because that is more than a scene ever needs
    ///     audible at one time and because the pool is what makes playing a sound allocation-free.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="voiceCapacity" /> is not positive.</exception>
    public AudioMixer(int voiceCapacity = 64) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(voiceCapacity);

        voices = new Voice[voiceCapacity];

        for (var i = 0; i < voices.Length; i++) {
            voices[i] = new Voice();
        }

        Master = new AudioBus(0, "Master", null);
        Master.Attach(this);
        buses.Add(Master);
        Rebuild();
    }

    /// <summary>The bus everything eventually reaches.</summary>
    public AudioBus Master { get; }

    /// <summary>Every bus, master first, in the order they were created.</summary>
    public IReadOnlyList<AudioBus> Buses => buses;

    /// <summary>What it is rendering into. Not valid until <see cref="Prepare" />.</summary>
    public AudioFormat Format => format;

    /// <summary>The most frames one <see cref="Render" /> will be asked for.</summary>
    public int MaxFrames => maxFrames;

    /// <summary>How many voices there are in total.</summary>
    public int VoiceCapacity => voices.Length;

    /// <summary>How many were doing something in the last block.</summary>
    public int ActiveVoices => Volatile.Read(ref activeVoices);

    /// <summary>Adds a bus.</summary>
    /// <param name="name">What to call it. Unique — a duplicate is a mistake worth catching.</param>
    /// <param name="parent">What it sums into. The master by default.</param>
    /// <returns>The bus.</returns>
    /// <exception cref="ArgumentException">A bus of that name already exists.</exception>
    /// <remarks>
    ///     Buses are made at start-up, from a mixer asset or from code, and not during play. Making
    ///     one takes a lock and rebuilds the render order; doing it per frame would be visible.
    /// </remarks>
    public AudioBus CreateBus(string name, AudioBus? parent = null) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (gate) {
            if (FindBus(name) is not null) {
                throw new ArgumentException($"There is already a bus called '{name}'.", nameof(name));
            }

            var bus = new AudioBus(buses.Count, name, parent ?? Master);
            bus.Attach(this);
            buses.Add(bus);

            if (format.IsValid) {
                bus.Prepare(format, maxFrames);
            }

            Rebuild();
            return bus;
        }
    }

    /// <summary>Finds a bus by name.</summary>
    /// <param name="name">What it is called. Case-sensitive.</param>
    /// <returns>The bus, or <see langword="null" />.</returns>
    public AudioBus? FindBus(string name) {
        foreach (var bus in byIndex) {
            if (bus.Name == name) {
                return bus;
            }
        }

        return null;
    }

    /// <summary>Sizes every buffer for a device.</summary>
    /// <param name="deviceFormat">The device's format.</param>
    /// <param name="frames">The most frames one render will ask for.</param>
    /// <exception cref="ArgumentException">The format is not one anything can render into.</exception>
    public void Prepare(in AudioFormat deviceFormat, int frames) {
        if (!deviceFormat.IsValid) {
            throw new ArgumentException($"{deviceFormat} is not a format a mixer can render into.", nameof(deviceFormat));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);

        lock (gate) {
            format = deviceFormat;
            maxFrames = frames;

            foreach (var bus in buses) {
                bus.Prepare(deviceFormat, frames);
            }

            foreach (var voice in voices) {
                voice.Prepare(deviceFormat);
            }
        }
    }

    /// <summary>Renders one block.</summary>
    /// <param name="destination">
    ///     Interleaved, at least <c>frameCount × channels</c> floats. Overwritten, not added to.
    /// </param>
    /// <param name="frameCount">How many frames. No more than <see cref="MaxFrames" />.</param>
    /// <param name="listeners">Where the ears are.</param>
    /// <remarks>Runs on the audio thread. Takes no lock and allocates nothing.</remarks>
    public void Render(Span<float> destination, int frameCount, in AudioListenerSet listeners) {
        var channels = format.Channels;
        var samples = frameCount * channels;

        if (samples <= 0 || frameCount > maxFrames) {
            destination.Clear();
            return;
        }

        var order = renderOrder;
        var lookup = byIndex;

        foreach (var bus in order) {
            bus.Clear(frameCount);
        }

        var active = 0;

        foreach (var voice in voices) {
            var state = (VoiceState)Volatile.Read(ref voice.State);

            if (state is not (VoiceState.Playing or VoiceState.Paused or VoiceState.Stopping)) {
                continue;
            }

            active++;
            var bus = lookup[(uint)voice.Bus < (uint)lookup.Length ? voice.Bus : 0];

            if (!voice.Render(bus.Buffer[..samples], frameCount, listeners)) {
                // The one moment nothing is reading this voice's render state, and therefore the only
                // safe place to hand its slot to a sound that stole it.
                if (voice.TryTakePending(out var paused)) {
                    Volatile.Write(ref voice.State, (int)(paused ? VoiceState.Paused : VoiceState.Playing));
                    continue;
                }

                // Finished, not Free: the game thread collects it, which is where a streaming source
                // can be unregistered from the pump and a reference can be dropped without the audio
                // thread touching the garbage collector.
                Volatile.Write(ref voice.State, (int)VoiceState.Finished);
            }
        }

        Volatile.Write(ref activeVoices, active);

        foreach (var bus in order) {
            bus.Finish(frameCount);
            var source = bus.Buffer[..samples];

            if (bus.Parent is not null) {
                var target = bus.Parent.Buffer;

                for (var i = 0; i < samples; i++) {
                    target[i] += source[i];
                }

                continue;
            }

            // The master. The clamp is a guard and not a level control: a LimiterEffect is what
            // keeps a loud scene from distorting, and this is what stops a NaN out of a misbehaving
            // effect, or an overshoot nothing caught, reaching a driver. A 16-bit backend would wrap
            // a sample above one round to the opposite rail, which is the loudest click a machine
            // can make.
            for (var i = 0; i < samples; i++) {
                var value = source[i];
                destination[i] = float.IsNaN(value) ? 0f : Math.Clamp(value, -1f, 1f);
            }
        }
    }

    /// <summary>Stops every voice at once, without a fade.</summary>
    /// <remarks>What a scene change calls. Effects keep their tails unless <see cref="AudioBus.ResetEffects" /> is called too.</remarks>
    public void StopAll() {
        foreach (var voice in voices) {
            var state = (VoiceState)Volatile.Read(ref voice.State);

            if (state is VoiceState.Playing or VoiceState.Paused) {
                Interlocked.CompareExchange(ref voice.State, (int)VoiceState.Stopping, (int)state);
            }
        }
    }

    internal Voice[] Voices => voices;

    internal AudioBus BusAt(int index) => byIndex[(uint)index < (uint)byIndex.Length ? index : 0];

    /// <summary>Recomputes the render order after the graph's shape changed.</summary>
    /// <remarks>
    ///     Called by <see cref="AudioBus" /> when a send or a sidechain is added or removed. The
    ///     shape changes at start-up, not per frame.
    /// </remarks>
    internal void Invalidate() {
        lock (gate) {
            Rebuild();
        }
    }

    /// <summary>
    ///     Orders the buses so that everything a bus reads has already been written.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A topological sort, where sorting by depth used to do.</b> With only parent edges
    ///         the graph is a tree and "deepest first" is a correct order for free. A send is an edge
    ///         that does not follow the tree — an ambience bus three levels down sending into an aux
    ///         reverb hanging off the master — and depth says nothing useful about it. A sidechain is
    ///         a third kind of edge with the same requirement: the key has to have been rendered.
    ///     </para>
    ///     <para>
    ///         Kahn's algorithm over the three edge kinds. Cycles cannot arrive here — both
    ///         <see cref="AudioBus.AddSend" /> and <see cref="AudioBus.SetSidechain" /> refuse to
    ///         create one — so anything left unvisited would be a bug in that check rather than a
    ///         user error, and it is appended in depth order so a mistake degrades into the old
    ///         behaviour instead of silencing the mixer.
    ///     </para>
    /// </remarks>
    void Rebuild() {
        byIndex = [.. buses];

        var count = buses.Count;
        var after = new List<int>[count];
        var indegree = new int[count];

        for (var i = 0; i < count; i++) {
            after[i] = [];
        }

        foreach (var bus in buses) {
            // A bus's signal reaches its parent, so the parent has to come after it.
            if (bus.Parent is { } parent) {
                Edge(bus.Index, parent.Index);
            }

            // A send is the same relationship without the tree.
            foreach (var send in bus.Sends) {
                Edge(bus.Index, send.Target.Index);
            }

            // And a sidechain points the other way: the key has to have been rendered before the
            // bus that listens to it.
            if (bus.SidechainSource is { } key) {
                Edge(key.Index, bus.Index);
            }
        }

        var order = new List<AudioBus>(count);
        var ready = new Queue<AudioBus>();

        // Deepest first among the ones that are ready, so a plain tree comes out in exactly the
        // order it used to and the change is invisible to anything that did not add a send.
        foreach (var bus in buses.OrderByDescending(candidate => candidate.Depth)) {
            if (indegree[bus.Index] == 0) {
                ready.Enqueue(bus);
            }
        }

        while (ready.Count > 0) {
            var bus = ready.Dequeue();
            order.Add(bus);

            foreach (var next in after[bus.Index]) {
                if (--indegree[next] == 0) {
                    ready.Enqueue(byIndex[next]);
                }
            }
        }

        if (order.Count != count) {
            // Unreachable unless the cycle checks in AddSend and SetSidechain have a hole. Appending
            // the stragglers degrades into the old depth order rather than silencing the mixer,
            // which is the right way for a bug here to behave.
            foreach (var bus in buses) {
                if (!order.Contains(bus)) {
                    order.Add(bus);
                }
            }
        }

        renderOrder = [.. order];

        void Edge(int from, int to) {
            after[from].Add(to);
            indegree[to]++;
        }
    }
}
