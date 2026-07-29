// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     The log where the platform's own tooling looks for it: <c>logcat</c> on Android, the unified
///     log on Apple platforms, the journal on Linux, the debugger's output window on Windows, and
///     the browser console on the web.
/// </summary>
/// <remarks>
///     <para>
///         The sink that makes mobile debugging possible at all. A phone has no terminal and a
///         crash there produces a native tombstone plus whatever <c>logcat</c> caught; a build whose
///         engine log never reaches <c>logcat</c> is a build whose last words are the operating
///         system's, not the engine's.
///     </para>
///     <para>
///         <b>Apple gets <c>syslog(3)</c>, not <c>os_log</c> proper.</b> The unified logging system
///         captures <c>syslog</c> writes, so the lines do appear in <c>log stream</c> and in
///         Console.app; what is lost is the subsystem/category pairing and the deferred formatting
///         that <c>os_log</c>'s own API gives. Reaching that API means <c>_os_log_impl</c>, which
///         takes a compiled format descriptor that cannot be produced from managed code — it needs a
///         native shim, and a shim belongs in <c>Vixen.Platform.Native</c> beside the other ones
///         rather than being the reason this sink does not exist.
///     </para>
///     <para>
///         <b>Windows gets <c>OutputDebugStringW</c></b>, which is what a debugger, DebugView and
///         the Visual Studio output window all read.
///     </para>
///     <para>
///         <b>The browser gets <see cref="Console" /></b>, whose output the WebAssembly runtime
///         already routes to <c>console.log</c>. Calling <c>console.log</c> through JS interop
///         directly would need a <c>browser-wasm</c> target this assembly does not have, and would
///         land in the same place.
///     </para>
///     <para>
///         On a platform with none of those, <see cref="IsSupported" /> is
///         <see langword="false" /> and writing does nothing — a sink that threw on an unrecognised
///         OS would take the process down for the sake of a log line.
///     </para>
/// </remarks>
public sealed partial class PlatformSink : LogRecordSink {
    /// <summary>The tag used when none is given.</summary>
    public const string DefaultTag = "Vixen";

    // syslog(3) priorities. LOG_USER (1 << 3) is the facility an application writes with.
    const int SyslogUser = 1 << 3;
    const int SyslogCritical = 2;
    const int SyslogError = 3;
    const int SyslogWarning = 4;
    const int SyslogNotice = 5;
    const int SyslogInfo = 6;
    const int SyslogDebug = 7;

    // android/log.h priorities.
    const int AndroidVerbose = 2;
    const int AndroidDebug = 3;
    const int AndroidInfo = 4;
    const int AndroidWarning = 5;
    const int AndroidError = 6;
    const int AndroidFatal = 7;

    static readonly PlatformLog Target = Detect();

    readonly Lock gate = new();
    readonly StringBuilder builder = new(256);
    readonly byte[] tagUtf8;

    /// <summary>Whether this platform has a log this sink can reach.</summary>
    public static bool IsSupported => Target != PlatformLog.None;

    /// <summary>The tag every line carries. What <c>logcat -s</c> filters on.</summary>
    public string Tag { get; }

    /// <summary>Creates a sink writing to the platform log.</summary>
    /// <param name="tag">The tag every line carries.</param>
    /// <param name="minimumLevel">The level below which nothing is written.</param>
    /// <param name="filter">
    ///     The filter to use, or <see langword="null" /> for one of this sink's own.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="tag" /> is null or empty.</exception>
    public PlatformSink(
        string tag = DefaultTag,
        LogLevel minimumLevel = LogLevel.Information,
        LogFilter? filter = null
    ) : base(filter) {
        ArgumentException.ThrowIfNullOrEmpty(tag);

        Tag = tag;
        tagUtf8 = NullTerminatedUtf8(tag);

        if (filter is null) {
            MinimumLevel = minimumLevel;
        }
    }

    /// <inheritdoc />
    protected override void Write(LogRecord record) {
        if (Target == PlatformLog.None) {
            return;
        }

        string line;

        lock (gate) {
            builder.Clear();
            builder.Append('[').Append(LogText.Abbreviate(record.Level)).Append("] ").Append(record.Category)
                .Append(": ");

            LogText.AppendMessage(builder, record);

            if (record.Exception is not null) {
                builder.Append(Environment.NewLine).Append(record.Exception);
            }

            line = builder.ToString();
        }

        switch (Target) {
            case PlatformLog.Windows:
                OutputDebugString(line);

                break;

            case PlatformLog.Android:
                WriteAndroid(AndroidPriority(record.Level), line);

                break;

            case PlatformLog.Syslog:
                WriteSyslog(SyslogUser | SyslogPriority(record.Level), line);

                break;

            case PlatformLog.Console:
                Console.WriteLine(line);

                break;

            default:
                break;
        }
    }

    /// <summary>
    ///     <c>syslog</c> reads its second argument as a format string, so a percent sign in a
    ///     message would be taken for a conversion and print whatever happened to be in the next
    ///     register. Doubling them is what makes an arbitrary log line safe to pass.
    /// </summary>
    internal static string EscapeFormatSpecifiers(string message) =>
        message.Contains('%', StringComparison.Ordinal)
            ? message.Replace("%", "%%", StringComparison.Ordinal)
            : message;

    static void WriteSyslog(int priority, string line) {
        var escaped = EscapeFormatSpecifiers(line);
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(escaped.Length) + 1);

        try {
            var written = Encoding.UTF8.GetBytes(escaped, buffer);
            buffer[written] = 0;
            Syslog(priority, buffer.AsSpan(0, written + 1));
        } finally {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    void WriteAndroid(int priority, string line) {
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(line.Length) + 1);

        try {
            var written = Encoding.UTF8.GetBytes(line, buffer);
            buffer[written] = 0;
            AndroidLogWrite(priority, tagUtf8, buffer.AsSpan(0, written + 1));
        } finally {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static PlatformLog Detect() {
        if (OperatingSystem.IsAndroid()) {
            return PlatformLog.Android;
        }

        if (OperatingSystem.IsBrowser()) {
            return PlatformLog.Console;
        }

        if (OperatingSystem.IsWindows()) {
            return PlatformLog.Windows;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
            || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst()) {
            return PlatformLog.Syslog;
        }

        return PlatformLog.None;
    }

    static int SyslogPriority(LogLevel level) => level switch {
        LogLevel.Trace => SyslogDebug,
        LogLevel.Debug => SyslogDebug,
        LogLevel.Information => SyslogInfo,
        LogLevel.Warning => SyslogWarning,
        LogLevel.Error => SyslogError,
        LogLevel.Critical => SyslogCritical,
        _ => SyslogNotice
    };

    static int AndroidPriority(LogLevel level) => level switch {
        LogLevel.Trace => AndroidVerbose,
        LogLevel.Debug => AndroidDebug,
        LogLevel.Information => AndroidInfo,
        LogLevel.Warning => AndroidWarning,
        LogLevel.Error => AndroidError,
        LogLevel.Critical => AndroidFatal,
        _ => AndroidInfo
    };

    static byte[] NullTerminatedUtf8(string text) {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);

        return bytes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OutputDebugStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void OutputDebugString(string message);

    [LibraryImport("log", EntryPoint = "__android_log_write")]
    private static partial int AndroidLogWrite(int priority, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> text);

    [LibraryImport("libc", EntryPoint = "syslog")]
    private static partial void Syslog(int priority, ReadOnlySpan<byte> format);

    enum PlatformLog {
        None,
        Windows,
        Android,
        Syslog,
        Console
    }
}
