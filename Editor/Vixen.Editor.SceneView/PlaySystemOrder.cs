// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.SceneView;

/// <summary>Says a contribution puts a service into the session, so others can be sorted after it.</summary>
/// <remarks>
///     <para>
///         <b>The declaration that was a comment.</b> <c>EditorApplication</c> registers
///         <c>PlayPhysics</c> before any module activates, and the reason is written above the call:
///         <i>"which is also what lets the terrain module's collider contribution find the scene this
///         one provides"</i>. That is a real dependency held together by the sequence of two
///         registrations in two assemblies — and <c>EditorModules.Standard</c>'s own comment says its
///         order is *not* about a dependency, which is true of the list and not of the frame.
///     </para>
///     <para>
///         ⚠ <b>The service, not the contribution, and that is the point rather than a shortcut.</b>
///         <c>PlayTerrainColliders</c> lives in <c>Vixen.Editor.Terrain.Physics</c> and
///         <c>PlayPhysics</c> in <c>Vixen.Editor.App</c>, which is above it — so a
///         <c>[RunsAfter(typeof(PlayPhysics))]</c> could not be written at all without a reference
///         that would invert the layering. Both already name <c>PhysicsScene</c>, because that is
///         what one hands over and the other asks for.
///     </para>
///     <para>
///         ⚠ <b>And it is checked rather than believed.</b> A contribution that declares this and
///         does not call <see cref="PlaySession.Provide{T}" /> is named in
///         <c>PlayModeController.Ordering</c> — the session is asked, after the contribution has
///         attached, whether the service is actually there. A declaration nothing verifies is the
///         one that goes stale and reorders a frame for a service that stopped existing.
///     </para>
/// </remarks>
/// <param name="service">The type it provides, as <see cref="PlaySession.Provide{T}" /> keys it.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ProvidesAttribute(Type service) : Attribute {
    /// <summary>The service type.</summary>
    public Type Service { get; } = service ?? throw new ArgumentNullException(nameof(service));
}

/// <summary>Says a contribution must attach after whatever provides a service.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Naming a service, not a contribution</b> — see <see cref="ProvidesAttribute" /> for
///         why that is the only form the layering permits.
///     </para>
///     <para>
///         ⚠ <b>Unmet is not an error.</b> Nothing providing the service is the ordinary situation a
///         contribution already handles — <c>PlayTerrainColliders</c> returns without adding a system
///         when the session has no <c>PhysicsScene</c>, which is "a terrain with no collision, not an
///         error". So an unmet edge leaves the contribution where registration order put it and is
///         *named* in <c>PlayModeController.Ordering</c>, on this repository's standing rule that a
///         part of the frame which did not happen is said out loud.
///     </para>
/// </remarks>
/// <param name="service">The service it needs to find in the session.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RunsAfterAttribute(Type service) : Attribute {
    /// <summary>The service type.</summary>
    public Type Service { get; } = service ?? throw new ArgumentNullException(nameof(service));
}

