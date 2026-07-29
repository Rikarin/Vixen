// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Vixen.Platform.Linux;

/// <summary>Running one of the desktop's own helper programs, and getting its bytes back.</summary>
/// <remarks>
///     <para>
///         <b>Why a process and not a library.</b> Both of the things this assembly cannot do
///         in-process — a file picker and a clipboard that carries more than text — are served on
///         Linux by D-Bus: the XDG desktop portal for the first, and the toolkit's own selection
///         owner for the second. There is no D-Bus client in the base class library, and adding one
///         means adding a native dependency and a message-serialisation layer to an engine that has
///         a stated policy against dependencies it does not need
///         (<c>docs/plan/01</c>). <c>zenity</c>, <c>kdialog</c>, <c>wl-copy</c> and <c>xclip</c> are
///         the programs the desktop already ships to do exactly this, they are what a user's session
///         is configured through, and inside a Flatpak sandbox they are themselves portal clients —
///         so going through them gets the portal's behaviour rather than bypassing it.
///     </para>
///     <para>
///         <b>Reading and writing are separate, because the processes behave differently.</b>
///         <c>wl-copy</c> and <c>xclip -i</c> keep running after they have read their input: on X11
///         and Wayland the clipboard has no store, the application that copied *is* the clipboard,
///         and something has to stay alive to answer the paste. So a write waits briefly for a
///         failure and treats "still running" as success, and only a read waits for the exit it is
///         actually going to get.
///     </para>
/// </remarks>
static class ExternalTool {
    /// <summary>How long a write is given to fail before it is assumed to be serving a selection.</summary>
    const int WriteTimeout = 250;

    /// <summary>How long a read is given, after which the tool is assumed to be stuck.</summary>
    /// <remarks>
    ///     Generous, because a paste can involve the source application re-encoding a large image,
    ///     and short enough that a clipboard owner that has hung does not hang the frame loop with
    ///     it.
    /// </remarks>
    const int ReadTimeout = 4000;

    /// <summary>Whether a program of this name is on the <c>PATH</c>.</summary>
    /// <remarks>
    ///     Looked up rather than attempted, so that a missing helper is a capability that is absent
    ///     rather than an exception thrown once per call. Not cached: a user who installs
    ///     <c>zenity</c> while the editor is open should get a file picker without restarting it,
    ///     and this is a handful of <c>stat</c> calls on a path nobody has made long.
    /// </remarks>
    public static bool Exists(string program) {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path)) {
            return false;
        }

        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries)) {
            if (File.Exists(Path.Combine(directory, program))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs a program and collects its standard output.</summary>
    /// <param name="program">The program name, resolved through the <c>PATH</c>.</param>
    /// <param name="arguments">The arguments, passed as a list so that nothing is quoted or split.</param>
    /// <param name="output">What it wrote.</param>
    /// <returns><see langword="false" /> if it could not be started, failed, timed out or said
    /// nothing.</returns>
    public static bool TryRead(string program, IReadOnlyList<string> arguments, out byte[] output) {
        output = [];

        try {
            using var process = Start(program, arguments, redirectInput: false, redirectOutput: true);

            if (process is null) {
                return false;
            }

            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);

            if (!process.WaitForExit(ReadTimeout)) {
                process.Kill(entireProcessTree: true);
                return false;
            }

            output = buffer.ToArray();
            return process.ExitCode == 0 && output.Length > 0;
        } catch (Exception error) when (error is InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception) {
            return false;
        }
    }

    /// <summary>Runs a program and feeds it bytes.</summary>
    /// <param name="program">The program name, resolved through the <c>PATH</c>.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="input">What to write to its standard input.</param>
    /// <returns><see langword="false" /> only if it could not be started or exited unhappily. See
    /// the remarks on this class about why still running is a success.</returns>
    public static bool TryWrite(string program, IReadOnlyList<string> arguments, ReadOnlySpan<byte> input) {
        try {
            using var process = Start(program, arguments, redirectInput: true, redirectOutput: false);

            if (process is null) {
                return false;
            }

            using (var stream = process.StandardInput.BaseStream) {
                stream.Write(input);
            }

            return !process.WaitForExit(WriteTimeout) || process.ExitCode == 0;
        } catch (Exception error) when (error is InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception) {
            return false;
        }
    }

    /// <summary>Runs a program without blocking the frame loop, and collects its standard output.</summary>
    /// <param name="program">The program name.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="cancellationToken">Kills the program, which is how a dialog is dismissed here.</param>
    /// <returns>Its exit code and its output, or <c>(-1, "")</c> if it could not be run.</returns>
    /// <remarks>
    ///     The dialog path, and the reason <see cref="INativeDialogs" /> is asynchronous: the helper
    ///     is showing a window the user is browsing in, which takes as long as they take, and the
    ///     frame loop keeps running throughout. Cancellation genuinely works here — the dialog is
    ///     another process and killing it is the platform's own way to close it — which is not true
    ///     on the other two desktops.
    /// </remarks>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    ) {
        try {
            using var process = Start(program, arguments, redirectInput: false, redirectOutput: true);

            if (process is null) {
                return (-1, string.Empty);
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode, output);
        } catch (OperationCanceledException) {
            return (-1, string.Empty);
        } catch (Exception error) when (error is InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception) {
            return (-1, string.Empty);
        }
    }

    static Process? Start(
        string program,
        IReadOnlyList<string> arguments,
        bool redirectInput,
        bool redirectOutput
    ) {
        var start = new ProcessStartInfo(program) {
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,

            // Redirected and dropped. A helper that cannot find a display writes a paragraph about
            // it to standard error, and inheriting our own would put it in the middle of the
            // application's log with no indication of where it came from.
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start);
    }
}
