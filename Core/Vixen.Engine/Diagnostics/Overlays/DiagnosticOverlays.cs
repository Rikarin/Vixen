// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Diagnostics.Overlays;

/// <summary>One toggleable diagnostic panel.</summary>
/// <remarks>
///     <para>
///         An interface rather than a closed set, because the interesting overlays belong to the
///         subsystems they report on: physics knows what a contact is and the asset system knows what
///         is resident, and neither of those facts belongs in this assembly. What lives here is the
///         frame, and the four whose data is already in <c>Vixen.Core.Diagnostics</c>.
///     </para>
///     <para>
///         ⚠ <b>An overlay draws and reads; it does not simulate.</b> It is called after everything
///         that produces its numbers has run, from a system in <c>PreRender</c>, so a value it reads
///         is this frame's and anything it writes is geometry.
///     </para>
/// </remarks>
public interface IDiagnosticOverlay {
    /// <summary>What the console and the toggles call it. Lower case, no spaces.</summary>
    string Name { get; }

    /// <summary>Which corner it is pinned to.</summary>
    OverlayAnchor Anchor { get; }

    /// <summary>Whether it is drawn.</summary>
    bool Enabled { get; set; }

    /// <summary>Draws it.</summary>
    /// <param name="surface">Where to draw.</param>
    /// <param name="time">The frame's clock.</param>
    void Draw(OverlaySurface surface, in GameTime time);
}

/// <summary>
///     The set of diagnostic overlays, what is switched on, and the one call a frame that draws them.
/// </summary>
/// <remarks>
///     <para>
///         [13](../../../../docs/plan/13-diagnostics.md) § Diagnostic overlays asks for these to be
///         toggleable and present <b>in every build</b>, which is the whole reason they are drawn out
///         of <see cref="DebugDraw" /> rather than out of the editor: a build with no editor attached
///         and no interface running still has a line pipeline, and that is the build where the
///         numbers are most wanted.
///     </para>
///     <para>
///         Nothing here polls or samples. An overlay reads what its subsystem already published —
///         <c>Profiler</c>'s rings, <c>RingBufferSink</c>'s records, a statistics value the host set —
///         so turning one on costs its own drawing and nothing else, and turning all of them off
///         costs one branch each.
///     </para>
/// </remarks>
public sealed class DiagnosticOverlays {
    /// <summary>How wide the notice panel is, in pixels.</summary>
    /// <remarks>
    ///     Narrower than <c>FrameStatsOverlay.Width</c>, because a notice is a short phrase and not a
    ///     column of numbers, and a panel sized for the numbers would put a stripe of empty
    ///     background above the frame statistics on every loose-content build.
    /// </remarks>
    const float NoticeWidth = 150f;

    readonly List<IDiagnosticOverlay> overlays = [];
    readonly OverlaySurface surface = new();
    readonly HashSet<string> requested = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> noticeKeys = [];
    readonly List<string> noticeTexts = [];

    /// <summary>Whether any overlay is drawn at all — the single switch, for a shipping build.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The colours and sizes every overlay is drawn with.</summary>
    public OverlayTheme Theme { get; set; } = OverlayTheme.Default;

    /// <summary>Everything registered, in the order it is drawn.</summary>
    public IReadOnlyList<IDiagnosticOverlay> Registered => overlays;

    /// <summary>How many panels the last <see cref="Draw" /> actually put on screen.</summary>
    /// <remarks>
    ///     ⚠ Panels from <see cref="Registered" /> only. The notice panel is not one of them and is
    ///     counted by <see cref="NoticesDrawn" /> — because the number a caller wants here is how
    ///     many of the overlays it switched on are visible, and a standing notice is precisely the
    ///     thing it did not switch on.
    /// </remarks>
    public int DrawnCount { get; private set; }

    /// <summary>How many standing notices the last <see cref="Draw" /> put on screen.</summary>
    public int NoticesDrawn { get; private set; }

    /// <summary>The standing notices, in the order they were first raised.</summary>
    /// <remarks>
    ///     Two parallel lists behind it rather than a list of pairs, so that this is the list itself
    ///     and not a projection of one — a diagnostics property that allocated on every read is the
    ///     shape of an overlay that measures itself.
    /// </remarks>
    public IReadOnlyList<string> Notices => noticeTexts;

