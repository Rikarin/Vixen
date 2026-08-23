// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Composition;

namespace Vixen.Ui.Desktop;

/// <summary>Everything an application says about itself before it has a window.</summary>
/// <remarks>
///     <para>
///         <b>Defaults that are answers rather than zeroes.</b> Every property here has a value an
///         application could ship with, so the shortest useful <c>Main</c> sets two of them —
///         <see cref="Title" /> and <see cref="Content" /> — and gets a titled, resizable, correctly
///         scaled window with the control theme installed, the utility classes loaded, a face found
///         and eight shader stages wired.
///     </para>
///     <para>
///         ⚠ <b><see cref="Content" /> is the one with no sensible default and it is deliberately not
///         nullable-with-a-fallback.</b> An application that forgets it would otherwise open an empty
///         window and look like a renderer bug.
///     </para>
/// </remarks>
public sealed class UiApplicationOptions {
    /// <summary>What the window is called.</summary>
    public string Title { get; set; } = "Vixen";

    /// <summary>The window's size in device-independent pixels.</summary>
    /// <remarks>
    ///     Points rather than framebuffer pixels: this is 1280×800 of desk, and on a display whose
    ///     backing scale is two the swapchain is built at 2560×1600 without the application saying so.
    /// </remarks>
    public Int2 Size { get; set; } = new(1280, 800);

    /// <summary>Whether the user may resize it.</summary>
    public bool IsResizable { get; set; } = true;

    /// <summary>Who the application belongs to, which is half of where its settings are kept.</summary>
    /// <remarks>
    ///     ⚠ Used for <c>IPlatform.FileSystem.DataDirectory</c> — <c>~/Library/Application Support/
    ///     &lt;organisation&gt;/&lt;application&gt;</c> and its equivalents. Left at the defaults, two
    ///     different applications share a directory, which is a bug that surfaces as one overwriting
    ///     the other's window placement.
    /// </remarks>
    public string Organisation { get; set; } = "Vixen";

    /// <summary>What the application is called, which is the other half.</summary>
    public string Application { get; set; } = "Vixen";

    /// <summary>Builds the interface.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A factory rather than an instance, because it is called after the document
    ///         exists.</b> A component built before there is a document has nowhere to mount, and one
    ///         built before the stylesheets are loaded resolves every class name against an empty
    ///         cascade.
    ///     </para>
    ///     <para>
    ///         In practice this returns the component a <c>.vxml</c> compiled to —
    ///         <c>() =&gt; new Shell()</c> — which is the whole of what an application writes here.
    ///     </para>
    /// </remarks>
    public Func<Component>? Content { get; set; }

    /// <summary>Stylesheets to load, in order, as author sheets.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is where an assembly's generated utility sheet goes, and it cannot be found
    ///         without being named.</b> The build step compiles the class names it scanned out of
    ///         <c>.vxml</c> and <c>.cs</c> into a <c>VixenUtilityStyles</c> in the <i>application's</i>
    ///         assembly, so nothing here can reach it: <c>options.Styles.Add(VixenUtilityStyles.Css)</c>
    ///         is one line and no host could have written it.
    ///     </para>
    ///     <para>
    ///         ⚠ It is also the cheapest check that the wiring is there at all. A project whose build
    ///         step did not run compiles perfectly and produces an empty sheet, and every class name
    ///         in the markup then quietly does nothing — see <c>VixenUtilityStyles.RuleCount</c>.
    ///     </para>
    /// </remarks>
    public IList<string> Styles { get; } = [];

    /// <summary>Whether the control set's own theme is installed under everything else.</summary>
    /// <remarks>
    ///     ⚠ <b>A user-agent sheet, which is what makes the ordering work.</b> Everything in
    ///     <see cref="Styles" /> out-specifies it simply by being an author sheet — a plain
    ///     <c>root { … }</c> beats a user-agent rule because of where it came from, not because of
    ///     what it selects. Turned off, a <c>&lt;Button&gt;</c> is an unstyled box.
    /// </remarks>
    public bool InstallControlTheme { get; set; } = true;

    /// <summary>Classes to put on the document's root.</summary>
    /// <remarks>
    ///     The root is the one element no markup owns, so an application that wants its window's
    ///     ground colour to come out of the same palette as everything else says so here:
    ///     <c>["p-0", "bg-slate-900"]</c>.
    /// </remarks>
    public IList<string> RootClasses { get; } = [];

