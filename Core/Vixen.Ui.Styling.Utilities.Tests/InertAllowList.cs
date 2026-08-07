// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>One line of the expiring allow-list: a property, the task that will close it, and why.</summary>
/// <param name="Task">The plan document's task number, or null if the line does not name one.</param>
/// <param name="Reason">The rest of the line.</param>
sealed record InertEntry(int? Task, string Reason);

/// <summary>The properties a family may emit today without a consumer, each with an expiry.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Designed against <c>docs/DocsExempt.txt</c>, which is this repository's own cautionary
///         example.</b> That file carries the instruction "Do not add a line to DocsExempt.txt to make
///         a build pass" and it rotted anyway, because an instruction is not a mechanism: nothing ever
///         checks whether an exempted type has since gained a page, so a line costs one keystroke to
///         add and nothing at all to keep. Three mechanisms here make the arithmetic come out the other
///         way round.
///     </para>
///     <para>
///         <b>One — a line expires on its condition, not on a date.</b> The run in which a consumer
///         starts acting on the property is the run that fails on the exemption. There is no way to
///         keep a line alive past its reason, and no version of "just bump it" that works.
///     </para>
///     <para>
///         <b>Two — a line has to name a task the plan document has heard of.</b> An invented number
///         fails, so the cheapest path to a new line runs through writing down, in
///         <c>docs/plan/43</c>, what would close it. That is more work than fixing several of the
///         properties currently on the list, which is the point.
///     </para>
///     <para>
///         <b>Three — the whole list is printed on every run.</b> A silent pass is how eighteen becomes
///         thirty without anybody deciding to allow it.
///     </para>
///     <para>
///         What is deliberately <i>not</i> here is a per-line escape for "the gate cannot see this
///         one". A property the differential cannot observe is a property with no observable effect,
///         which is the same statement the gate is making; if that is ever wrong the fix is a scene
///         that observes it, in <c>UtilityConsumptionProbe</c>, not a line here.
///     </para>
/// </remarks>
static class InertAllowList {
    /// <summary>Reads the list beside the test assembly.</summary>
    /// <returns>Property name to entry, in file order.</returns>
    /// <remarks>
    ///     Blank lines and <c>#</c> comments are skipped; everything else is
    ///     <c>property whitespace #task whitespace reason</c>. A line whose task is missing parses to a
    ///     null <see cref="InertEntry.Task" /> rather than throwing, because
    ///     <c>Every_allow_list_entry_names_a_task_the_plan_document_knows</c> reports it far better
    ///     than an exception during collection would.
    /// </remarks>
    public static IReadOnlyDictionary<string, InertEntry> Load() {
        var path = Path.Combine(AppContext.BaseDirectory, "InertProperties.txt");
        var entries = new Dictionary<string, InertEntry>(StringComparer.Ordinal);

        foreach (var raw in File.ReadAllLines(path)) {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var property = parts[0];

            var task = parts.Length > 1 && parts[1].StartsWith('#') && int.TryParse(parts[1][1..], out var number)
                ? number
                : (int?)null;

            var reason = task is null
                ? string.Join(' ', parts.Skip(1))
                : string.Join(' ', parts.Skip(2));

            entries[property] = new InertEntry(task, reason);
        }

        return entries;
    }
}
