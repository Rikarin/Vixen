// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;

namespace Vixen.Editor.SceneView;

/// <summary>What one out-of-process player is for.</summary>
public enum PlayerRole {
    /// <summary>A standalone game with no networking.</summary>
    Standalone,

    /// <summary>The authoritative server.</summary>
    Server,

    /// <summary>A client connecting to the server.</summary>
    Client
}

/// <summary>How to launch one player process.</summary>
/// <param name="Role">What it is for.</param>
/// <param name="Executable">The player to run.</param>
/// <param name="Arguments">What to pass it, beyond what the role and the inspector port add.</param>
/// <param name="WorkingDirectory">Where to run it, or <see langword="null" /> for the project root.</param>
/// <param name="InspectorPort">The port the remote inspector attaches on, or zero for none.</param>
public readonly record struct PlayerLaunch(
    PlayerRole Role,
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    int InspectorPort = 0
);

/// <summary>One running player process.</summary>
public sealed class PlayerSession : IDisposable {
    readonly Process process;
    bool disposed;

    /// <summary>What it is for.</summary>
    public PlayerRole Role { get; }

    /// <summary>Its process id, for a log line and for the session panel.</summary>
    public int ProcessId { get; }

    /// <summary>The port the remote inspector attaches on, or zero.</summary>
    public int InspectorPort { get; }

    /// <summary>Whether it is still running.</summary>
    public bool IsRunning {
        get {
            try {
                return !disposed && !process.HasExited;
            } catch (InvalidOperationException) {
                // The process was never started or has already been reaped. Either way it is not
                // running, and a session panel asking a dead session how it is doing is ordinary.
                return false;
            }
        }
    }

    /// <summary>What it exited with, or <see langword="null" /> while it is running.</summary>
    public int? ExitCode => IsRunning ? null : Exited();

    /// <summary>Raised when the process ends, however it ended.</summary>
    public event Action<PlayerSession>? Ended;

    internal PlayerSession(Process process, PlayerRole role, int inspectorPort) {
        this.process = process;
        Role = role;
        InspectorPort = inspectorPort;
        ProcessId = process.Id;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Ended?.Invoke(this);
    }

    /// <summary>Asks it to close, and kills it if it will not.</summary>
    /// <param name="grace">How long to wait for it to go on its own.</param>
    /// <remarks>
    ///     ⚠ <b>Killed rather than left</b>, because the case this exists for is a game that hung —
    ///     doc 11 names isolating one as half the reason the out-of-process topology exists. A player
    ///     the editor politely asked to stop and then forgot about is a process holding a port and a
    ///     lock on the content the next run needs.
    /// </remarks>
    public void Stop(TimeSpan grace = default) {
        if (!IsRunning) {
            return;
        }

        try {
            process.CloseMainWindow();

            if (!process.WaitForExit(grace == default ? TimeSpan.FromSeconds(3) : grace)) {
                process.Kill(entireProcessTree: true);
            }
        } catch (InvalidOperationException) {
            // It exited between the check and the ask, which is the ordinary race and not a failure.
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        Stop();
        disposed = true;
        process.Dispose();
    }

    int? Exited() {
        try {
            return process.ExitCode;
        } catch (InvalidOperationException) {
            return null;
        }
    }
}

/// <summary>The player processes the editor has launched.</summary>
/// <remarks>
///     <para>
///         <b>Doc 11's second play topology, and networking is what requires it.</b> Testing a
///         server-authoritative game needs a server and several clients, and none of that can happen
///         inside one editor process. It doubles as the way to check release-configuration behaviour
///         and to isolate a game that hangs, which is why a hung player is killed rather than waited
///         for.
///     </para>
///     <para>
///         <b>The incremental cost is process launch and a panel.</b> The remote inspector already
///         exists (doc 13), so what is here is the launching, the bookkeeping and the ports —
///         deliberately not a second inspector.
///     </para>
///     <para>
///         ⚠ <b>Ports are assigned by the set, not by each launch.</b> Two clients that both took the
///         inspector's default port would mean the second one silently not being attachable, which
///         presents as "the remote inspector does not work with more than one client" and is very
///         hard to see.
///     </para>
/// </remarks>
public sealed class PlayerSessions : IDisposable {
    readonly List<PlayerSession> sessions = [];
    bool disposed;

