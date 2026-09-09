// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace Vixen.Platform.Windows.Tests;

/// <summary>The accent read that fills <c>--accent</c> on Windows.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An accent reader is the one shape of platform read whose failure is invisible.</b>
///         Every plausible wrong answer is a colour — a hard-coded blue, a <c>COLORREF</c> read
///         red-first, an alpha of zero — and a window painted the wrong blue looks exactly like a
///         window painted the right one to anybody who has not compared it with the machine's own
///         settings. So the two halves are asserted differently and neither is "it produced a
///         colour".
///     </para>
///     <para>
///         <b>The decode is a pure function and is asserted everywhere.</b> A <c>COLORREF</c> is
///         <c>0x00BBGGRR</c> and the hex a human writes is the other way round, so the swap is the
///         mistake most likely to be made and the least likely to be seen. That test runs on this
///         machine, on the Linux runner and on the Windows one.
///     </para>
///     <para>
///         <b>The read is asserted against the registry the system colours actually live in</b> —
///         <c>HKCU\Control Panel\Colors</c>, written by <c>SetSysColors</c> and read by
///         <c>GetSysColor</c> — rather than against a second call to the same function, which would
///         assert only that the function is deterministic. That half runs on a Windows runner and
///         skips elsewhere, which is the honest arrangement: this reader was written on a Mac.
///     </para>
///     <para>
///         ⚠ <b>And the wrong answer this cannot see.</b> A Windows sitting at its default selection
///         colour makes "the read" and "the classic blue" the same value, so a constant would
///         survive on a stock machine exactly as a hard-coded <c>#007AFF</c> survives
///         <c>MacOSAccentTests</c>. A runner whose accent has been moved off the default is where
///         this test is worth the most.
///     </para>
/// </remarks>
public partial class WindowsAccentTests {
    /// <summary><c>RRF_RT_REG_SZ</c>.</summary>
    const uint RrfRtRegSz = 0x00000002;

    [Theory]
    // Blue in the high byte of the three, which is what makes this worth a test: written as a
    // human writes a colour, 0x123456 is red 0x12 — and that is the wrong reading.
    [InlineData(0x00123456u, 0x56, 0x34, 0x12)]
    [InlineData(0x00000000u, 0x00, 0x00, 0x00)]
    [InlineData(0x00FFFFFFu, 0xFF, 0xFF, 0xFF)]
    // The classic Windows selection blue, #0078D7, as the COLORREF that stands for it.
    [InlineData(0x00D77800u, 0x00, 0x78, 0xD7)]
    public void AColorRefIsBlueGreenRedAndOpaque(uint colorRef, int red, int green, int blue) {
        var colour = WindowsAccent.FromColorRef(colorRef);

        Assert.Equal(red / 255f, colour.R, 0.001f);
        Assert.Equal(green / 255f, colour.G, 0.001f);
        Assert.Equal(blue / 255f, colour.B, 0.001f);

        // A COLORREF has no alpha channel, so anything but opaque is this reader inventing one —
        // and an accent at zero alpha is invisible rather than wrong, which a colour comparison
        // alone would call a pass.
        Assert.Equal(1f, colour.A);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheAccentReadIsTheSystemColourRatherThanAConstant() {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Reads Windows system colours.");

        var accent = WindowsAccent.Read();

        // The instrument first: `GetSysColor` cannot fail, so `WindowsAccent` treats two equal
        // answers as "could not be asked". Reaching here with `Unknown` means that fired, which on
        // a working Windows is the reader being broken rather than the machine being unusual.
        Assert.True(accent.IsKnown, "COLOR_HIGHLIGHT and COLOR_HIGHLIGHTTEXT came back equal.");

        var read = accent.Color!.Value;

        // ⚠ Component by component against an independent read of the same colour, out of the
        // registry the classic system colours are stored in. A hard-coded blue and a COLORREF read
        // red-first both survive "it is a colour" and neither survives this.
        var expected = RawSystemColour("Hilight");

        Assert.SkipWhen(expected is null, @"HKCU\Control Panel\Colors\Hilight is not set on this machine.");

        var (r, g, b) = expected!.Value;

        Assert.Equal(r / 255f, read.R, 0.001f);
        Assert.Equal(g / 255f, read.G, 0.001f);
        Assert.Equal(b / 255f, read.B, 0.001f);

        // Opaque, and the pair, because half a read does not move the theme's token:
        // `PlatformInput` puts `SystemPalette.PlatformAccentClass` on for the accent AND its text
        // or not at all.
        Assert.Equal(1f, read.A);
        Assert.NotNull(accent.Text);
        Assert.Equal(1f, accent.Text!.Value.A);
    }

    /// <summary>
    ///     One <c>HKCU\Control Panel\Colors</c> value — <c>"0 120 215"</c>, three decimal bytes —
    ///     read here rather than trusted from the reader under test.
    /// </summary>
    /// <param name="name">The value name.</param>
    /// <returns>The components, or <see langword="null" /> where the value is absent or malformed.</returns>
    [SupportedOSPlatform("windows")]
    static unsafe (int R, int G, int B)? RawSystemColour(string name) {
        var buffer = stackalloc char[64];
        var size = (uint)(64 * sizeof(char));

        var status = RegGetValue(
            unchecked((nint)(int)0x80000001),
            @"Control Panel\Colors",
            name,
            RrfRtRegSz,
            out _,
            buffer,
            ref size
        );

        if (status != 0) {
            return null;
        }

        var parts = new string(buffer).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3) {
            return null;
        }

        return (
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture)
        );
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int RegGetValue(
        nint key,
        string subKey,
        string value,
        uint flags,
        out uint type,
        void* data,
        ref uint size
    );
}
