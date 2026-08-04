// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;

namespace Vixen.Live.Placement;

/// <summary>Starts realms with <c>Process.Start</c>. The production path of this backend.</summary>
public sealed class SystemProcessHost : IRealmProcessHost {
    /// <summary>The one everybody uses. Stateless.</summary>
    public static SystemProcessHost Instance { get; } = new();

    /// <inheritdoc />
    public IRealmProcessHandle Start(RealmProcessRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var info = new ProcessStartInfo(request.Executable) {
            // Redirected on all three, and stdin is the one that matters: it is how the launcher
            // says "drain" (RealmSignals), and a process whose stdin is the launcher's own console
            // would be racing a developer's keyboard for it.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = request.WorkingDirectory ?? ""
        };

        foreach (var argument in request.Arguments) {
            // The collection, never a joined string: an encoded RealmSpec contains characters a
            // shell would take an interest in, and quoting them correctly on three platforms is a
            // problem .NET has already solved.
            info.ArgumentList.Add(argument);
        }

        foreach (var variable in request.Environment) {
            info.Environment[variable.Key] = variable.Value;
        }

        var process = Process.Start(info)
            ?? throw new InvalidOperationException($"`{request.Executable}` did not start and did not say why.");

        return new Handle(process);
    }

    sealed class Handle : IRealmProcessHandle {
        readonly Process process;

        bool disposed;

        public string Id { get; }

        public bool HasExited {
            get {
                try {
                    return process.HasExited;
                } catch (InvalidOperationException) {
                    // Racing the process's own disposal. "Gone" is the honest answer and the one
                    // every caller of this would have written in the catch block anyway.
                    return true;
                }
            }
        }

        public int ExitCode {
            get {
                try {
                    return process.HasExited ? process.ExitCode : 0;
                } catch (InvalidOperationException) {
                    return 0;
                }
            }
        }

        public event Action<string>? OutputLine;

        public Handle(Process started) {
            process = started;
            Id = started.Id.ToString(CultureInfo.InvariantCulture);

            process.OutputDataReceived += OnLine;

            // Standard error too. A realm that fails to load its map says so there, and a launcher
            // that only read stdout would report "it exited" for a process that explained itself.
            process.ErrorDataReceived += OnLine;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public bool WriteLine(string line) {
            if (HasExited) {
                return false;
            }

            try {
                process.StandardInput.WriteLine(line);
                process.StandardInput.Flush();

                return true;
            } catch (Exception failure) when (failure is IOException or ObjectDisposedException or InvalidOperationException) {
                // It exited between the check and the write, which is not an error condition — it is
                // the ordinary race between draining a realm and the realm finishing on its own.
                return false;
            }
        }

        public void Kill() {
            try {
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                }
            } catch (Exception failure) when (failure is InvalidOperationException or NotSupportedException) {
                // Already gone, or a platform that will not say. Either way there is nothing left
                // to kill and nothing a caller could do differently.
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellation) => process.WaitForExitAsync(cancellation);

        public void Dispose() {
            if (disposed) {
                return;
            }

            disposed = true;
            process.OutputDataReceived -= OnLine;
            process.ErrorDataReceived -= OnLine;
            process.Dispose();
        }

        void OnLine(object sender, DataReceivedEventArgs line) {
            if (line.Data is { } text) {
                OutputLine?.Invoke(text);
            }
        }
    }
}
