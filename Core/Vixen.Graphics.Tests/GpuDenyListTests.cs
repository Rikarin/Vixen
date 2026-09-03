// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Tests;

/// <summary>The one question about a GPU that no capability query can answer.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here is really about a false positive.</b> A deny-list that misses a
///     broken driver costs the crash it was already going to have; a deny-list that matches hardware
///     nobody meant to refuse costs a black screen on a machine that works, on a build already
///     shipped. So the interesting cases are the ones where it must <em>not</em> fire.
/// </remarks>
public sealed class GpuDenyListTests {
    /// <summary>Nothing is denied by an empty list, which is every machine's state by default.</summary>
    [Fact]
    public void TheEmptyListDeniesNothing() {
        Assert.False(GpuDenyList.Empty.IsDenied("Apple M1 Max", "1.2.3", out var reason));
        Assert.Null(reason);
        Assert.Empty(GpuDenyList.Empty.Rules);
    }

    /// <summary>The adapter is matched as a substring, case-insensitively.</summary>
    /// <remarks>
    ///     ⚠ The same GPU is <c>Mali-G78</c> on one device and <c>Mali-G78 MC14</c> on another. A
    ///     rule keyed on equality stops matching after an OTA update and looks like coverage while
    ///     doing nothing, which is the worse of the two failures.
    /// </remarks>
    [Theory]
    [InlineData("Mali-G78")]
    [InlineData("Mali-G78 MC14")]
    [InlineData("ARM Mali-G78 MC14")]
    [InlineData("arm mali-g78")]
    public void TheAdapterMatchesAsASubstring(string name) {
        var list = new GpuDenyList([new("Mali-G78", GpuDenyList.Any, "dynamic rendering is a lie here")]);

        Assert.True(list.IsDenied(name, "38.1.0", out var reason));
        Assert.Contains("dynamic rendering is a lie here", reason, StringComparison.Ordinal);
        Assert.Contains(name, reason, StringComparison.Ordinal);
    }

    /// <summary>A different GPU from the same vendor is not denied.</summary>
    [Fact]
    public void ADifferentAdapterIsNotDenied() {
        var list = new GpuDenyList([new("Mali-G78", GpuDenyList.Any, "broken")]);

        Assert.False(list.IsDenied("Mali-G710", "38.1.0", out _));
    }

    /// <summary>A rule naming a driver version applies to that version and not to others.</summary>
    [Fact]
    public void TheDriverVersionNarrowsTheRule() {
        var list = new GpuDenyList([new("Adreno", "512.502", "crashes on rotation")]);

        Assert.True(list.IsDenied("Adreno (TM) 640", "512.502", out _));
        Assert.False(list.IsDenied("Adreno (TM) 640", "512.530", out _));
    }

    /// <summary>A rule may name a driver version and no particular adapter.</summary>
    /// <remarks>
    ///     A vendor's driver branch is sometimes the whole story, and that is not a catch-all: it
    ///     still names one of the two fields.
    /// </remarks>
    [Fact]
    public void AWildcardAdapterWithANamedDriverIsAllowed() {
        var list = new GpuDenyList([new(GpuDenyList.Any, "512.502", "this branch is broken everywhere")]);

        Assert.True(list.IsDenied("Adreno (TM) 640", "512.502", out _));
        Assert.False(list.IsDenied("Adreno (TM) 640", "512.530", out _));
    }

    /// <summary>A rule that matches every adapter and every driver is refused outright.</summary>
    /// <remarks>
    ///     ⚠ <b>The footgun this type exists to not have.</b> A curated list is content somebody
    ///     edits, and <c>* | * | …</c> is one keystroke from a rule for one device — it would deny
    ///     every GPU on every machine, which for a shipped game is a content update that turns the
    ///     picture off.
    /// </remarks>
    [Fact]
    public void ARuleThatMatchesEverythingIsRefused() {
        Assert.Throws<ArgumentException>(() => new GpuDenyList([new(GpuDenyList.Any, GpuDenyList.Any, "oops")]));
        Assert.Throws<ArgumentException>(() => new GpuDenyList([new(GpuDenyList.Any, null, "oops")]));
        Assert.Throws<FormatException>(() => GpuDenyList.Parse("* | * | oops"));
    }

    /// <summary>The text form: comments, blank lines, and three fields.</summary>
    [Fact]
    public void ParsesTheTextForm() {
        var list = GpuDenyList.Parse(
            """
            # The device deny-list.

            Mali-G78 | *       | VK_KHR_dynamic_rendering is advertised and unimplemented
            Adreno   | 512.502 | crashes in vkCreateSwapchainKHR on rotation  # seen on the 640
            """
        );

        Assert.Equal(2, list.Rules.Count);
        Assert.Equal("Mali-G78", list.Rules[0].Adapter);
        Assert.Equal(GpuDenyList.Any, list.Rules[0].DriverVersion);
        Assert.Equal("512.502", list.Rules[1].DriverVersion);
        Assert.Equal("crashes in vkCreateSwapchainKHR on rotation", list.Rules[1].Reason);
    }

    /// <summary>A malformed line is reported with its number, not skipped.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument-first assertion.</b> A parser that dropped the line it could not read
    ///     would leave a green run, a quiet log, and the device the rule was written for exactly as
    ///     broken as before — the failure shape this repository keeps rediscovering. So the two ways
    ///     of writing a useless rule, an empty adapter and an empty reason, are failures too.
    /// </remarks>
    [Theory]
    [InlineData("Mali-G78 | *", "2 field")]
    [InlineData("Mali-G78 | * | reason | extra", "4 field")]
    [InlineData(" | * | reason", "adapter field is empty")]
    [InlineData("Mali-G78 | * | ", "reason field is empty")]
    public void AMalformedLineIsAFailureWithItsNumber(string line, string expected) {
        var failure = Assert.Throws<FormatException>(() => GpuDenyList.Parse("# a comment\n" + line));

        Assert.Contains("line 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>An empty file, or one that is all comments, is a list that denies nothing.</summary>
    [Fact]
    public void AFileOfCommentsIsAnEmptyList() =>
        Assert.Empty(GpuDenyList.Parse("# nothing here yet\n\n   \n").Rules);

    /// <summary>The reason names the adapter and the driver, because a log reader has neither.</summary>
    [Fact]
    public void TheReasonNamesWhatWasRefused() {
        var list = new GpuDenyList([new("Adreno", "512.502", "crashes on rotation")]);

        Assert.True(list.IsDenied("Adreno (TM) 640", "512.502", out var reason));
        Assert.Contains("Adreno (TM) 640", reason, StringComparison.Ordinal);
        Assert.Contains("512.502", reason, StringComparison.Ordinal);
        Assert.Contains("crashes on rotation", reason, StringComparison.Ordinal);
    }
}
