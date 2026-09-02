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

    /// <summary>
    ///     ⚠ <b>A run that asked to render offscreen is refused by the Null device rather than
    ///     served by it — even when the caller named Null itself.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the whole of issue #126's ⚠: a new way to ask for a real device must not
    ///         become a fourth quiet route to the one that draws nothing. The failure it prevents has
    ///         no symptom — <c>NullDevice</c> exits 0, writes a black PNG, and prints CPU counters
    ///         character for character identical to a healthy run, so an A/B between a branch that
    ///         fixed a renderer and one that broke it comes out equal.
    ///     </para>
    ///     <para>
    ///         Null is named explicitly here on purpose, because that is the case an escape hatch
    ///         would have allowed and the one worth pinning: there is no ordering of a preference
    ///         list that makes the device drawing nothing an answer to "render offscreen". The way
    ///         to ask for the fall-through is still there and unchanged — it is
    ///         <c>--vixen-backend vulkan,null</c> with neither of these two settings.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnOffscreenRunIsRefusedByTheDeviceThatDrawsNothing() {
        var options = new GraphicsOptions { Offscreen = true };

        options.Backends.Add(GraphicsBackend.Null);

        var device = GraphicsHost.Create(options, window: null, logs: null, out var reason);

        Assert.Null(device);
        Assert.Contains("draws nothing", reason!, StringComparison.Ordinal);
        Assert.Contains("--vixen-offscreen", reason!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>And it is what lifts Vulkan's no-surface refusal, which until now only a capture
    ///     path could.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The assertion is about the <em>refusal</em> rather than about a device, deliberately.
    ///         Whether Vulkan opens is a fact about the machine — this suite runs on CI boxes with no
    ///         driver — but whether the chain even asked it to is a fact about this code, and it is
    ///         the one that was wrong: without the flag the answer is "the application asked for no
    ///         window", decided before any loader is touched.
    ///     </para>
    ///     <para>
    ///         So the pair is the test. The same options with the flag off must give that sentence,
    ///         and with it on must not — leaving either a device or a refusal Vulkan itself wrote.
    ///     </para>
    /// </remarks>
    [Fact]
    public void OffscreenIsWhatLetsVulkanBeAskedWithNoSurfaceAtAll() {
        var refusing = new GraphicsOptions();

        refusing.Backends.Add(GraphicsBackend.Vulkan);

        Assert.Null(GraphicsHost.Create(refusing, window: null, logs: null, out var declined));
        Assert.Contains("asked for no window", declined!, StringComparison.Ordinal);

        var asking = new GraphicsOptions { Offscreen = true };

        asking.Backends.Add(GraphicsBackend.Vulkan);

        using var device = GraphicsHost.Create(asking, window: null, logs: null, out var reason);

        // One or the other, and never the sentence above: either this machine has Vulkan and the
        // device is real, or it does not and the refusal is the loader's rather than the chain's.
        if (device is null) {
            Assert.DoesNotContain("asked for no window", reason!, StringComparison.Ordinal);
        } else {
            Assert.IsNotType<NullDevice>(device);
            Assert.Contains("drawing offscreen", reason!, StringComparison.Ordinal);
        }
    }

    /// <summary>And a capture is the same request, so it is refused by the same device.</summary>
    /// <remarks>
    ///     ⚠ <b>This half was broken before <c>Offscreen</c> existed and nobody had said so.</b>
    ///     <c>--vixen-capture</c> on a machine with no working Vulkan fell through the default
    ///     order to Null and wrote a black PNG with a startup log that read as success — the exact
    ///     defect the flag lifting the refusal was added to avoid, one backend further down the
    ///     chain. <c>build/Build.SampleFrame.cs</c> caught it after the fact by inspecting the
    ///     adapter name in the run's log; it now cannot happen.
    /// </remarks>
    [Fact]
    public void ACaptureRunIsRefusedByItToo() {
        var options = new GraphicsOptions { CapturePath = "shots" };

        options.Backends.Add(GraphicsBackend.Null);

        Assert.Null(GraphicsHost.Create(options, window: null, logs: null, out var reason));
        Assert.Contains("draws nothing", reason!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And with neither, the fall-through is exactly what it was: Null opens, and says why
    ///     nothing will appear.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The other half of the claim, and the one that keeps a dedicated server working.</b>
    ///     Doc 17 runs a server on this device on purpose; a refusal that fired for every windowless
    ///     run rather than for the two that state an intent would have taken the server's backend
    ///     away, which is a far worse bug than the one being fixed.
    /// </remarks>
    [Fact]
    public void AWindowlessRunThatAskedForNeitherStillGetsTheDeviceThatDrawsNothing() {
        var options = new GraphicsOptions();

        options.Backends.Add(GraphicsBackend.Null);

        using var device = GraphicsHost.Create(options, window: null, logs: null, out var reason);

        Assert.IsType<NullDevice>(device);
        Assert.Contains("draws nothing by design", reason!, StringComparison.Ordinal);
    }

    /// <summary>OpenGL needs a window, and says so rather than being quietly absent.</summary>
    /// <remarks>
    ///     ⚠ <b>A different reason from Vulkan's, for a different cause.</b> Vulkan and WebGPU want a
    ///     <i>presentable surface</i> to build a swapchain on; OpenGL has no swapchain at all and
    ///     draws into the window's own default framebuffer, so what it wants is a window. Collapsing
    ///     the two into one test would let the wrong check pass for the wrong backend.
    ///     <para>
    ///         Whether a window that exists can actually produce a context is the platform's answer
    ///         and is covered by <c>DesktopGlContextTests</c>, which needs a real SDL video driver
    ///         and skips where there is none.
    ///     </para>
    /// </remarks>
    [Fact]
    public void OpenGlDeclinesWithoutAWindow() {
        var options = new GraphicsOptions();

        options.Backends.Add(GraphicsBackend.OpenGl);

        Assert.Null(GraphicsHost.Create(options, window: null, logs: null, out var reason));
        Assert.Contains("window", reason!, StringComparison.OrdinalIgnoreCase);
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
