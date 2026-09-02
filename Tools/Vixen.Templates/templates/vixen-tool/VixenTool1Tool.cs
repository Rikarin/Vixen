using Vixen.App;

namespace VixenTool1;

/// <summary>
///     A batch step that runs on the engine's own host, with no window and no device.
/// </summary>
/// <remarks>
///     docs/plan/17 § Q5d: this is the head for content validation, CI captures, batch conversion
///     and custom pipeline steps — the same boot path a game takes, minus everything that needs a
///     person in front of it. What it does out of the box is open the content built beside the
///     binary and check that every address in the catalog can actually be read; replace
///     <c>OnInitialise</c> with the step you want.
/// </remarks>
public sealed class VixenTool1Tool : Game {
    /// <summary>What the process should exit with. Non-zero means the step found something.</summary>
    public int ExitCode { get; private set; }

    protected override void OnConfigure(AppConfig config) {
        config.Name = "VixenTool1";

        // No window at all, rather than an invisible one. A hidden window still asks the platform
        // for a surface a swapchain could be built on, which is a thing a build agent with no
        // display cannot give — and the failure would be at start-up rather than at the step.
        config.Headless = true;
        config.Window = null;

        // No world. An ECS, a scene manager and a fixed-step accumulator are what a game needs to
        // be a game; a step that reads a catalog pays for all three and uses none.
        config.UseEngine = false;

        // A device only if somebody asked for a picture. `--vixen-capture <path>` is what a CI
        // screenshot job passes, and it is also the only reason this head has to open a GPU at all
        // — so the flag decides, and a validation run on a machine with no Vulkan is unaffected.
        config.Graphics.Enabled = config.Graphics.CapturePath is { Length: > 0 };

        // ⚠ What ends the process, and it has to be a default rather than an assignment.
        // ExitWhenAllWindowsClose cannot end this run: it is skipped when there is no window, so
        // that a headless run is not over before it starts. And OnConfigure is called *after* the
        // command line has been applied — see AppConfig.Apply — so writing `config.MaxFrames = 1`
        // here would throw away a `--vixen-frames 120` somebody typed.
        if (config.MaxFrames <= 0) {
            config.MaxFrames = 1;
        }
    }

    protected override void OnInitialise() {
        // The content built beside this binary — Content/ in the output directory, which is where
        // Vixen.Sdk puts what `vixen content build` produced for the project this tool inspects.
        var content = Services.Content;

        if (content.Assets is not { } assets) {
            Console.Error.WriteLine($"There is no content to check: {content.Reason}");
            ExitCode = 1;

            return;
        }

        var unreadable = 0;

        foreach (var entry in assets.Catalog.Entries) {
            if (assets.CanOpen(entry.Address)) {
                continue;
            }

            // An address the catalog names and the bundles do not hold. At run time this is a
            // Load<T> that throws in front of a player; here it is a line in a build log.
            Console.Error.WriteLine($"{entry.Address} is in the catalog and cannot be opened.");
            unreadable++;
        }

        Console.WriteLine($"{assets.Catalog.Count} addresses in {assets.Catalog.Target} content, {unreadable} unreadable.");

        ExitCode = unreadable == 0 ? 0 : 1;
    }
}
