// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>Where the platform and the device come from, once the host stopped choosing them.</summary>
/// <remarks>
///     ⚠ <b>The seam exists because of a build rule, so a test that only checked the happy path would
///     miss the whole point.</b> <c>Vixen.App.Hosting</c> is in <c>Core/</c> and
///     <c>CheckArchitecture</c> forbids it referencing <c>Platform/</c>, where Vulkan, Null, the
///     desktop platform and the headless platform all are. So a builder that nobody installed
///     backends into has neither — and what it does about that is the behaviour worth pinning down,
///     because the tempting answer (fall back to headless) produces a game that boots, runs, and
///     shows nothing.
/// </remarks>
public sealed class AppBackendTests {
    /// <summary>The entry point installs both, which is what makes the one-line form work.</summary>
    [Fact]
    public void CreateInstallsTheBackendsThisPackageShips() {
        using var application = VixenApp.Create(["--vixen-headless"]).Build(new Silent());

        Assert.NotNull(application.Services.Platform);
        Assert.Equal("Headless", application.Services.Platform.Name);
    }

    /// <summary>
    ///     A builder assembled by hand, with nothing installed, says so rather than booting blind.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The message names the two ways out</b>, because the audience for it is somebody
    ///     writing an app head for a platform this package does not ship — Android, iOS, Web — and
    ///     the failure arrives before there is a window to print anything into.
    /// </remarks>
    [Fact]
    public void ABuilderWithNoPlatformRefusesAndSaysWhat() {
        var builder = new AppBuilder(AppArguments.Parse(["--vixen-headless"]));

        var refusal = Assert.Throws<InvalidOperationException>(() => builder.Build(new Silent()));

        Assert.Contains("IPlatformFactory", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AppBuilder.WithPlatformFactory), refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The same, one step later: a platform but no way to open a device.</summary>
    /// <remarks>
    ///     ⚠ <b>Not folded into the platform's refusal.</b> The two are independently supplied — an
    ///     editor's play mode hands over a live device and lets the factory pick the platform — so a
    ///     single check would refuse a combination that is legitimate.
    /// </remarks>
    [Fact]
    public void ABuilderWithNoGraphicsBackendRefusesAndSaysWhat() {
        using var platform = new HeadlessPlatform(new() { Organisation = "Vixen", Application = "Test" });

        var builder = new AppBuilder(AppArguments.Parse(["--vixen-headless"])).WithPlatform(platform);

        var refusal = Assert.Throws<InvalidOperationException>(() => builder.Build(new Silent()));

        Assert.Contains("IGraphicsBackend", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AppBuilder.WithGraphicsBackend), refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A head that wants no device at all is not refused — it asked for none.</summary>
    [Fact]
    public void ABuilderThatAskedForNoGraphicsNeedsNoBackend() {
        using var platform = new HeadlessPlatform(new() { Organisation = "Vixen", Application = "Test" });

        using var application = new AppBuilder(AppArguments.Parse(["--vixen-headless"]))
            .WithPlatform(platform)
            .Build(new Headless());

        Assert.Null(application.Services.Graphics);
    }

    /// <summary>A backend the caller installed is the one that is asked.</summary>
    [Fact]
    public void AnInstalledBackendIsWhatOpensTheDevice() {
        var backend = new CountingBackend();

        using var application = new AppBuilder(AppArguments.Parse(["--vixen-headless"]))
            .WithPlatformFactory(PlatformHost.Instance)
            .WithGraphicsBackend(backend)
            .Build(new Silent());

        Assert.Equal(1, backend.Opened);
        Assert.Same(backend.Device, application.Services.Graphics?.Device);
    }

    /// <summary>
    ///     A device handed over outright wins, and the backend is never asked.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Sharper than the platform's precedence.</b> A device handed in belongs to somebody
    ///     else's frame — the editor's play mode, an XR runtime — and opening a second would leave
    ///     two devices addressing one GPU with neither aware of the other's submissions.
    /// </remarks>
    [Fact]
    public void AHandedOverDeviceBeatsTheBackend() {
        var backend = new CountingBackend();
        using var device = new NullDevice();

        using var application = new AppBuilder(AppArguments.Parse(["--vixen-headless"]))
            .WithPlatformFactory(PlatformHost.Instance)
            .WithGraphicsBackend(backend)
            .WithGraphics(device)
            .Build(new Silent());

        Assert.Equal(0, backend.Opened);
        Assert.Same(device, application.Services.Graphics?.Device);
    }

    /// <summary>The shipped backend answers headless with a device that records and draws nothing.</summary>
    /// <remarks>
    ///     Doc 17 makes that a shipping backend rather than a failure, so it is asserted as a
    ///     supported answer and not as a fallback: it is what the dedicated server runs on.
    /// </remarks>
    [Fact]
    public void TheShippedBackendAnswersNoWindowWithADeviceAndAReason() {
        var device = GraphicsHost.Instance.Create(window: null, logs: null, out var reason);

        Assert.NotNull(device);
        Assert.NotNull(reason);

        device.Dispose();
    }

    sealed class CountingBackend : IGraphicsBackend {
        public int Opened { get; private set; }

        public IGraphicsDevice? Device { get; private set; }

        public IGraphicsDevice Create(IWindow? window, ILoggerFactory? logs, out string? reason) {
            Opened++;
            reason = "the test asked for one that draws nothing.";

            return Device = new NullDevice(new() { Record = true });
        }
    }

    /// <summary>A game that configures nothing beyond staying out of the way.</summary>
    sealed class Silent : Game {
        protected internal override void OnConfigure(AppConfig config) {
            config.Name = "Backend test";
            config.Window = null;
            config.UseEngine = false;
        }
    }

    /// <summary>The same, with no device asked for at all.</summary>
    sealed class Headless : Game {
        protected internal override void OnConfigure(AppConfig config) {
            config.Name = "Backend test";
            config.Window = null;
            config.UseEngine = false;
            config.Graphics.Enabled = false;
        }
    }
}
