// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vixen.Audio.Diagnostics;
using Vixen.Audio.Effects;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>The wire, driven by a client that is this test.</summary>
public sealed class MixControlServerTests : IDisposable {
    readonly AudioEngine engine;
    readonly MixControlServer server;

    public MixControlServerTests() {
        (engine, _) = AudioTestData.Engine();
        server = new(engine.Control);
    }

    public void Dispose() {
        server.Dispose();
        engine.Dispose();
    }

    /// <summary>A client, and the plumbing to say something and hear the answer.</summary>
    sealed class Session : IDisposable {
        readonly TcpClient client;
        readonly StreamReader reader;
        readonly StreamWriter writer;

        public Session(int port) {
            client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            var stream = client.GetStream();
            reader = new(stream, Encoding.UTF8, leaveOpen: true);
            writer = new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            Greeting = reader.ReadLine() ?? string.Empty;
        }

        public string Greeting { get; }

        public string Say(string line) {
            writer.WriteLine(line);
            return reader.ReadLine() ?? string.Empty;
        }

        public void SayWithoutWaiting(string line) => writer.WriteLine(line);

        public List<string> SayAndReadUntilEnd(string line) {
            writer.WriteLine(line);
            var lines = new List<string>();

            while (reader.ReadLine() is { } answer && answer != "end") {
                lines.Add(answer);
            }

            return lines;
        }

        public void Dispose() {
            reader.Dispose();
            writer.Dispose();
            client.Dispose();
        }
    }

    Session Connect() {
        server.Start();
        Assert.True(server.IsRunning);
        Assert.True(server.Port > 0);
        return new(server.Port);
    }

    /// <summary>
    ///     A listener in a game's process is a way into that process, and "all it can do is move a
    ///     fader" is not an argument anybody should have to make. Asserted against the binding rather
    ///     than by probing from the network: a connection to an address nothing is listening on is
    ///     dropped rather than refused on any machine with a firewall, so a probe would hang.
    /// </summary>
    [Fact]
    public void ItBindsToLoopbackAndNothingElse() {
        server.Start();

        Assert.NotNull(server.EndPoint);
        Assert.True(IPAddress.IsLoopback(server.EndPoint.Address));

        // And is reachable there, so this is a claim about where it is bound rather than about it
        // being bound at all.
        using var local = new TcpClient();
        local.Connect(IPAddress.Loopback, server.Port);
        Assert.True(local.Connected);
    }

    [Fact]
    public void ItIsOffUntilSomethingStartsIt() {
        Assert.False(server.IsRunning);
        Assert.Equal(0, server.Port);
    }

    [Fact]
    public void ItAnnouncesItselfSoAClientKnowsWhatItIsTalkingTo() {
        using var session = Connect();
        Assert.Equal("vixen-mix 1", session.Greeting);
    }

    /// <summary>Including for a client that connects before the game has ticked once.</summary>
    [Fact]
    public void ListingIsWhatAnEditorDrawsFrom() {
        engine.CreateBus("Music").AddEffect(new ReverbEffect());

        using var session = Connect();
        var lines = session.SayAndReadUntilEnd("list");

        Assert.Contains(lines, l => l.StartsWith("control bus/Music/gain BusGain", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("control bus/Music/effect/0/Wet", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("control bus/Master/gain", StringComparison.Ordinal));
    }

    [Fact]
    public void GettingReadsTheMixAsItIs() {
        var music = engine.CreateBus("Music");
        music.Gain = 0.5f;

        using var session = Connect();
        var answer = session.Say("get bus/Music/gain");

        Assert.StartsWith("value ", answer, StringComparison.Ordinal);
        Assert.Equal(-6.02f, float.Parse(answer[6..], CultureInfo.InvariantCulture), 0.05f);
    }

    /// <summary>
    ///     The mixer's threading model is one writer and one reader; a socket thread writing bus gains
    ///     would be a third party to an arrangement with room for two.
    /// </summary>
    [Fact]
    public void SettingIsAcknowledgedAtOnceAndAppliedOnTheGameThread() {
        var music = engine.CreateBus("Music");
        using var session = Connect();

        Assert.Equal("ok", session.Say("set bus/Music/gain -12"));

        // Acknowledged, and not yet done — because nothing has called Update.
        Assert.Equal(1f, music.Gain);
        Assert.Equal(0, server.AppliedChanges);

        server.Update();

        Assert.Equal(0.2512f, music.Gain, 1e-3f);
        Assert.Equal(1, server.AppliedChanges);
    }

    [Fact]
    public void AnEffectKnobGoesTheSameWay() {
        var voice = engine.CreateBus("Voice");
        var filter = new BiquadFilterEffect { Frequency = 1_000f };
        voice.AddEffect(filter);

        using var session = Connect();
        Assert.Equal("ok", session.Say("set bus/Voice/effect/0/Frequency 400"));

        server.Update();
        Assert.Equal(400f, filter.Frequency);
    }

    [Fact]
    public void ATypoIsReportedToTheHumanWhoMadeItRatherThanSwallowed() {
        engine.CreateBus("Music");
        using var session = Connect();

        Assert.Equal("error unknown path", session.Say("set bus/Nowhere/gain 0"));
        Assert.Equal("error unknown path", session.Say("get bus/Nowhere/gain"));
        Assert.Equal("error not a number", session.Say("set bus/Music/gain loud"));
        Assert.Equal("error unknown command", session.Say("destroy everything"));

        server.Update();
        Assert.Equal(0, server.AppliedChanges);
    }

    /// <summary>Ignored means no answer at all, so the next command's answer is the next line.</summary>
    [Fact]
    public void AnEmptyLineIsIgnoredRatherThanBeingAnError() {
        engine.CreateBus("Music");
        using var session = Connect();

        session.SayWithoutWaiting("   ");
        Assert.Equal("ok", session.Say("set bus/Music/gain -6"));
    }

    [Fact]
    public void SayingGoodbyeEndsIt() {
        using var session = Connect();
        Assert.Equal("bye", session.Say("bye"));
    }

    [Fact]
    public void StoppingIsIdempotentAndLeavesNothingListening() {
        server.Start();
        var port = server.Port;

        server.Stop();
        server.Stop();

        Assert.False(server.IsRunning);

        using var client = new TcpClient();
        Assert.Throws<SocketException>(() => client.Connect(IPAddress.Loopback, port));
    }

    [Fact]
    public void StartingTwiceIsAMistakeAndSaysSo() {
        server.Start();
        Assert.Throws<InvalidOperationException>(() => server.Start());
    }

    /// <summary>A whole round trip: connect, read, change, and see it in what came out.</summary>
    [Fact]
    public void AFaderMovedOverTheWireChangesWhatIsHeard() {
        var (loud, device) = AudioTestData.Engine();

        using (loud) {
            var music = loud.CreateBus("Music");
            using var wire = new MixControlServer(loud.Control);
            wire.Start();

            loud.Play(AudioTestData.Constant(48_000, 1f), new Mixing.PlaybackSettings { Bus = music.Index });
            loud.Update(0f);

            var before = AudioTestData.Peak(AudioTestData.Render(device, 64));

            using var session = new Session(wire.Port);
            Assert.Equal("ok", session.Say("set bus/Music/gain -30"));
            wire.Update();

            var after = AudioTestData.Peak(AudioTestData.Render(device, 64));

            Assert.True(before > 0.6f, $"it was {before:F3} before");
            Assert.True(after < before * 0.1f, $"before {before:F3}, after {after:F3}");
        }
    }
}