    /// <summary>Raises a standing notice, or replaces the one already under that key.</summary>
    /// <param name="key">What the notice is about, so it can be replaced or withdrawn.</param>
    /// <param name="text">The line to draw. Short — it shares a panel with the others.</param>
    /// <exception cref="ArgumentException"><paramref name="key" /> is blank.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A notice is not an overlay, and that is the whole of why it exists.</b> Every
    ///         panel here can be taken off the screen — <see cref="Set" /> (which the console
    ///         publishes as <c>overlay &lt;name&gt; off</c>), <see cref="Remove" />,
    ///         <see cref="DisableAll" />, and <c>Enabled = false</c> written straight onto an overlay
    ///         a caller is holding, which this class never sees at all. So a standing statement about
    ///         the build that lives <em>on</em> a panel is a statement any of those five acts erases,
    ///         and none of them is unusual. This one is drawn by <see cref="Draw" /> itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is still inside <see cref="Enabled" />, deliberately, and that is the line.</b>
    ///         Hiding a <i>panel</i> must not take a notice with it; asking for no diagnostics at all
    ///         is a different act, and there is no honest way to draw on a surface that is not being
    ///         drawn. What this guarantees is that a screenshot with any overlay in it carries the
    ///         notice, which is the condition doc 17 Q5b traded the "release reads only bundles"
    ///         invariant for — see <c>VixenApplication</c>, its only caller today.
    ///     </para>
    ///     <para>
    ///         Nothing here knows what a notice is about. The engine does not know how content was
    ///         mounted and should not learn; the host raises the line and this draws it.
    ///     </para>
    /// </remarks>
    public void Notice(string key, string text) {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(text);

        for (var at = 0; at < noticeKeys.Count; at++) {
            if (string.Equals(noticeKeys[at], key, StringComparison.OrdinalIgnoreCase)) {
                noticeTexts[at] = text;
                return;
            }
        }

        noticeKeys.Add(key);
        noticeTexts.Add(text);
    }

    /// <summary>Withdraws a standing notice.</summary>
    /// <param name="key">What <see cref="Notice" /> was given.</param>
    /// <returns><see langword="true" /> if one was withdrawn.</returns>
    public bool Forget(string key) {
        for (var at = 0; at < noticeKeys.Count; at++) {
            if (string.Equals(noticeKeys[at], key, StringComparison.OrdinalIgnoreCase)) {
                noticeKeys.RemoveAt(at);
                noticeTexts.RemoveAt(at);
                return true;
            }
        }

        return false;
    }

    /// <summary>Adds an overlay, at the end of its corner's stack.</summary>
    /// <param name="overlay">The overlay.</param>
    /// <exception cref="ArgumentException">Something with that name is already registered.</exception>
    public void Add(IDiagnosticOverlay overlay) {
        ArgumentNullException.ThrowIfNull(overlay);

        // Names are what the console types and what a settings file writes down, so two overlays
        // answering to one name is a toggle that flips whichever was registered first and silently
        // ignores the other — a bug that only shows up as "the console command does nothing".
        if (Find(overlay.Name) is not null) {
            throw new ArgumentException($"An overlay called '{overlay.Name}' is already registered.", nameof(overlay));
        }

        overlays.Add(overlay);

        // ⚠ Applied on registration and not once at start-up, because a subsystem's panel is
        // registered by whoever owns its numbers and that is generally after the host has finished
        // reading its command line — `Samples/13` adds the audio panel from `OnInitialise`. A switch
        // applied once would turn on the host's own panels and silently miss every other one, which
        // reads as "that overlay's name does not work".
        if (requested.Contains(overlay.Name)) {
            overlay.Enabled = true;
        }
    }

    /// <summary>Asks for panels by name, whether or not they have been registered yet.</summary>
    /// <param name="names">What they are called. Unknown names are kept, not refused.</param>
    /// <remarks>
    ///     ⚠ <b>An unknown name is remembered rather than rejected</b>, for the same reason the check
    ///     lives in <see cref="Add" />: at the moment a command line is read, most of the panels a
    ///     build has do not exist yet, so "there is no overlay called that" would be true of every
    ///     correct request. <c>overlays</c> at the console is what lists the ones that arrived.
    /// </remarks>
    public void Request(IEnumerable<string>? names) {
        if (names is null) {
            return;
        }

        foreach (var name in names) {
            if (string.IsNullOrWhiteSpace(name)) {
                continue;
            }

            requested.Add(name.Trim());

            if (Find(name) is { } existing) {
                existing.Enabled = true;
            }
        }
    }

    /// <summary>Removes an overlay.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns><see langword="true" /> if one was removed.</returns>
    public bool Remove(string name) {
        var overlay = Find(name);
        return overlay is not null && overlays.Remove(overlay);
    }

