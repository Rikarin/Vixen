// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vixen.Audio.Diagnostics;

/// <summary>The wire an editor drives a running game's mix down.</summary>
/// <remarks>
///     <para>
///         <b>Loopback only, and not configurable.</b> It binds to <see cref="IPAddress.Loopback" />
///         and nothing else: a listener on a game's process that answers the network is a way into
///         that process, and the fact that all it can do is move a fader is not an argument anybody
///         should have to make. An editor on another machine — a devkit session — tunnels, which is
///         one line of <c>ssh</c> and puts the authentication somewhere that has some.
///     </para>
///     <para>
///         <b>Off unless something starts it.</b> Nothing here runs in a build that did not ask, and
///         a shipping build should not ask. There is no authentication because there is nothing to
///         authenticate against on a loopback socket, which is exactly why it must stay on one.
///     </para>
///     <para>
///         <b>Writes are queued and applied on the game thread.</b> The mixer's whole threading model
///         is one writer and one reader; a socket thread writing bus gains would be a third party to
///         an arrangement that has room for two. So a <c>set</c> is parsed on the socket thread and
///         performed in <see cref="Update" />, which is where every other change to the mix happens.
///     </para>
///     <para>
///         <b>Reads come from a snapshot.</b> Walking the bus list from another thread while the game
///         thread adds to it is the other half of the same problem, so <see cref="Update" /> leaves a
///         copy behind and the socket thread answers from that. It is a few frames stale, which for
///         something a human is looking at is no staleness at all.
///     </para>
///     <para>
///         The protocol is lines of text, because the client is a tool and a human debugging it with
///         <c>nc</c> is a feature: <c>list</c>, <c>get &lt;path&gt;</c>, <c>set &lt;path&gt;
///         &lt;value&gt;</c>, <c>bye</c>.
///     </para>
/// </remarks>
/// <param name="control">The mix this drives.</param>
public sealed class MixControlServer(MixControl control) : IDisposable {
    readonly Lock gate = new();
    readonly List<(string Path, float Value)> pending = [];
    readonly List<MixControlInfo> snapshot = [];

    TcpListener? listener;
    Thread? accepting;
    volatile bool running;
    long applied;
    long refreshed;

    /// <summary>How often the snapshot a client reads is brought up to date.</summary>
    /// <remarks>
    ///     Ten times a second. A human moving a fader cannot see faster, and rebuilding it every frame
    ///     would allocate the whole control list sixty times a second for nobody's benefit.
    /// </remarks>
    public int RefreshHz { get; set; } = 10;

    /// <summary>Which port it is listening on, or zero if it is not.</summary>
    public int Port => EndPoint?.Port ?? 0;

    /// <summary>Exactly where it is bound, which is always a loopback address.</summary>
    /// <remarks>
    ///     Exposed so that "it is loopback only" is a thing a test can assert rather than a thing the
    ///     documentation says. Probing for it from the network cannot: a connection to an address
    ///     nothing is listening on is dropped rather than refused on any machine with a firewall, so
    ///     the test would hang instead of failing.
    /// </remarks>
    public IPEndPoint? EndPoint { get; private set; }

    /// <summary>Whether it is listening.</summary>
    public bool IsRunning => running;

    /// <summary>Whether a client is connected.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>How many changes have been applied since it started.</summary>
    public long AppliedChanges => Interlocked.Read(ref applied);

    /// <summary>Starts listening on the loopback interface.</summary>
    /// <param name="port">Which port, or zero to be given a free one — read it back from <see cref="Port" />.</param>
    /// <exception cref="InvalidOperationException">It is already running.</exception>
    public void Start(int port = 0) {
        lock (gate) {
            if (running) {
                throw new InvalidOperationException("The mix control server is already running.");
            }

            listener = new(IPAddress.Loopback, port);
            listener.Start();
            EndPoint = (IPEndPoint)listener.LocalEndpoint;
            running = true;

            accepting = new(Accept) {
                IsBackground = true,
                Name = "Vixen Mix Control",

                // Below everything. A tool that is a human moving a slider has no deadline at all,
                // and it must never be the reason a frame or a block was late.
                Priority = ThreadPriority.BelowNormal
            };

            accepting.Start();
        }

        // So a client that connects before the game has ticked once still gets a list. Start is
        // called from the game thread, which is the thread allowed to walk the mixer.
        Refresh();
    }

