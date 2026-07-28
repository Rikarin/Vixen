// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Vixen.Audio;
using Vixen.Audio.Codecs;
using Vixen.Audio.Devices;
using Vixen.Audio.Mixing;
using Vixen.Net;
using Vixen.Net.Sessions;
using Vixen.Net.Transport.Udp;

namespace Vixen.Samples.VoiceChat;

/// <summary>
///     Two players talking to each other over real UDP sockets, with the audio path joined end to
///     end: capture, gate, encode, send, jitter, conceal, decode, mix.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this proves that the unit tests cannot.</b> The codec tests drive a stand-in for a
///         network, because losing and reordering a packet on purpose is the point and no real
///         transport can be asked to do it on cue. That leaves one thing unchecked: whether the two
///         halves actually fit — whether what <see cref="VoiceSender" /> hands out survives a real
///         session, a real socket and a real relay, and comes out of a mixer as sound. This is that.
///     </para>
///     <para>
///         <b>Both ends run in one process, and the sockets are still real.</b> Loopback UDP is a
///         genuine datagram path with genuine framing and a genuine MTU; what it lacks is loss, which
///         is exactly the part already covered by tests. One process means <c>dotnet run</c>
///         demonstrates the whole thing without two terminals and a firewall prompt.
///     </para>
/// </remarks>
static class Program {
    const int TalkFrames = 250; // 20 ms each: five seconds of conversation.

    static int Main() {
        Console.WriteLine("Vixen voice chat — two talkers over loopback UDP\n");

        var listen = new IPEndPoint(IPAddress.Loopback, 0);
        var factory = new UdpDatagramSocketFactory();

        using var serverTransport = new UdpTransport(factory, new UdpTransportOptions { ListenEndPoint = listen });
        using var server = new NetworkSession(serverTransport);
        var relay = new Relay(server);
        server.StartServer();

        var address = serverTransport.ListeningOn as IPEndPoint;

        if (address is null) {
            Console.Error.WriteLine("the server did not bind");
            return 1;
        }

        Console.WriteLine($"server listening on {address}");

        using var alice = new Peer("Alice", factory, address, toneHz: 220f);
        using var bob = new Peer("Bob", factory, address, toneHz: 330f);

        // A frame is 20 ms of a talker, which is also one Opus packet — so the loop below is the
        // game loop, at the rate voice actually moves.
        var step = TimeSpan.FromMilliseconds(20);

        for (var frame = 0; frame < TalkFrames; frame++) {
            // Alice talks for the first half, Bob for the second, and they overlap in the middle —
            // so both the one-talker and the two-talker cases actually run.
            alice.Talk(aloud: frame < TalkFrames * 3 / 5);
            bob.Talk(aloud: frame > TalkFrames * 2 / 5);

            alice.Pump(step);
            bob.Pump(step);
            server.Update(step, relay);

            // Real sockets, so the receive path needs real time to have happened in.
            Thread.Sleep(4);
        }

        // Let the tail of the last words arrive.
        for (var i = 0; i < 25; i++) {
            alice.Pump(step);
            bob.Pump(step);
            server.Update(step, relay);
            Thread.Sleep(4);
        }

        Console.WriteLine();
        alice.Report();
        bob.Report();

        Console.WriteLine($"\nserver relayed {relay.Relayed} packets, {relay.Bytes:N0} bytes");
        Console.WriteLine($"which is {relay.Bytes * 8.0 / 5_000:N0} kbit a second, for two people talking most of the time");

        // Not "packets arrived" but "a sound came out of the mixer", which is the whole claim.
        var heard = alice.Loudest > 0.01f && bob.Loudest > 0.01f;
        Console.WriteLine(heard ? "\nBoth ends heard the other. ✓" : "\nSomebody heard nothing. ✗");
        return heard ? 0 : 1;
    }
}

/// <summary>The server: forwards each talker's packets to everybody else, and decodes nothing.</summary>
sealed class Relay(NetworkSession session) : ISessionMessageHandler {
    readonly byte[] buffer = new byte[VoiceLink.MaxBytes];

    public long Relayed { get; private set; }

    public long Bytes { get; private set; }

    public void OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> payload) {
        if (channel is not Channel.Sequenced) {
            return;
        }

        var length = VoiceLink.Relay(buffer, from, payload);

        foreach (var player in session.Players) {
            // Not back to the talker. Hearing yourself a hundred milliseconds late is the single
            // most disorienting thing a voice system can do.
            if (player.Id != from) {
                session.SendToPlayer(player.Id, buffer.AsSpan(0, length), Channel.Sequenced);
            }
        }

        Relayed++;
        Bytes += length;
    }
}

/// <summary>One player: a microphone, an encoder, a session, and a mixer full of other people.</summary>
sealed class Peer : ISessionMessageHandler, IDisposable {
    readonly string name;
    readonly UdpTransport transport;
    readonly NetworkSession session;

    readonly VoiceSender sender;
    readonly Dictionary<byte, VoiceReceiver> talkers = [];
    readonly Dictionary<byte, VoiceHandle> voices = [];

