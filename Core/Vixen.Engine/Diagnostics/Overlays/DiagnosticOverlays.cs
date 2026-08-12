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
    readonly List<IDiagnosticOverlay> overlays = [];
    readonly OverlaySurface surface = new();
    readonly HashSet<string> requested = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any overlay is drawn at all — the single switch, for a shipping build.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The colours and sizes every overlay is drawn with.</summary>
    public OverlayTheme Theme { get; set; } = OverlayTheme.Default;

    /// <summary>Everything registered, in the order it is drawn.</summary>
    public IReadOnlyList<IDiagnosticOverlay> Registered => overlays;

    /// <summary>How many panels the last <see cref="Draw" /> actually put on screen.</summary>
    public int DrawnCount { get; private set; }

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

        if (!Enabled || !draw.Enabled || viewport.X <= 0f || viewport.Y <= 0f) {
            return;
        }

        surface.Begin(draw, viewport, Theme);

        foreach (var overlay in overlays) {
            if (!overlay.Enabled) {
                continue;
            }

            overlay.Draw(surface, time);
            DrawnCount++;
        }
    }
}