    /// <summary>Stops listening and drops any client.</summary>
    public void Stop() {
        Thread? joining;

        lock (gate) {
            if (!running) {
                return;
            }

            running = false;
            listener?.Stop();
            listener = null;
            joining = accepting;
            accepting = null;
            EndPoint = null;
        }

        joining?.Join(TimeSpan.FromSeconds(1));
        IsConnected = false;
    }

    /// <summary>Applies what a client asked for, and refreshes what it can read. Once a frame.</summary>
    public void Update() {
        if (!running) {
            return;
        }

        lock (gate) {
            foreach (var (path, value) in pending) {
                if (control.TrySet(path, value)) {
                    Interlocked.Increment(ref applied);
                }
            }

            pending.Clear();
        }

        var now = Environment.TickCount64;
        var interval = 1_000 / Math.Max(RefreshHz, 1);

        if (now - refreshed >= interval) {
            Refresh();
        }
    }

    void Refresh() {
        refreshed = Environment.TickCount64;
        var fresh = control.Enumerate();

        lock (gate) {
            snapshot.Clear();
            snapshot.AddRange(fresh);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    void Accept() {
        while (running) {
            TcpClient? client = null;

            try {
                client = listener?.AcceptTcpClient();
            } catch (SocketException) {
                // Stop() closed the listener out from under the accept, which is how it is meant to
                // end. Anything else and the loop's own condition will have gone false too.
            } catch (ObjectDisposedException) {
                // Same.
            }

            if (client is null) {
                return;
            }

            IsConnected = true;

            try {
                Serve(client);
            } catch (IOException) {
                // A client that went away mid-sentence. Not an error anybody can act on.
            } catch (SocketException) {
                // Same.
            } finally {
                IsConnected = false;
                client.Dispose();
            }
        }
    }

    void Serve(TcpClient client) {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        writer.WriteLine("vixen-mix 1");

        while (running && reader.ReadLine() is { } line) {
            if (!Respond(line.Trim(), writer)) {
                return;
            }
        }
    }

    /// <summary>Answers one command.</summary>
    /// <returns>Whether the conversation continues.</returns>
    bool Respond(string line, TextWriter writer) {
        if (line.Length == 0) {
            return true;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        switch (parts) {
            case ["bye"]:
                writer.WriteLine("bye");
                return false;

            case ["list"]:
                lock (gate) {
                    foreach (var info in snapshot) {
                        writer.WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"control {info.Path} {info.Kind} {info.Value} {info.Minimum} {info.Maximum}"
                        ));
                    }
                }

                writer.WriteLine("end");
                return true;

            case ["get", var path]:
                if (control.TryGet(path, out var value)) {
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"value {value}"));
                } else {
                    writer.WriteLine("error unknown path");
                }

                return true;

            case ["set", var path, var text]:
                if (!float.TryParse(text, CultureInfo.InvariantCulture, out var wanted)) {
                    writer.WriteLine("error not a number");
                    return true;
                }

                // Checked here so a typo is reported to the human who made it, and performed on the
                // game thread so the mixer only ever has one writer.
                if (!control.TryGet(path, out _)) {
                    writer.WriteLine("error unknown path");
                    return true;
                }

                lock (gate) {
                    pending.Add((path, wanted));
                }

                writer.WriteLine("ok");
                return true;

            default:
                writer.WriteLine("error unknown command");
                return true;
        }
    }
}
