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
        var device = GraphicsHost.Instance.Create(new GraphicsOptions(), window: null, logs: null, out var reason);

        Assert.NotNull(device);
        Assert.NotNull(reason);

        device.Dispose();
    }

    /// <summary>With no window, a presenting backend declines and the chain reaches Null.</summary>
    /// <remarks>
    ///     ⚠ <b>Vulkan creates perfectly happily with no surface, which is the trap.</b> Its
    ///     <c>TryCreate</c> asks for no surface extensions when there is nothing to present to and
    ///     returns a working headless device — so a selector that simply tried each backend in turn
    ///     would hand <c>docs/plan/17</c>'s dedicated server a real GPU device where it has always
    ///     had the Null one. Nothing about the server would look wrong until it was deployed to a
    ///     machine with no driver.
    ///     <para>
    ///         The old <c>GraphicsHost</c> got this right by short-circuiting on <c>window is
    ///         null</c> before it chose anything. The preference list has to reproduce that as a
    ///         refusal from the presenting backends themselves, or the short-circuit is lost.
    ///     </para>
    /// </remarks>
    [Fact]
    public void APresentingBackendDeclinesWhenThereIsNoWindow() {
        var options = new GraphicsOptions();

        options.Backends.Add(GraphicsBackend.Vulkan);
        options.Backends.Add(GraphicsBackend.Null);

        var device = GraphicsHost.Create(options, window: null, logs: null, out var reason);

        Assert.NotNull(device);
        Assert.IsType<NullDevice>(device);
        Assert.Contains("no window", reason, StringComparison.OrdinalIgnoreCase);

        device.Dispose();
    }

    /// <summary>And asking for only a presenting backend with no window refuses outright.</summary>
    [Fact]
    public void APresentingBackendAloneWithNoWindowOpensNothing() {
        var options = new GraphicsOptions();

        options.Backends.Add(GraphicsBackend.Vulkan);

        Assert.Null(GraphicsHost.Create(options, window: null, logs: null, out _));
    }

    /// <summary>An empty preference list is the order this package has always used.</summary>
    /// <remarks>
    ///     ⚠ <b>The compatibility assertion.</b> Every head that has never heard of this setting
    ///     goes through the same code as one that has, so "the default order" has to keep meaning
    ///     Vulkan-then-Null. Promoting WebGPU into it would silently move existing games onto a
    ///     different API.
    /// </remarks>
    [Fact]
    public void TheDefaultOrderIsVulkanThenNull() =>
        Assert.Equal([GraphicsBackend.Vulkan, GraphicsBackend.Null], GraphicsHost.Default);

    /// <summary>A list ending in Null always opens, whatever came before it refused.</summary>
    [Fact]
    public void AChainEndingInNullAlwaysOpens() {
        var options = new GraphicsOptions();

        options.Backends.Add(GraphicsBackend.OpenGl);
        options.Backends.Add(GraphicsBackend.Null);

        var device = GraphicsHost.Create(options, window: null, logs: null, out var reason);

        Assert.NotNull(device);

        // ⚠ The refusal that was survived is still reported. A fall-through that said only "the
        // Null backend draws nothing" would hide which candidates were tried and why each declined,
        // which is the whole question somebody reads this line to answer.
        Assert.Contains("opengl", reason, StringComparison.Ordinal);

        device.Dispose();
    }

    /// <summary>A list with nothing openable in it returns null rather than falling back.</summary>
    /// <remarks>
    ///     ⚠ <b>The behaviour the whole feature turns on.</b> An operator running
    ///     <c>--vixen-backend vulkan</c> is asking whether Vulkan works; handing back a device that
    ///     draws nothing would answer with exactly the silence the question was asked to break.
    /// </remarks>
    [Fact]
    public void AChainWithNoFallbackRefusesRatherThanDowngrading() {
        var options = new GraphicsOptions();

        options.Backends.Add(GraphicsBackend.OpenGl);

        var device = GraphicsHost.Create(options, window: null, logs: null, out var reason);

        Assert.Null(device);
        Assert.NotNull(reason);
        Assert.Contains("opengl", reason, StringComparison.Ordinal);
    }

    /// <summary>And the host turns that into a boot failure that names the way out.</summary>
    [Fact]
    public void ABuilderWhoseChainOpensNothingRefusesAndNamesTheWayOut() {
        var builder = VixenApp.Create(["--vixen-headless", "--vixen-backend", "opengl"]);

        var refusal = Assert.Throws<InvalidOperationException>(() => builder.Build(new Silent()));

        Assert.Contains(nameof(GraphicsBackend.Null), refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>OpenGL refuses for the real reason rather than being quietly absent.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted so the gap stays visible.</b> No <c>Vixen.Platform</c> implementation
    ///     creates a GL context, so the backend cannot be booted by an app head however it is asked
    ///     for. Leaving it out of the selector would make that indistinguishable from a backend that
    ///     was tried and failed; the day a platform grows the context call, this test is what says
    ///     the message needs revisiting.
    /// </remarks>
    [Fact]
    public void OpenGlRefusesBecauseNoPlatformMakesAContext() {
        var options = new GraphicsOptions();

        options.Backends.Add(GraphicsBackend.OpenGl);

        GraphicsHost.Create(options, window: null, logs: null, out var reason);

        Assert.Contains("context", reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>--vixen-backend</c> parses an ordered list, and replaces what the game asked for.</summary>
    [Fact]
    public void TheArgumentReplacesTheGamesOwnOrder() {
        var config = new AppConfig();

        config.Graphics.Backends.Add(GraphicsBackend.Vulkan);
        config.Apply(AppArguments.Parse(["--vixen-backend", "webgpu,null"]));

        Assert.Equal([GraphicsBackend.WebGpu, GraphicsBackend.Null], config.Graphics.Backends);
    }

    /// <summary>One unreadable name rejects the whole argument rather than half-applying it.</summary>
    /// <remarks>
    ///     ⚠ <b>Half a preference list is worse than none.</b> <c>vulkan,nul</c> would otherwise
    ///     become Vulkan-only, and the missing fallback would surface on the one machine that needed
    ///     it. Rejected and reported as unrecognised instead.
    /// </remarks>
    [Theory]
    [InlineData("vulkan,nul")]
    [InlineData("unknown")]
    [InlineData("")]
    public void AnUnreadableBackendNameRejectsTheWholeArgument(string value) {
        var parsed = AppArguments.Parse(["--vixen-backend", value]);

        Assert.Empty(parsed.Backends);
        Assert.Contains("--vixen-backend", parsed.Unrecognised);
    }

    sealed class CountingBackend : IGraphicsBackend {
        public int Opened { get; private set; }

        public IGraphicsDevice? Device { get; private set; }

        public IGraphicsDevice? Create(
            GraphicsOptions options,
            IWindow? window,
            ILoggerFactory? logs,
            out string? reason
        ) {
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
