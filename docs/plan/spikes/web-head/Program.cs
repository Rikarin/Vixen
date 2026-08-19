using Vixen.Graphics;
using Vixen.Graphics.WebGPU;
using Vixen.Graphics.WebGPU.Browser;
using Vixen.Platform;
using Vixen.Platform.Web;

// ModuleUrl is left at its default, which is the point of this probe: it was "./vixen-platform.js"
// and could never resolve, because JSHost.ImportAsync resolves against _framework/ and the file is
// published to the site root. MountContent is false because nothing in this repository produces the
// manifest.json that FetchFileProvider requires.
var platform = await WebPlatform.CreateAsync(new() { CanvasSelector = "#view", MountContent = false });

Console.WriteLine("VIXENPROBE platform=" + platform.GetType().Name);

var window = platform.CreateWindow(new() { Title = "Vixen", IsVisible = true });
Console.WriteLine("VIXENPROBE surfaceKind=" + window.Surface.Handle.Kind);
Console.WriteLine("VIXENPROBE processors=" + platform.Processors.AvailableProcessors);

// ── Graphics ────────────────────────────────────────────────────────────────────────────────
IGraphicsDevice? device = null;

try {
    if (!WebCanvas.TryGetSelector(window.Surface.Handle, out var canvasSelector)) {
        throw new InvalidOperationException("the window's surface is not a canvas");
    }

    Console.WriteLine("VIXENPROBE canvasSelector=" + canvasSelector);

    var binding = await BrowserWebGpuBinding.CreateAsync(
        new() { CanvasSelector = canvasSelector }
    );

    Console.WriteLine("VIXENPROBE adapter=" + binding.AdapterInfo.Name + " surface=" + binding.HasSurface
        + " format=" + binding.PreferredSurfaceFormat);

    device = new WebGpuDevice(binding);
    Console.WriteLine("VIXENPROBE device=" + device.GetType().Name);
} catch (Exception exception) {
    Console.WriteLine("VIXENPROBE gpu-failed " + exception.GetType().Name + ": " + exception.Message);
}

// ── The loop ────────────────────────────────────────────────────────────────────────────────
var frames = 0;
var loop = new WebFrameLoop();

loop.Start(timestamp => {
    frames++;

    if (frames is 10 or 150 or 400) {
        Console.WriteLine("VIXENPROBE frames=" + frames + " rate=" + loop.RefreshRate);
    }
});

Console.WriteLine("VIXENPROBE main-returned");