    readonly AudioEngine engine;
    readonly NullAudioDevice device;
    readonly AudioBus voiceBus;

    readonly byte[] outgoing = new byte[VoiceLink.MaxBytes];
    readonly byte[] packet = new byte[OpusPacketEncoder.MaxPacketBytes];
    readonly float[] captured;
    readonly float[] block;

    readonly float toneHz;
    int phase;

    public Peer(string name, UdpDatagramSocketFactory factory, IPEndPoint server, float toneHz) {
        this.name = name;
        this.toneHz = toneHz;

        transport = new UdpTransport(factory, new UdpTransportOptions {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RemoteEndPoint = server
        });

        session = new NetworkSession(transport);
        session.StartClient();

        sender = new VoiceSender();
        captured = new float[sender.FrameSize];

        // The null backend, because this sample is about the path and not about the speakers: it
        // renders on demand, which is what lets the loop below assert that sound actually came out
        // rather than merely that packets arrived. A real game swaps this one line for OpenAL.
        var backend = new NullAudioBackend();
        device = (NullAudioDevice)backend.OpenDevice(new AudioDeviceOptions {
            Format = new AudioFormat(48_000, 2),
            BufferFrames = 480
        });

        engine = new AudioEngine(device, new AudioEngineOptions { VoiceCapacity = 16, StreamOnOwnThread = false });
        voiceBus = engine.CreateBus("Voice");
        block = new float[480 * 2];
    }

    public long FramesHeard { get; private set; }

    /// <summary>The loudest sample that ever left this peer's mixer.</summary>
    public float Loudest { get; private set; }

    /// <summary>Produces 20 ms of microphone, or of silence.</summary>
    /// <remarks>
    ///     <b>A tone stands in for a talker</b> so the sample runs on a machine with no microphone
    ///     and produces the same numbers every time. Swapping it for
    ///     <c>CaptureSampleProvider</c> over a real <c>IAudioCaptureDevice</c> is the only change a
    ///     game makes here — everything downstream is already what a game would run.
    /// </remarks>
    public void Talk(bool aloud) {
        for (var i = 0; i < captured.Length; i++) {
            captured[i] = aloud ? 0.4f * MathF.Sin(2f * MathF.PI * toneHz * phase++ / 48_000f) : 0f;
        }

        if (!aloud) {
            phase = 0;
        }

        sender.Write(captured);
    }

    /// <summary>Drains what the encoder produced onto the wire, and what the wire produced into the mixer.</summary>
    public void Pump(TimeSpan step) {
        while (sender.TryRead(packet, out var header, out var length)) {
            var written = VoiceLink.Write(outgoing, header, packet.AsSpan(0, length));
            session.SendToServer(outgoing.AsSpan(0, written), Channel.Sequenced);
        }

        session.Update(step, this);

        foreach (var (id, receiver) in talkers) {
            FramesHeard += receiver.Pump();

            // A talker becomes a voice in the mixer the first time they say anything, and stays one.
            // Positioning it is then ordinary spatial audio — and the per-instance parameters are
            // what would put this one player underwater without touching the other.
            if (!voices.TryGetValue(id, out var handle) || !engine.IsPlaying(handle)) {
                voices[id] = engine.Play(receiver.Provider, new PlaybackSettings { Bus = voiceBus.Index });
            }
        }

        engine.Update((float)step.TotalSeconds);

        // Pull a block through the mixer, which is the only thing that turns "packets arrived" into
        // "a sound came out". 480 frames at 48 kHz is the 10 ms a device callback would ask for.
        device.Render(block);

        foreach (var sample in block) {
            Loudest = MathF.Max(Loudest, MathF.Abs(sample));
        }
    }

    public void OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> payload) {
        if (channel is not Channel.Sequenced || !VoiceLink.ReadRelayed(payload, out var who, out var header, out var opus)) {
            return;
        }

        // One receiver per talker, because Opus carries state between packets: two people through
        // one decoder would each be extrapolating from the other's voice when a packet went missing.
        if (!talkers.TryGetValue(who, out var receiver)) {
            receiver = new VoiceReceiver();
            talkers[who] = receiver;
        }

        receiver.Receive(header, opus);
    }

    public void Report() {
        var concealed = 0L;
        var late = 0L;

        foreach (var receiver in talkers.Values) {
            concealed += receiver.Concealed;
            late += receiver.Late;
        }

        Console.WriteLine(
            $"{name,-6} sent {sender.Sent,4} packets, suppressed {sender.Suppressed,4} silent frames; "
            + $"heard {talkers.Count} talker(s), {FramesHeard,6} frames, concealed {concealed}, late {late}, "
            + $"peak {Loudest:F3}"
        );
    }

    public void Dispose() {
        foreach (var receiver in talkers.Values) {
            receiver.Dispose();
        }

        sender.Dispose();
        engine.Dispose();
        device.Dispose();
        session.Dispose();
        transport.Dispose();
    }
}
