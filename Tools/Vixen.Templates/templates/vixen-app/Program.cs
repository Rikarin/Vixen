using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Desktop;

namespace VixenApp1;

/// <summary>The platform and the window. Everything else is <see cref="AppHost" />.</summary>
static class Program {
    static int Main(string[] arguments) {
        var frames = Frames(arguments);

        // ⚠ The GPU surface has to be asked for when the window is made. SDL needs the Vulkan
        // window flag at creation time, and a window made without it has nothing to present to.
        using var platform = new DesktopPlatform(
            new() { Organisation = "VixenApp1", Application = "VixenApp1", RequestGpuSurface = true }
        );

        using var window = platform.CreateWindow(
            new WindowOptions {
                Title = "VixenApp1",
                Size = new Int2(1280, 800),
                IsVisible = true,
                IsResizable = true
            }
        );

        using var host = new AppHost(platform, window);

        return host.Run(frames);
    }

    /// <summary>Reads <c>--frames N</c>, or zero for "until the window is closed".</summary>
    /// <remarks>
    ///     Worth keeping: a build that runs exactly N frames and exits is what a CI job can assert
    ///     starts, presents and stops without a hang, on a machine that may have no GPU at all.
    /// </remarks>
    static int Frames(ReadOnlySpan<string> arguments) {
        for (var i = 0; i + 1 < arguments.Length; i++) {
            if (arguments[i] is "--frames" && int.TryParse(
                    arguments[i + 1],
                    CultureInfo.InvariantCulture,
                    out var count
                )) {
                return Math.Max(0, count);
            }
        }

        return 0;
    }
}