    /// <summary>The first port handed out; each session takes the next one.</summary>
    public int FirstInspectorPort { get; set; } = 34000;

    /// <summary>What is running, in the order it was launched.</summary>
    public IReadOnlyList<PlayerSession> Sessions => sessions;

    /// <summary>How many are still alive.</summary>
    public int RunningCount => sessions.Count(static session => session.IsRunning);

    /// <summary>Raised when a session is launched or ends.</summary>
    public event Action<PlayerSessions>? Changed;

    /// <summary>How this launch should be spelled out on a command line.</summary>
    /// <param name="launch">The launch.</param>
    /// <param name="port">The inspector port to use, or zero for none.</param>
    /// <returns>The arguments, in order.</returns>
    /// <remarks>
    ///     Separated from <see cref="Launch" /> so that what the editor would run is something a test
    ///     can assert on and a person can copy out of a log — which is the first thing anybody wants
    ///     when a player will not start.
    /// </remarks>
    public static IReadOnlyList<string> ArgumentsFor(PlayerLaunch launch, int port) {
        List<string> arguments = ["--role", launch.Role.ToString().ToLowerInvariant()];

        if (port > 0) {
            arguments.Add("--inspector-port");
            arguments.Add(port.ToString(CultureInfo.InvariantCulture));
        }

        if (launch.Arguments is { } extra) {
            arguments.AddRange(extra);
        }

        return arguments;
    }

    /// <summary>Starts a player.</summary>
    /// <param name="launch">What to run.</param>
    /// <returns>The session.</returns>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    public PlayerSession Launch(PlayerLaunch launch) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(launch.Executable);

        var port = launch.InspectorPort > 0 ? launch.InspectorPort : NextPort();

        var start = new ProcessStartInfo(launch.Executable) {
            UseShellExecute = false,
            WorkingDirectory = launch.WorkingDirectory ?? string.Empty
        };

        foreach (var argument in ArgumentsFor(launch, port)) {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"'{launch.Executable}' did not start. A player that has not been built yet is the "
                + "usual cause; build the player head before asking the editor to run one."
            );

        var session = new PlayerSession(process, launch.Role, port);
        session.Ended += _ => Changed?.Invoke(this);

        sessions.Add(session);
        Changed?.Invoke(this);

        return session;
    }

    /// <summary>Starts a server and some clients against it.</summary>
    /// <param name="executable">The player to run.</param>
    /// <param name="clients">How many clients.</param>
    /// <param name="workingDirectory">Where to run them.</param>
    /// <returns>The sessions, the server first.</returns>
    /// <remarks>
    ///     The shape doc 16's testing needs, as one call rather than as three the user has to get in
    ///     the right order — a client launched before its server is a client that fails to connect
    ///     and looks like a networking bug.
    /// </remarks>
    public IReadOnlyList<PlayerSession> LaunchNetworked(
        string executable,
        int clients = 1,
        string? workingDirectory = null
    ) {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentOutOfRangeException.ThrowIfNegative(clients);

        List<PlayerSession> launched = [
            Launch(new(PlayerRole.Server, executable, [], workingDirectory))
        ];

        for (var index = 0; index < clients; index++) {
            launched.Add(Launch(new(PlayerRole.Client, executable, [], workingDirectory)));
        }

        return launched;
    }

    /// <summary>Stops everything.</summary>
    public void StopAll() {
        foreach (var session in sessions) {
            session.Stop();
        }

        Changed?.Invoke(this);
    }

    /// <summary>Forgets the sessions that have ended.</summary>
    /// <returns>How many were forgotten.</returns>
    public int Prune() {
        var removed = 0;

        for (var index = sessions.Count - 1; index >= 0; index--) {
            if (!sessions[index].IsRunning) {
                sessions[index].Dispose();
                sessions.RemoveAt(index);
                removed++;
            }
        }

        if (removed > 0) {
            Changed?.Invoke(this);
        }

        return removed;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var session in sessions) {
            session.Dispose();
        }

        sessions.Clear();
    }

    int NextPort() {
        var port = FirstInspectorPort;

        while (sessions.Any(session => session.InspectorPort == port)) {
            port++;
        }

        return port;
    }
}
