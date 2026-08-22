// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>The surfaces one rule set is being shown on, and what <c>@media</c> answers on each.</summary>
/// <remarks>
///     <para>
///         <b>One rule set, several answers.</b> A document's surfaces share a rule set — that is
///         what keeps one theme across a torn-off window, and it is the reason <c>@media</c> could
///         not answer per window while the verdict lived in the rule set. A scope is where the
///         verdict lives instead: one <see cref="MediaContext" /> and one <see cref="MediaVerdicts" />
///         per place the document is shown, and an element resolves against the scope it is in.
///     </para>
///     <para>
///         ⚠ <b>Ids are monotonic and never reused, for the reason <c>UiSurface.Id</c> is.</b> A
///         node carries its scope, and a scope handed out again after a window closed would give
///         whatever had inherited the old id the new window's breakpoints. Scopes are created when a
///         window opens, so the list grows at human speed and never being reclaimed costs a
///         reference each.
///     </para>
///     <para>
///         ⚠ <b>Verdicts are re-evaluated lazily against <see cref="MediaConditions.Revision" />
///         rather than eagerly on a reload.</b> A load registers groups one at a time — a sheet with
///         forty <c>@media</c> blocks bumps the revision forty times — and evaluating every scope
///         after each would be quadratic in the number of windows for no observable difference. The
///         cost of the laziness is one integer compare on the path that reads them, which is the
///         cascade, and that path was already doing dictionary work per element.
///     </para>
/// </remarks>
public sealed class MediaScopes {
    /// <summary>The scope an element outside every surface is in, and the engine's own default.</summary>
    public const int Document = 0;

    readonly MediaConditions conditions;
    readonly List<Entry> scopes = [];

    /// <summary>Creates a registry over a condition table, with the document's scope in it.</summary>
    /// <param name="conditions">The groups verdicts are about.</param>
    public MediaScopes(MediaConditions conditions) {
        ArgumentNullException.ThrowIfNull(conditions);

        this.conditions = conditions;
        scopes.Add(new Entry(default, default));
    }

    /// <summary>How many scopes have been created, the document's included.</summary>
    public int Count => scopes.Count;

    /// <summary>Adds a place the document is shown.</summary>
    /// <param name="context">What that surface is.</param>
    /// <returns>Its scope id, which its elements carry.</returns>
    public int Create(MediaContext context) {
        scopes.Add(new Entry(context, default));
        return scopes.Count - 1;
    }

    /// <summary>What a scope's surface is.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>Its context.</returns>
    public MediaContext ContextOf(int scope) => scopes[scope].Context;

    /// <summary>Tells a scope what its surface is now.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="context">What the surface is now.</param>
    /// <returns>Whether that changed any group's verdict here.</returns>
    /// <remarks>
    ///     ⚠ <b>The return value is the guard a window drag rides on, and it is about the verdicts
    ///     rather than the context.</b> A resize moves <see cref="MediaContext.Width" /> on every
    ///     frame of it and almost never moves an answer; a caller that forgot everything on every
    ///     frame would re-cascade the whole window sixty times a second. A document with no
    ///     <c>@media</c> in it — which is every stylesheet this repository ships — has one group,
    ///     compares one boolean, and never says yes.
    /// </remarks>
    public bool Set(int scope, MediaContext context) {
        var before = VerdictsOf(scope);
        var after = conditions.Evaluate(context);

        scopes[scope] = new Entry(context, after);

        return before != after;
    }

    /// <summary>Which groups hold in a scope.</summary>
    /// <param name="scope">The scope, or anything out of range for the conservative answer.</param>
    /// <returns>The verdicts.</returns>
    /// <remarks>
    ///     Out of range answers <c>default</c> — the unconditional group and nothing else — rather
    ///     than throwing, because the caller is the cascade and a node carrying a stale scope is a
    ///     bug that should show as missing <c>@media</c> rules rather than as a crash mid-frame.
    /// </remarks>
    public MediaVerdicts VerdictsOf(int scope) {
        if ((uint) scope >= (uint) scopes.Count) {
            return default;
        }

        var entry = scopes[scope];

        if (entry.Verdicts.Revision == conditions.Revision) {
            return entry.Verdicts;
        }

        var evaluated = conditions.Evaluate(entry.Context);
        scopes[scope] = entry with { Verdicts = evaluated };

        return evaluated;
    }

    /// <summary>Whether any scope's verdicts have moved since they were last read.</summary>
    /// <returns>Whether a caller holding resolved styles has to forget them.</returns>
    /// <remarks>
    ///     What a reload owes its consumers. <see cref="VerdictsOf" /> would answer correctly on its
    ///     own — it re-evaluates a stale scope on the way past — but a consumer that has already
    ///     resolved every element needs to know it should ask again, and reading each scope to find
    ///     out is exactly this.
    /// </remarks>
    public bool Refresh() {
        var moved = false;

        for (var i = 0; i < scopes.Count; i++) {
            var before = scopes[i].Verdicts;

            if (before.Revision == conditions.Revision) {
                continue;
            }

            moved |= VerdictsOf(i) != before;
        }

        return moved;
    }

    readonly record struct Entry(MediaContext Context, MediaVerdicts Verdicts);
}