/// <summary>Puts the play-mode contributions in an order their declarations justify.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Registration order is the tie-break and the default, deliberately.</b> A sort that
///         reordered contributions with nothing to say about each other would make the frame depend
///         on a hash order and turn every existing comment about sequence into a lie. With no
///         attributes anywhere this returns exactly what it was given, which is what every
///         contribution in the tree got before the attributes existed.
///     </para>
///     <para>
///         ⚠ <b>It cannot move the snapshot.</b> <c>PlayModeController.Play</c> captures the world
///         and *then* calls <c>Contribute</c>; this runs inside that call, so everything a
///         contribution creates is still outside the snapshot and still goes away with a Stop. An
///         ordering mechanism that ran earlier — at registration, say — would be the one change that
///         could move that boundary, which is why the sort is here and not in
///         <c>IEditorRegistry.Add</c>.
///     </para>
/// </remarks>
public static class PlaySystemOrder {
    /// <summary>Sorts contributions so that a provider attaches before anything that asked for it.</summary>
    /// <param name="contributions">The contributions, in registration order.</param>
    /// <param name="notes">One readable line per edge that could not be honoured. Empty is the usual answer.</param>
    /// <returns>The order to attach them in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contributions" /> is null.</exception>
    public static IReadOnlyList<T> Sort<T>(IReadOnlyList<T> contributions, out IReadOnlyList<string> notes)
        where T : class {
        ArgumentNullException.ThrowIfNull(contributions);

        List<string> lines = [];
        notes = lines;

        if (contributions.Count < 2) {
            Unmet(contributions, lines);

            return contributions;
        }

        var wanted = new List<Type>[contributions.Count];
        var supplied = new List<Type>[contributions.Count];

        for (var index = 0; index < contributions.Count; index++) {
            var type = contributions[index].GetType();

            wanted[index] = [.. type.GetCustomAttributes(typeof(RunsAfterAttribute), false)
                .Cast<RunsAfterAttribute>()
                .Select(attribute => attribute.Service)];

            supplied[index] = [.. type.GetCustomAttributes(typeof(ProvidesAttribute), false)
                .Cast<ProvidesAttribute>()
                .Select(attribute => attribute.Service)];
        }

        // An edge from every provider to everything that asked for what it provides. Counted rather
        // than stored, because the answer is which node has nothing left in front of it.
        var remaining = new int[contributions.Count];
        var after = new List<int>[contributions.Count];

        for (var index = 0; index < contributions.Count; index++) {
            after[index] = [];
        }

        for (var consumer = 0; consumer < contributions.Count; consumer++) {
            foreach (var service in wanted[consumer]) {
                var met = false;

                for (var provider = 0; provider < contributions.Count; provider++) {
                    if (provider == consumer || !supplied[provider].Contains(service)) {
                        continue;
                    }

                    after[provider].Add(consumer);
                    remaining[consumer]++;
                    met = true;
                }

                if (!met) {
                    lines.Add(
                        $"{Name(contributions[consumer])} wants a {service.Name} and no contribution "
                        + "declares one — it attaches where it was registered"
                    );
                }
            }
        }

        // ⚠ Lowest original index among the ready ones, which is what makes this stable: two
        // contributions that do not constrain each other come out in the order they were added.
        List<T> ordered = new(contributions.Count);
        var placed = new bool[contributions.Count];

        for (var step = 0; step < contributions.Count; step++) {
            var next = -1;

            for (var index = 0; index < contributions.Count; index++) {
                if (!placed[index] && remaining[index] == 0) {
                    next = index;

                    break;
                }
            }

            if (next < 0) {
                // A cycle. Everything still standing keeps registration order, because an arbitrary
                // half of a cycle is worse than the order somebody wrote down — and every one of
                // them is named, since a frame ordered by a rule that could not be applied is a
                // frame nobody can reason about.
                for (var index = 0; index < contributions.Count; index++) {
                    if (!placed[index]) {
                        placed[index] = true;
                        ordered.Add(contributions[index]);

                        lines.Add(
                            $"{Name(contributions[index])} is in a cycle of [RunsAfter] declarations "
                            + "— it attaches where it was registered"
                        );
                    }
                }

                break;
            }

            placed[next] = true;
            ordered.Add(contributions[next]);

            foreach (var consumer in after[next]) {
                remaining[consumer]--;
            }
        }

        return ordered;
    }

    /// <summary>Names what a contribution declared it provides and did not.</summary>
    /// <param name="contribution">The one that has just attached.</param>
    /// <param name="session">The session it attached to.</param>
    /// <param name="notes">Where to put a line for each service that is not there.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Asked of the session rather than trusted.</b> A <c>[Provides]</c> that has stopped
    ///     being true still sorts other contributions after it, so it would go on quietly deciding
    ///     the frame's order for a service nothing can find. This is the instrument that fails
    ///     loudly on the day the declaration and the code disagree.
    /// </remarks>
    public static void Verify(object contribution, PlaySession session, IList<string> notes) {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(notes);

        var services = (IServiceProvider) session;

        foreach (var declared in contribution.GetType()
                     .GetCustomAttributes(typeof(ProvidesAttribute), false)
                     .Cast<ProvidesAttribute>()) {
            if (services.GetService(declared.Service) is null) {
                notes.Add(
                    $"{Name(contribution)} declares [Provides(typeof({declared.Service.Name}))] and "
                    + "did not provide one — anything that asked for it was sorted after nothing"
                );
            }
        }
    }

    static void Unmet<T>(IReadOnlyList<T> contributions, List<string> lines) where T : class {
        foreach (var contribution in contributions) {
            foreach (var wanted in contribution.GetType()
                         .GetCustomAttributes(typeof(RunsAfterAttribute), false)
                         .Cast<RunsAfterAttribute>()) {
                lines.Add(
                    $"{Name(contribution)} wants a {wanted.Service.Name} and no contribution declares "
                    + "one — it attaches where it was registered"
                );
            }
        }
    }

    static string Name(object contribution) => contribution.GetType().Name;
}
