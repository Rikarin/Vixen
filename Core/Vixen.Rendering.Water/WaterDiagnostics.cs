// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vixen.Core;
using Vixen.Engine.Diagnostics.Overlays;

namespace Vixen.Rendering.Water;

/// <summary>Which of water's debug draws are switched on.</summary>
/// <remarks>
///     <para>
///         <b>[35 § Part 2 § Debugging](../../docs/plan/35-water.md#debugging), and it is copied on
///         purpose.</b> Unreal's <c>stat water</c>, <c>stat watermesh</c> and its colour-coded tile
///         bounds are better than most first-party debug tooling in any engine, and there is no
///         credit in inventing worse ones. Doc 35 puts them in the phase list as a deliverable
///         rather than as something added after the first bug.
///     </para>
///     <para>
///         <b>What reads them is <see cref="WaterDebugDraw" />, and it turned out no new seam was
///         needed.</b> These shipped as flags with the drawing owed, on the belief that a viewport
///         line pass was missing — <c>DebugDraw</c> is exactly that pass, and it is an accumulator
///         rather than a renderer, so this assembly draws into it without knowing what a line is.
///     </para>
///     <para>
///         ⚠ <b>Five of the six are drawn by <see cref="WaterDebugDraw" /> and the sixth is
///         elsewhere.</b> <see cref="ShowBuoyancy" />'s pontoons and forces belong to
///         <c>Vixen.Water.Physics</c>, which this assembly must not reference — § D1 puts the physics
///         join in its own assembly precisely so that nothing linking Jolt is linked by a renderer.
///         The flag is here because the console verb is; <c>BuoyancyDebugDraw</c> is where the data
///         is, and a host copies this across.
///     </para>
/// </remarks>
public static class WaterDebug {
    /// <summary>Patch bounds, coloured by body kind, with the LOD level as a label.</summary>
    public static bool ShowTiles { get; set; }

    /// <summary>The morph bands as rings, which is where a pop is diagnosed.</summary>
    public static bool ShowLod { get; set; }

    /// <summary>The info field's four channels as an overlay, individually.</summary>
    public static bool ShowInfo { get; set; }

    /// <summary>Flow vectors on the surface, and the spline velocity arrows.</summary>
    public static bool ShowFlow { get; set; }

    /// <summary>Pontoons, their submerged fraction, and the forces as arrows.</summary>
    public static bool ShowBuoyancy { get; set; }

    /// <summary>The ripple window's bounds and the injection count against the budget.</summary>
    public static bool ShowRipples { get; set; }

    /// <summary>Switches every one of them off.</summary>
    public static void Reset() {
        ShowTiles = false;
        ShowLod = false;
        ShowInfo = false;
        ShowFlow = false;
        ShowBuoyancy = false;
        ShowRipples = false;
    }

    /// <summary>Whether anything is being drawn at all — the single branch a renderer takes.</summary>
    public static bool Any => ShowTiles || ShowLod || ShowInfo || ShowFlow || ShowBuoyancy || ShowRipples;

    // --- The console verbs --------------------------------------------------

    /// <summary>`water.showTiles [0|1]`.</summary>
    /// <param name="context">The command's arguments and where to answer.</param>
    [ConsoleCommand("water.showTiles", Help = "Patch bounds, coloured by body kind, with the LOD level")]
    public static void Tiles(ConsoleContext context) =>
        ShowTiles = Toggle(context, "water.showTiles", ShowTiles);

    /// <summary>`water.showLod [0|1]`.</summary>
    /// <param name="context">The command's arguments and where to answer.</param>
    [ConsoleCommand("water.showLod", Help = "The morph bands as rings, which is where a pop is diagnosed")]
    public static void Lod(ConsoleContext context) => ShowLod = Toggle(context, "water.showLod", ShowLod);

    /// <summary>`water.showInfo [0|1]`.</summary>
    /// <param name="context">The command's arguments and where to answer.</param>
    [ConsoleCommand("water.showInfo", Help = "The info field's four channels, individually")]
    public static void Info(ConsoleContext context) => ShowInfo = Toggle(context, "water.showInfo", ShowInfo);

