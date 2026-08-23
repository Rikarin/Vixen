using Vixen.Core.Mathematics;
using Vixen.Ui.Desktop;

namespace VixenApp1;

/// <summary>The whole of the application that is not the interface.</summary>
/// <remarks>
///     <para>
///         <b>Start in <c>AppShell.vxml</c>.</b> This file says what the window is called and how big
///         it opens; everything else the application is lives in the markup, the stylesheet beside it
///         and whatever C# the <c>@code</c> block grows.
///     </para>
///     <para>
///         ⚠ <b>The <c>--frames N</c> flag is read for you.</b> A build that runs exactly N frames
///         and exits is what a CI job can assert starts, presents and stops without a hang, on a
///         machine that may have no GPU at all — everything above the RHI runs whether or not a
///         device was ever created.
///     </para>
///     <para>
///         ⚠ <b>No <c>Vixen.App</c> and no <c>Vixen.Engine</c>.</b> That host owns a frame loop built
///         around an ECS world and a fixed-step accumulator; an interface's loop redraws a document.
///         Add the engine the day this application has a scene in it, not before.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) =>
        UiApplication.Run(
            new UiApplicationOptions {
                Title = "VixenApp1",
                Organisation = "VixenApp1",
                Application = "VixenApp1",
                Size = new Int2(1280, 800),

                // ⚠ **The generated sheet, and there is no code behind that name.**
                // `Theme/vixen.ui.vcss` is the tokens; every `.vxml` and every `.cs` in this project
                // is scanned for class names at build time; the rules for the ones actually used are
                // compiled into `VixenUtilityStyles` before the compiler runs. Nothing here walks a
                // manifest or runs a scanner.
                //
                // ⚠ It is also the cheapest check that the wiring is there at all: a project whose
                // build step did not run compiles perfectly and produces an *empty* sheet, and every
                // class name in the markup then quietly does nothing.
                Styles = { VixenUtilityStyles.Css },

                // The root is the one element no markup owns, so its background is set here.
                // Everything else this application looks like is a class name in AppShell.vxml.
                RootClasses = { "p-0", "bg-slate-900" },

                Content = () => new AppShell()
            },
            arguments
        );
}
