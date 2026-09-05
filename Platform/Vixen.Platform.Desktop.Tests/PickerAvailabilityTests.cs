// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Platform.Desktop.Tests;

/// <summary>Telling "there is no file picker" apart from "the user cancelled".</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Ask what the SDL fallback prints on the day it does not run: "the user
///         cancelled".</b> <c>DesktopDialogs</c>' four pickers return <see langword="null" /> and an
///         empty list, because SDL 2 has no file dialog at all — and every one of those values is
///         also what the user pressing Cancel produces. The conflation is deliberate on
///         <see cref="INativeDialogs" />, and it is deliberate only because there is a *second*
///         channel that answers the other question.
///     </para>
///     <para>
///         <b>That channel is <see cref="PlatformCapabilities.NativeDialogs" />, and these are the
///         two halves of it together.</b> <c>DesktopSupplementTests</c> already pins the flag being
///         absent without a supplement; what was never asserted beside it is that the stubs behind
///         the missing flag really do answer nothing-chosen, so a caller that skipped the flag would
///         see a cancellation. Both facts in one place is what makes the pair readable — and
///         <c>PlatformExtensions.Pickers</c> is the pair spelled as one call.
///     </para>
/// </remarks>
public sealed class PickerAvailabilityTests {
    static DesktopPlatformOptions Options =>
        new() {
            Application = "Vixen.Tests",
            EnableGameControllers = false,
            VideoDriver = "dummy",
            RequestGpuSurface = false,
            UseNativeSupplement = false
        };

    /// <summary>Without a supplement there are no pickers, and asking for one reads as a cancel.</summary>
    [Fact]
    public async Task TheSdlFallbackAnswersNothingChosenAndSaysSoThroughTheCapability() {
        Assert.SkipUnless(SdlLibrary.IsAvailable, "SDL2 is not installed on this machine.");

        using var platform = new DesktopPlatform(Options);

        // The half a caller sees if it skips the flag: indistinguishable from Cancel.
        var token = TestContext.Current.CancellationToken;

        Assert.Null(await platform.Dialogs.OpenFileAsync(new(), cancellationToken: token));
        Assert.Empty(await platform.Dialogs.OpenFilesAsync(new(), cancellationToken: token));
        Assert.Null(await platform.Dialogs.SaveFileAsync(new(), cancellationToken: token));
        Assert.Null(await platform.Dialogs.OpenFolderAsync(new(), cancellationToken: token));

        // And the half that says why, which is the one an "Open…" menu item must read.
        Assert.False(platform.Has(PlatformCapabilities.NativeDialogs));
        Assert.Null(platform.Pickers());
    }

    /// <summary>A platform that has pickers hands them out.</summary>
    /// <remarks>
    ///     ⚠ <b>The predicate has to be able to be true, or it is a test that passes on the day the
    ///     capability stops being reported at all.</b> A supplement that supplies pickers is the only
    ///     thing that adds the flag, so a fake one is what makes the true branch reachable without a
    ///     desktop session.
    /// </remarks>
    [Fact]
    public void APlatformWithPickersHandsThemOut() {
        Assert.SkipUnless(SdlLibrary.IsAvailable, "SDL2 is not installed on this machine.");

        var supplement = new PickerSupplement();

        using var platform = new DesktopPlatform(Options with { Supplement = supplement });

        Assert.True(platform.Has(PlatformCapabilities.NativeDialogs));
        Assert.Same(supplement.Dialogs, platform.Pickers());
    }

    sealed class NoDialogs : INativeDialogs {
        public ValueTask<string?> OpenFileAsync(
            FileDialogOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<IReadOnlyList<string>> OpenFilesAsync(
            FileDialogOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<string?> SaveFileAsync(
            FileDialogOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<string?> OpenFolderAsync(
            FileDialogOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<MessageBoxResult> ShowMessageAsync(
            MessageBoxOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult(MessageBoxResult.None);
    }

    sealed class PickerSupplement : IPlatformSupplement {
        public INativeDialogs Dialogs { get; } = new NoDialogs();

        public string Name => "Pickers";

        public PlatformServices Augment(in PlatformServices baseline) =>
            baseline with {
                Dialogs = Dialogs,
                Capabilities = baseline.Capabilities | PlatformCapabilities.NativeDialogs
            };

        public void Dispose() { }
    }
}