    /// <summary>What the window is cleared to.</summary>
    /// <remarks>
    ///     ⚠ <b>One value used twice, and the two uses have to agree.</b> The interface's pass clears
    ///     to it and <c>UiRenderer.Compose</c> is told the same colour, so that a glass panel's
    ///     captured backdrop begins from the ground the frame is actually drawn on. Two literals
    ///     drift into a rectangle of the wrong shade under every such panel. Alpha one, which is what
    ///     makes the clear and the capture the same picture — see <c>UiBackdropSource</c>.
    ///     <para>
    ///         It is not read from the root's <c>background-color</c>, and that is the honest way
    ///         round: a backdrop is captured before the cascade has necessarily settled, and a clear
    ///         colour that changed with a class would change what every <c>backdrop-filter</c> in the
    ///         frame started from.
    ///     </para>
    /// </remarks>
    public Color4 Ground { get; set; } = new(0.06f, 0.07f, 0.09f, 1f);

    /// <summary>Whether a face is borrowed from the operating system when none is registered.</summary>
    /// <remarks>
    ///     ⚠ An application that ships its own font sets this false and registers it in
    ///     <see cref="Configure" />; leaving it on costs one failed <c>File.Exists</c> per candidate
    ///     and is what stops a first run from drawing every label at zero width. See
    ///     <see cref="SystemFonts" /> for why "whatever Arial the machine has" is a starting point
    ///     rather than a design.
    /// </remarks>
    public bool InstallSystemFont { get; set; } = true;

    /// <summary>Run once against the document, after the sheets and before the content is mounted.</summary>
    /// <remarks>
    ///     The seam for everything this options object does not have a property for: a second theme
    ///     assembly's <c>Install</c>, a font asset, a <c>TypeRegistry</c> registration an inspector
    ///     needs before it inspects.
    /// </remarks>
    public Action<UiDocument>? Configure { get; set; }

    /// <summary>Run once, after the interface is built and before the first frame is pumped.</summary>
    /// <remarks>
    ///     ⚠ <b>Here rather than only as an event on <c>UiApplication</c>, because the shortest way
    ///     to run one is the static <c>UiApplication.Run(options)</c> — which constructs the
    ///     application itself and hands a caller no object to subscribe to.</b> An options object
    ///     that could not carry a start hook would force every application wanting one to open the
    ///     window by hand, which is the thing this type exists to stop.
    ///     <para>
    ///         What goes here is anything that needs the document and the window to both exist: a
    ///         <c>HotReloadWatcher</c> over the project's <c>.vcss</c>, a window title bound to a
    ///         model, a first-run dialog.
    ///     </para>
    /// </remarks>
    public Action<UiApplication>? Started { get; set; }

    /// <summary>Run once a frame, after the events are pumped and before the document is updated.</summary>
    /// <remarks>
    ///     ⚠ Most applications need nothing here: a control that animates reads the clock through
    ///     <c>UiDocument.Ticked</c>, which is what makes a panel mountable in a test that has no
    ///     frame loop. What this is for is polling something that is not the document — a watcher, a
    ///     job queue, a socket.
    /// </remarks>
    public Action<UiApplication, UiFrame>? Frame { get; set; }

    /// <summary>Run once, after the loop stops and while the document is still alive.</summary>
    /// <remarks>
    ///     ⚠ Before the document goes, which is the reason it is not a <c>Dispose</c>. Anything that
    ///     persists state reads it out of the tree — a docking arrangement, a window placement, a
    ///     form's contents — and a disposed document has none.
    /// </remarks>
    public Action<UiApplication>? Stopping { get; set; }

    /// <summary>How many frames to run before exiting, or zero for "until the window is closed".</summary>
    /// <remarks>
    ///     ⚠ <b>Worth wiring to a command-line flag in every application, and
    ///     <c>UiApplication.Run(options, arguments)</c> does it for you.</b> A
    ///     build that runs exactly N frames and exits is what a CI job can assert starts, presents
    ///     and stops without a validation error or a hang — on a machine that may have no GPU at all,
    ///     because everything above the RHI runs whether or not a device was ever created.
    /// </remarks>
    public int Frames { get; set; }
}