    /// <summary>Finds a registered overlay by name, case-insensitively.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The overlay, or <see langword="null" />.</returns>
    public IDiagnosticOverlay? Find(string? name) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        foreach (var overlay in overlays) {
            if (string.Equals(overlay.Name, name, StringComparison.OrdinalIgnoreCase)) {
                return overlay;
            }
        }

        return null;
    }

    /// <summary>Turns one overlay on or off.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="enabled">On, off, or <see langword="null" /> to flip it.</param>
    /// <returns>Its new state, or <see langword="null" /> if there is no such overlay.</returns>
    public bool? Set(string name, bool? enabled = null) {
        var overlay = Find(name);

        if (overlay is null) {
            return null;
        }

        overlay.Enabled = enabled ?? !overlay.Enabled;
        return overlay.Enabled;
    }

    /// <summary>Adds the <c>overlay</c> and <c>overlays</c> commands to a console.</summary>
    /// <param name="commands">The registry to add them to.</param>
    /// <remarks>
    ///     Here rather than in the console, because what the commands do is this class's business and
    ///     the console has no business knowing that overlays exist — which is what lets a host use one
    ///     without the other.
    /// </remarks>
    public void RegisterCommands(ConsoleCommands commands) {
        ArgumentNullException.ThrowIfNull(commands);

        commands.Register(
            "overlays",
            "Lists the diagnostic overlays and whether each is on",
            entry => {
                foreach (var overlay in overlays) {
                    entry.Write($"{overlay.Name,-14} {(overlay.Enabled ? "on" : "off")}  ({overlay.Anchor})");
                }
            }
        );

        commands.Register(
            "overlay",
            "Turns one on or off: overlay <name> [on|off]",
            entry => {
                if (entry.Count == 0) {
                    entry.Write("overlay <name> [on|off]");
                    return;
                }

                var name = entry[0]!;
                var state = entry.TryFlag(1, out var wanted) ? wanted : (bool?) null;
                var result = Set(name, state);

                entry.Write(
                    result is null
                        ? $"There is no overlay called '{name}'. Try 'overlays'."
                        : $"{name} is {(result.Value ? "on" : "off")}."
                );
            }
        );
    }

    /// <summary>Turns every overlay off.</summary>
    public void DisableAll() {
        foreach (var overlay in overlays) {
            overlay.Enabled = false;
        }
    }

    /// <summary>Draws every enabled overlay into a frame's accumulator.</summary>
    /// <param name="draw">Where the geometry goes.</param>
    /// <param name="viewport">How big the screen is, in pixels.</param>
    /// <param name="time">The frame's clock.</param>
    /// <remarks>
    ///     ⚠ <b>Viewport pixels, not window points.</b> The screen lines produced here are projected
    ///     with an orthographic matrix over the render target's own size, so a caller that passes the
    ///     logical window size on a scaled display draws an overlay at half size in the corner.
    /// </remarks>
    public void Draw(DebugDraw draw, Vector2 viewport, in GameTime time) {
        ArgumentNullException.ThrowIfNull(draw);

        DrawnCount = 0;
        NoticesDrawn = 0;

        if (!Enabled || !draw.Enabled || viewport.X <= 0f || viewport.Y <= 0f) {
            return;
        }

        surface.Begin(draw, viewport, Theme);
        DrawNotices();

        foreach (var overlay in overlays) {
            if (!overlay.Enabled) {
                continue;
            }

            overlay.Draw(surface, time);
            DrawnCount++;
        }
    }

    /// <summary>Draws the standing notices, above whatever is stacked in the top-left corner.</summary>
    /// <remarks>
    ///     <para>
    ///         Before the panels rather than after, because a corner is a stack in the order things
    ///         are drawn — so this puts the notice at the top of the screen where the frame statistics
    ///         usually start, and pushes them down rather than sitting under them. A notice that
    ///         scrolled below three panels would be a notice somebody has to look for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="OverlayTheme.Bad" />, not <see cref="OverlayTheme.Warning" />.</b> The
    ///         one caller today is a build whose content did not come from its own bundles, which is
    ///         a statement about what the picture <i>is</i> rather than a number near a limit — and
    ///         the colour is what makes a screenshot answer the question before anybody asks it.
    ///     </para>
    /// </remarks>
    void DrawNotices() {
        if (noticeTexts.Count == 0) {
            return;
        }

        var region = surface.Panel(OverlayAnchor.TopLeft, NoticeWidth, noticeTexts.Count, "BUILD");

        if (region.IsEmpty) {
            return;
        }

        for (var at = 0; at < noticeTexts.Count; at++) {
            region.Text(at, noticeTexts[at], Theme.Bad);
        }

        NoticesDrawn = noticeTexts.Count;
    }
}
