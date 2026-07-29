// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text;

namespace Vixen.Platform.Linux;

/// <summary>The Linux clipboard, for everything that is not text.</summary>
/// <remarks>
///     <para>
///         <b>There is no clipboard on Linux.</b> There is a protocol by which one client offers a
///         list of MIME types and another asks for one of them, and the offering client — which has
///         to still be running — produces the bytes on demand. That is why an image survives closing
///         the application it was copied from on Windows and macOS and does not here unless
///         something is holding it, and why the tools this drives keep running after they exit
///         (see <see cref="ExternalTool" />).
///     </para>
///     <para>
///         <b>Wayland and X11 need different programs, and the session says which.</b>
///         <c>WAYLAND_DISPLAY</c> is set by a Wayland session and is what SDL itself keys on. A
///         Wayland session running XWayland has both, and <c>wl-copy</c> is the right one there —
///         its selection is visible to XWayland clients and the reverse is not reliably true.
///     </para>
///     <para>
///         <b>Text stays with the portable implementation.</b> SDL handles it on both display
///         servers, in-process, without a helper program, and it is the one format that always
///         works. What is added here is the two that never did.
///     </para>
/// </remarks>
/// <param name="fallback">The portable clipboard, which keeps the text half.</param>
[SupportedOSPlatform("linux")]
public sealed class LinuxClipboard(IClipboard fallback) : IClipboard {
    const string ImageFormat = "image/png";

    /// <inheritdoc />
    public bool HasText => fallback.HasText;

    /// <inheritdoc />
    public bool HasImage {
        get {
            foreach (var type in Types()) {
                if (type.StartsWith("image/", StringComparison.Ordinal)) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether either helper program is installed.</summary>
    /// <remarks>
    ///     What decides whether this implementation is used at all. Without one, images and custom
    ///     formats would be a pair of methods that always fail, which the portable implementation
    ///     already provides.
    /// </remarks>
    public static bool IsAvailable => IsWayland
        ? ExternalTool.Exists("wl-paste") && ExternalTool.Exists("wl-copy")
        : ExternalTool.Exists("xclip");

    static bool IsWayland =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) => fallback.TryGetText(out text);

    /// <inheritdoc />
    public bool SetText(string text) => fallback.SetText(text);

    /// <inheritdoc />
    public bool TryGetImage(out ClipboardImage image) {
        image = default;
        return TryGetData(ImageFormat, out var data) && PngImage.TryDecode(data.Span, out image);
    }

    /// <inheritdoc />
    public bool SetImage(in ClipboardImage image) {
        var png = PngImage.Encode(image);
        return png is not null && SetData(ImageFormat, png);
    }

    /// <summary>Reads a format's bytes.</summary>
    /// <param name="format">
    ///     A MIME type, passed through unchanged. That is the vocabulary both display servers use
    ///     for this — <c>image/png</c>, <c>text/uri-list</c>, an application's own
    ///     <c>application/x-…</c> — so there is nothing to translate.
    /// </param>
    /// <param name="data">The bytes.</param>
    /// <returns><see langword="false" /> if the clipboard holds nothing in that format.</returns>
    public bool TryGetData(string format, out ReadOnlyMemory<byte> data) {
        ArgumentException.ThrowIfNullOrEmpty(format);

        byte[] bytes;

        var read = IsWayland
            ? ExternalTool.TryRead("wl-paste", ["--no-newline", "--type", format], out bytes)
            : ExternalTool.TryRead("xclip", ["-selection", "clipboard", "-o", "-t", format], out bytes);

        data = read ? bytes : default;
        return read;
    }

    /// <inheritdoc cref="TryGetData" />
    public bool SetData(string format, ReadOnlySpan<byte> data) {
        ArgumentException.ThrowIfNullOrEmpty(format);

        return IsWayland
            ? ExternalTool.TryWrite("wl-copy", ["--type", format], data)
            : ExternalTool.TryWrite("xclip", ["-selection", "clipboard", "-i", "-t", format], data);
    }

    /// <inheritdoc />
    public void Clear() {
        if (IsWayland) {
            ExternalTool.TryWrite("wl-copy", ["--clear"], []);
            return;
        }

        // xclip has no clear, and the closest thing to one is owning the selection with nothing in
        // it. The portable implementation does the same through SDL, which is why this defers to it
        // rather than starting a process to write zero bytes.
        fallback.Clear();
    }

    /// <summary>What the clipboard is currently offering.</summary>
    static string[] Types() {
        byte[] bytes;

        var listed = IsWayland
            ? ExternalTool.TryRead("wl-paste", ["--list-types"], out bytes)
            : ExternalTool.TryRead("xclip", ["-selection", "clipboard", "-o", "-t", "TARGETS"], out bytes);

        return listed
            ? Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }
}