    /// <summary>`water.showFlow [0|1]`.</summary>
    /// <param name="context">The command's arguments and where to answer.</param>
    [ConsoleCommand("water.showFlow", Help = "Flow vectors on the surface and the spline velocity arrows")]
    public static void Flow(ConsoleContext context) => ShowFlow = Toggle(context, "water.showFlow", ShowFlow);

    /// <summary>`water.showBuoyancy [0|1]`.</summary>
    /// <param name="context">The command's arguments and where to answer.</param>
    [ConsoleCommand("water.showBuoyancy", Help = "Pontoons, their submerged fraction, and the forces")]
    public static void Buoyancy(ConsoleContext context) =>
        ShowBuoyancy = Toggle(context, "water.showBuoyancy", ShowBuoyancy);

    /// <summary>`water.showRipples [0|1]`.</summary>
    /// <param name="context">The command's arguments and where to answer.</param>
    [ConsoleCommand("water.showRipples", Help = "The ripple window's bounds and its injection budget")]
    public static void Ripples(ConsoleContext context) =>
        ShowRipples = Toggle(context, "water.showRipples", ShowRipples);

    /// <summary>Puts all six verbs into a registry, naming every one of them.</summary>
    /// <param name="into">Where they go.</param>
    /// <returns>How many were registered, which is always six.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="into" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Because <see cref="ConsoleCommands.RegisterFrom(Assembly)" /> has no callers, and
    ///         cannot honestly acquire one.</b> That overload scans every type in an assembly for the
    ///         attribute, which is annotated <see cref="RequiresUnreferencedCodeAttribute" /> and says
    ///         so — a shipping title calling it takes the warning, or trims the commands away and
    ///         finds out on device. Its own remarks name
    ///         <see cref="ConsoleCommands.Register(string, string, Action{ConsoleContext})" /> as what
    ///         a shipping title should use "until the generator that reads the attribute at compile
    ///         time exists"; this is that call, made six times, where the six names are.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>[ConsoleCommand]</c> attributes above stay, and are not now redundant.</b>
    ///         They are what the documentation graph reads to list the verbs, and they are what the
    ///         compile-time generator will read when it exists. What changed is that reaching them no
    ///         longer requires reflection.
    ///     </para>
    ///     <para>
    ///         <b>And a host no longer has to call this.</b> When these remarks were written nothing
    ///         in the tree constructed a <see cref="ConsoleCommands" />, a <c>DiagnosticOverlays</c>
    ///         or a <c>DebugDraw</c> outside its own tests, so registering the six made them typable
    ///         nowhere. Doc 13's host wiring exists now, and <see cref="WaterConsole" /> below hands
    ///         this method to <see cref="ConsoleCommands.Contribute" /> when the assembly is first
    ///         touched — so a game that renders water has <c>water.showFlow</c> without naming it.
    ///         The method stays public because a host with its own registry and no engine loop — a
    ///         tool, a test — still wants one call rather than six.
    ///     </para>
    /// </remarks>
    public static int Register(ConsoleCommands into) {
        ArgumentNullException.ThrowIfNull(into);

        into.Register("water.showTiles", "Patch bounds, coloured by body kind, with the LOD level", Tiles);
        into.Register("water.showLod", "The morph bands as rings, which is where a pop is diagnosed", Lod);
        into.Register("water.showInfo", "The info field's four channels, individually", Info);
        into.Register("water.showFlow", "Flow vectors on the surface and the spline velocity arrows", Flow);
        into.Register("water.showBuoyancy", "Pontoons, their submerged fraction, and the forces", Buoyancy);
        into.Register("water.showRipples", "The ripple window's bounds and its injection budget", Ripples);

        return 6;
    }

    /// <summary>The `cmd`, `cmd 1`, `cmd 0` triple every one of these accepts.</summary>
    /// <remarks>
    ///     ⚠ <b>Bare toggles rather than requiring an argument</b>, because that is what a person
    ///     typing at a console during a bug means and is what the reference does. An explicit value
    ///     still works, so a startup script can say what it wants rather than flipping whatever the
    ///     last session left.
    /// </remarks>
    static bool Toggle(ConsoleContext context, string name, bool current) {
        ArgumentNullException.ThrowIfNull(context);

        var wanted = context.Count == 0
            ? !current
            : context[0] is "1" or "true" or "on";

        context.Write($"{name} {(wanted ? "1" : "0")}");

        return wanted;
    }
}

