// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Graphics.Null;
using Vixen.Input;
using Vixen.Platform;
using Vixen.Testing;
using Xunit;
using Xunit.Sdk;

namespace Vixen.App.Tests;

/// <summary>
///     The harness itself: the four parts <c>docs/plan/12</c> names, and the two ways it refuses to
///     hand back a green report on a frame that did not happen.
/// </summary>
/// <remarks>
///     ⚠ <b>Written because a harness is an instrument, and an instrument that has never been read on
///     the day the thing it drives did not run reports success.</b> Every claim
///     <see cref="TestApp" /> makes about determinism is a claim about what a fixture would otherwise
///     have believed — so each of these is written from the failing side: the app with no device, the
///     frames that were called and not simulated, the key that was set and never delivered.
/// </remarks>
public sealed class TestAppTests {
    /// <summary>The clock is the configuration's, and a frame is worth exactly one step.</summary>
    /// <remarks>
    ///     A property expressed as work rather than as elapsed time — twelve frames are twelve steps
    ///     of simulated time whatever the machine was doing, which is what makes two runs comparable
    ///     and is the whole argument for a fake clock.
    /// </remarks>
    [Fact]
    public void TwelveFramesAreTwelveStepsOfSimulatedTime() {
        using var app = TestApp.Create(new Silent());

        app.RunFrames(12);

        Assert.Equal(app.Step * 12, app.Time.Total);
        Assert.Equal(12, app.Time.FrameCount);
    }

    /// <summary>The device is the Null one, and it is recording.</summary>
    /// <remarks>
    ///     ⚠ Both halves matter and only one of them is obvious. A device that is not recording has
    ///     no command log, and every <c>Assert.Empty(log.OfKind(…))</c> written against it passes —
    ///     which is <c>RecordingBackend</c>'s first rule, one layer down from here.
    /// </remarks>
    [Fact]
    public void TheDeviceIsTheNullOneAndItIsRecording() {
        using var app = TestApp.Create(new Silent());

        Assert.IsType<NullDevice>(app.Services.Graphics!.Device);
        Assert.NotNull(app.Device.Recorder);
    }

    /// <summary>The file system is a dictionary, and nothing it holds is on a disk.</summary>
    [Fact]
    public void TheFileSystemIsADictionaryAndNamesNoDirectory() {
        using var app = TestApp.Create(new Silent());

        using (var stream = app.Services.FileSystem.OpenWrite(MountPoints.Data / "save.dat")) {
            stream.WriteByte(7);
        }

        Assert.Equal(1, app.Files.Data.FileCount);
        Assert.True(app.Files.Data.Exists(new("/save.dat")));

        // Empty rather than a synthetic path: there is no directory, and a lie here is one somebody
        // hands to System.IO and cannot explain the failure of. See TestFileSystem's remarks.
        Assert.Equal(string.Empty, app.Services.Platform.FileSystem.DataDirectory);
    }

    /// <summary>A pressed key arrives at the input the game reads.</summary>
    [Fact]
    public void APressedKeyReachesTheInputAGameReads() {
        using var app = TestApp.Create(new Silent());

        app.RunFrames(1);
        app.PressKey(Key.W);
        app.RunFrames(1);

        Assert.True(app.Services.Input.Devices.Keyboard.IsDown(InputKey.W));
        Assert.True(app.Input.IsKeyDown(Key.W));

        app.ReleaseKey(Key.W);
        app.RunFrames(1);

        Assert.False(app.Services.Input.Devices.Keyboard.IsDown(InputKey.W));
        Assert.False(app.Input.IsKeyDown(Key.W));
    }

    /// <summary>
    ///     ⚠ And the half of it that a fixture would otherwise get wrong: the polled state is not the
    ///     event stream.
    /// </summary>
    /// <remarks>
    ///     <see cref="HeadlessInputSource.SetKey" /> is the obvious way to "press a key" and it posts
    ///     nothing, while <c>Services.Input</c> is fed by <c>InputDeviceSet.Submit</c> from the events
    ///     <c>PumpEvents</c> drains. A test that pressed a key that way and then asserted an action
    ///     had fired would fail; one that asserted an action had <em>not</em> fired would pass for the
    ///     wrong reason, for ever. This is why <see cref="TestApp.PressKey" /> does both.
    /// </remarks>
    [Fact]
    public void SettingTheKeyWithoutTheEventReachesNothing() {
        using var app = TestApp.Create(new Silent());

        app.Input.SetKey(Key.W, true);
        app.RunFrames(1);

        Assert.True(app.Input.IsKeyDown(Key.W));
        Assert.False(app.Services.Input.Devices.Keyboard.IsDown(InputKey.W));
    }

    /// <summary>
    ///     An application with no device is refused, rather than handed back to assert nothing over.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The failure this replaces is silent all the way through.</b> A game that turned
    ///     graphics off still builds, initialises, pumps events and runs frames — so a fixture gets a
    ///     healthy frame count, a null <c>Services.Graphics</c> and every command-log assertion it
    ///     makes over the device it thinks it has is an assertion over nothing.
    /// </remarks>
    [Fact]
    public void AnApplicationWithNoDeviceIsRefusedByName() {
        var refusal = Assert.Throws<XunitException>(() => TestApp.Create(new NoGraphics()));

        Assert.Contains("Graphics.Enabled", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("VixenApp.Create", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Frames that were called and not simulated are a failure, not a count.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The form this replaces is <c>for (var i = 0; i &lt; 5; i++) app.RunFrame();</c>, which
    ///     is what eleven fixtures in this project write.</b> <c>RunFrame</c> returns normally on a
    ///     stopping application — it pumps events, drains posted work and returns before
    ///     <c>Advance</c> — so the loop completes, the clock does not move, nothing draws, and every
    ///     assertion about the frame is about a frame that never ran. <c>--vixen-frames 1</c> is the
    ///     cheapest way to produce that state; a game calling <c>Stop</c> and a closed window produce
    ///     it identically.
    /// </remarks>
    [Fact]
    public void FramesThatDidNotRunAreAFailureRatherThanACount() {
        using var app = TestApp.Create(new Silent(), "--vixen-frames", "1");

        var refusal = Assert.Throws<XunitException>(() => app.RunFrames(5));

        Assert.Contains("5 frame(s) were asked for and 1 were simulated", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("--vixen-frames 1", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>And the same application runs the one frame it does have without complaint.</summary>
    /// <remarks>
    ///     The other half of the pair: a refusal that cannot pass is worth as little as an assertion
    ///     that cannot fail.
    /// </remarks>
    [Fact]
    public void TheFrameThatDoesRunIsNotRefused() {
        using var app = TestApp.Create(new Silent(), "--vixen-frames", "1");

        app.RunFrames(1);

        Assert.Equal(1, app.Time.FrameCount);
    }

    /// <summary>A later argument wins, which is what makes the defaults defaults.</summary>
    [Fact]
    public void ACallersArgumentOverridesTheHarnessOwn() {
        using var app = TestApp.Create(new Silent(), "--vixen-fixed-step", "0.02");

        app.RunFrames(3);

        Assert.Equal(TimeSpan.FromSeconds(0.02), app.Step);
        Assert.Equal(TimeSpan.FromSeconds(0.06), app.Time.Total);
    }

    class Silent : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Window = null;
    }

    sealed class NoGraphics : Silent {
        protected internal override void OnConfigure(AppConfig config) {
            base.OnConfigure(config);
            config.Graphics.Enabled = false;
        }
    }
}