/// <summary>Hands water's six verbs to every console, when the assembly is first touched.</summary>
/// <remarks>
///     <para>
///         <b>The line that turns "registered" into "typable" without a host knowing water exists.</b>
///         <c>AppGraphics</c> constructs a <c>WaterZoneSystem</c> in every frame it builds, which is
///         what touches this assembly; the initializer then contributes, and a console built before
///         or after gets the same six.
///     </para>
///     <para>
///         ⚠ <b>This is the shape the next subsystem should copy.</b> The reason water's were the
///         only <c>[ConsoleCommand]</c>s in the tree is that an attribute nobody can invoke teaches
///         people not to write any; four lines here are what make a verb reach a prompt.
///     </para>
/// </remarks>
static class WaterConsole {
    [ModuleInitializer]
    [SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification =
            "One delegate appended to a static list, which is the same trade QualityTierSerializer "
            + "makes: the alternative is every host remembering to register a subsystem's console "
            + "verbs, which is exactly the omission that left six verbs unreachable for a phase."
    )]
    internal static void Register() => ConsoleCommands.Contribute(commands => WaterDebug.Register(commands));
}

/// <summary>What the water stack did this frame, as numbers only it can know.</summary>
/// <remarks>
///     <see cref="FrameStatistics" />' arrangement and its reason: the host reads them off the
///     systems once a frame and the overlay formats them, so <c>Vixen.Engine</c> never learns what a
///     water zone is.
/// </remarks>
public readonly record struct WaterStatistics {
    /// <summary>How many zones the last fold saw.</summary>
    public int Zones { get; init; }

    /// <summary>How many bodies resolved to a curve.</summary>
    public int Bodies { get; init; }

    /// <summary>How many no zone's window reached.</summary>
    public int Zoneless { get; init; }

    /// <summary>How many named a spline nothing could supply.</summary>
    public int Unresolved { get; init; }

    /// <summary>How many <em>zones</em> named a <c>.vxwaves</c> that could not be used.</summary>
    /// <remarks>
    ///     ⚠ <b>A third diagnostic and not a fourth column on the second, because this one is still
    ///     drawing.</b> A zoneless or unresolved body is water that is not there; a zone whose sea
    ///     state did not load has water that looks entirely convincing and is the wrong sea — which on
    ///     a client is a boat that rides differently from the one on the server. It is the only
    ///     evidence of that.
    /// </remarks>
    public int UnresolvedWaves { get; init; }

    /// <summary>How many bodies the fold rebuilt.</summary>
    /// <remarks>
    ///     ⚠ <b>The reading that says the amortisation is real.</b> A number equal to
    ///     <see cref="Bodies" /> every frame is a fold rebuilding everything, which re-rasterises
    ///     every field — the cost the scroll threshold exists to avoid, paid in full and invisible in
    ///     a picture.
    /// </remarks>
    public int Rebuilt { get; init; }

    /// <summary>How many times any zone has re-rasterised its field.</summary>
    public int Rasterisations { get; init; }

    /// <summary>How many times any zone has staged its field for upload.</summary>
    public int Uploads { get; init; }
}

/// <summary>And what the surface mesh did.</summary>
public readonly record struct WaterMeshStatistics {
    /// <summary>How many zones drew.</summary>
    public int Zones { get; init; }

    /// <summary>How many patches they selected between them.</summary>
    public int Patches { get; init; }

    /// <summary>How many they had to drop, over the per-zone cap.</summary>
    /// <remarks>⚠ Non-zero is a hole in the water — see <see cref="WaterSurfacePass.MaxPatches" />.</remarks>
    public int Dropped { get; init; }

    /// <summary>How many vertices those patches are, which is what the draw actually costs.</summary>
    public int Vertices { get; init; }

    /// <summary>How many draws it took, as the passes counted them.</summary>
    /// <remarks>
    ///     Two per zone at most — the skirt and the window — and fewer whenever one of the two has no
    ///     instances, which is the ordinary case for a lake that fits inside its own window.
    /// </remarks>
    public int Draws { get; init; }
}

/// <summary>`stat water`: the fold's counts, and the two diagnostics that are not one number.</summary>
/// <remarks>
///     ⚠ <b>Two diagnostics rather than one, because the fixes differ.</b> A zoneless body is a
///     zone's extent; an unresolved one is an asset name or an asset that has not loaded. One number
///     for both sends an author to the wrong place, which is the whole reason the fold counts them
///     separately.
/// </remarks>
public sealed class WaterOverlay : IDiagnosticOverlay {
    const int Rows = 8;

    /// <inheritdoc />
    public string Name => "water";

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopRight;

    /// <inheritdoc />
    public bool Enabled { get; set; }

    /// <summary>How wide the panel is, in pixels.</summary>
    public float Width { get; set; } = 200f;

    /// <summary>What the systems reported. The host sets this once a frame.</summary>
    public WaterStatistics Statistics { get; set; }

    /// <summary>Reads this frame's numbers off a zone system, and the node that drew it.</summary>
    /// <param name="zones">The system.</param>
    /// <param name="mesh">
    ///     The surface node, for the upload count, or <see langword="null" /> when there is none.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="zones" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         A convenience so a host wires one line rather than seven, and so the mapping from a
    ///         counter to a row lives beside the row rather than in whichever host copied it last.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two sources rather than one, because the last row's number is not the fold's.</b>
    ///         Rasterising a field is <see cref="WaterZoneSystem" />'s and sending it to the device is
    ///         the node's, and the two differ by exactly what the amortisation saved — which is what
    ///         the row is for. Before the node was passed in, <c>uploads</c> was structurally
    ///         unreachable from here and drew a flat zero over a frame that was uploading: a panel
    ///         asserting an amortisation nobody had measured.
    ///     </para>
    /// </remarks>
    public void Read(WaterZoneSystem zones, WaterMeshRenderer? mesh = null) {
        ArgumentNullException.ThrowIfNull(zones);

        var rasterisations = 0;

        foreach (var state in zones.States.Values) {
            rasterisations += state.RasterCount;
        }

        Statistics = Statistics with {
            Zones = zones.ZoneCount,
            Bodies = zones.BodyCount,
            Zoneless = zones.ZonelessBodies,
            Unresolved = zones.UnresolvedBodies,
            UnresolvedWaves = zones.UnresolvedWaves,
            Rebuilt = zones.RebuiltBodies,
            Rasterisations = rasterisations,
            Uploads = mesh?.InfoUploads ?? 0
        };
    }

    /// <inheritdoc />
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        var region = surface.Panel(Anchor, Width, Rows, "WATER");

        if (region.IsEmpty) {
            return;
        }

        var theme = surface.Theme;
        var statistics = Statistics;

        WaterRows.Draw(in region, 0, "zones", statistics.Zones, theme.Text, theme.Text);
        WaterRows.Draw(in region, 1, "bodies", statistics.Bodies, theme.Text, theme.Text);

        // ⚠ Warned rather than merely printed. "I placed a lake and there is no water" is the
        // question these two answer, and a plain zero beside a red one is what makes an author read
        // the right row.
        WaterRows.Draw(in region, 2, "zoneless", statistics.Zoneless, theme.Text, statistics.Zoneless > 0 ? theme.Bad : theme.Muted);
        WaterRows.Draw(in region, 3, "unresolved", statistics.Unresolved, theme.Text, statistics.Unresolved > 0 ? theme.Bad : theme.Muted);

        // ⚠ Warning rather than bad, and the shade is the diagnosis. The two rows above it are water
        // that is not on screen; this one is water that is, drawn from a sea state nobody authored.
        WaterRows.Draw(in region, 4, "no waves", statistics.UnresolvedWaves, theme.Text, statistics.UnresolvedWaves > 0 ? theme.Warning : theme.Muted);

        WaterRows.Draw(in region, 5, "rebuilt", statistics.Rebuilt, theme.Text, statistics.Rebuilt > statistics.Bodies ? theme.Warning : theme.Muted);
        WaterRows.Draw(in region, 6, "rasters", statistics.Rasterisations, theme.Text, theme.Muted);
        WaterRows.Draw(in region, 7, "uploads", statistics.Uploads, theme.Text, theme.Muted);
    }
}

/// <summary>`stat watermesh`: nodes selected, dropped, vertices and draws.</summary>
public sealed class WaterMeshOverlay : IDiagnosticOverlay {
    const int Rows = 5;

    /// <inheritdoc />
    public string Name => "watermesh";

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopRight;

    /// <inheritdoc />
    public bool Enabled { get; set; }

    /// <summary>How wide the panel is, in pixels.</summary>
    public float Width { get; set; } = 200f;

    /// <summary>What the surface node reported. The host sets this once a frame.</summary>
    public WaterMeshStatistics Statistics { get; set; }

    /// <summary>Reads this frame's numbers off a surface node.</summary>
    /// <param name="mesh">The node.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Both derived numbers were wrong, and both were wrong by asserting a constant the
    ///     node already knew.</b> The lattice is <see cref="WaterMeshRenderer.GridQuads" /> quads
    ///     across — a document may say otherwise, and <c>Samples/13</c>'s does not — so a vertex
    ///     count computed from a hard-coded 32 is a plausible number for a different mesh. And the
    ///     draw count was two per zone, where a zone whose water is wholly inside its window records
    ///     one: see <see cref="WaterMeshRenderer.DrawsRecorded" />, which is what the pass counted
    ///     rather than what a caller assumed.
    /// </remarks>
    public void Read(WaterMeshRenderer mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var perPatch = (mesh.GridQuads + 1) * (mesh.GridQuads + 1);

        Statistics = new() {
            Zones = mesh.ZonesDrawn,
            Patches = mesh.PatchesDrawn,
            Dropped = mesh.DroppedPatches,
            Vertices = mesh.PatchesDrawn * perPatch,
            Draws = mesh.DrawsRecorded
        };
    }

    /// <inheritdoc />
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        var region = surface.Panel(Anchor, Width, Rows, "WATER MESH");

        if (region.IsEmpty) {
            return;
        }

        var theme = surface.Theme;
        var statistics = Statistics;

        WaterRows.Draw(in region, 0, "zones", statistics.Zones, theme.Text, theme.Text);
        WaterRows.Draw(in region, 1, "patches", statistics.Patches, theme.Text, theme.Text);

        // ⚠ Bad rather than muted, because a dropped patch is a hole in the water and a hole
        // attributed to the wrong thing is a week.
        WaterRows.Draw(in region, 2, "dropped", statistics.Dropped, theme.Text, statistics.Dropped > 0 ? theme.Bad : theme.Muted);

        WaterRows.Draw(in region, 3, "vertices", statistics.Vertices, theme.Text, theme.Muted);
        WaterRows.Draw(in region, 4, "draws", statistics.Draws, theme.Text, theme.Muted);
    }
}


/// <summary>One "label   value" row, right-aligned, allocating nothing.</summary>
/// <remarks>
///     ⚠ <b>A static rather than a local function, because a <c>stackalloc</c> cannot cross into
///     one.</b> A local function capturing a <c>Span&lt;char&gt;</c> is CS8175, and the alternative —
///     a field — would make two overlays drawn in one frame share a buffer.
/// </remarks>
static class WaterRows {
    /// <summary>Draws one.</summary>
    /// <param name="region">The panel.</param>
    /// <param name="row">Which row.</param>
    /// <param name="label">What it is called.</param>
    /// <param name="value">The number.</param>
    /// <param name="label_colour">What the label is drawn in.</param>
    /// <param name="colour">What the number is drawn in, which is how a bad one is flagged.</param>
    public static void Draw(
        in OverlayRegion region,
        int row,
        ReadOnlySpan<char> label,
        int value,
        Vixen.Core.Mathematics.Color4 label_colour,
        Vixen.Core.Mathematics.Color4 colour
    ) {
        Span<char> buffer = stackalloc char[32];

        region.Text(row, label, label_colour);

        if (buffer.TryWrite($"{value,7}", out var length)) {
            region.TextRight(row, buffer[..length], colour);
        }
    }
}
