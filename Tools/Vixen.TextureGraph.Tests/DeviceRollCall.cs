// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Testing;

/// <summary>
///     Doc 48's exit criterion 11 — "a device is confirmed by name in every GPU test in this area" —
///     as one walk both of the area's test projects take, rather than as one walk and a convention.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The criterion's area is two test projects and the roll call covered one</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/883">#883</a>). That is the third time in
///         this workstream a rule's subject set has been narrower than the rule
///         (<a href="https://github.com/Rikarin/Vixen/issues/814">#814</a>,
///         <a href="https://github.com/Rikarin/Vixen/issues/872">#872</a>), and the shape that fixes
///         it is the one <c>PluginReferenceRule</c> uses: the walk takes its subject as a parameter
///         and both projects call it. Two callers of one function, not two transcriptions of one
///         idea.
///     </para>
///     <para>
///         ⚠ <b><see cref="Take" /> is the weaker of the mechanisms and it is kept because it covers
///         a hole the stronger ones cannot.</b> A harness that names the adapter itself makes an
///         anonymous device impossible for anything that goes <em>through</em> the harness; a file
///         that creates a device itself bypasses it entirely, and this walk is what notices that the
///         file opened a device without naming one. It is weak because its detector is a
///         <em>naming convention</em> — the name the harness gave its opener — which the author of
///         the next device file chooses, so a file that calls its own opener something else is
///         invisible to it. ⚠ It matches prose as readily as code, so a paragraph anywhere in the
///         directory that quotes the opener's name is a file the walk then expects to name an
///         adapter. That is not a flaw worth removing — the detector has to be a plain substring
///         to be one the caller supplies — but it is why <see cref="Creates" /> is declared in
///         two pieces and why these paragraphs do not spell either call.
///     </para>
///     <para>
///         ⚠ <b><see cref="Sole" /> is the answer to that, and it is the one that makes an anonymous
///         device impossible rather than merely noticed</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/923">#923</a>). Its detector is the
///         backend call that actually produces a device, which no test author can rename, so the
///         subject set is every file that could open one rather than every file that named its
///         opener the expected thing. Requiring exactly the harness to match means the eighth device
///         file cannot get a device except through the harness, and the harness names the adapter.
///         That is the fourth time in this workstream a rule's subject set has been narrower than
///         the rule (<a href="https://github.com/Rikarin/Vixen/issues/814">#814</a>,
///         <a href="https://github.com/Rikarin/Vixen/issues/872">#872</a>,
///         <a href="https://github.com/Rikarin/Vixen/issues/883">#883</a>), and it is the first time
///         the answer has been to widen the subject set to something the subject cannot opt out of.
///     </para>
///     <para>
///         ⚠ <b>A walk that reads no files reports every file compliant, which is the failure
///         criterion 11 exists because of</b> — eighteen golden files <em>passed</em> rather than
///         skipped without a device until 2026-08-21. So <see cref="Take" /> refuses before it
///         reports: the directory has to exist, it has to contain the caller's own anchor file, the
///         harness has to be one of the matches, and the match set has to be big enough to be the
///         device suites rather than a stray file.
///     </para>
/// </remarks>
static class DeviceRollCall {
    /// <summary>The backend call that produces a device, which is the one way to get one.</summary>
    /// <remarks>
    ///     ⚠ <b>Written in two pieces so that this file is not itself a match.</b> The detector is
    ///     matched against whole file texts including the one that declares it, and a rule that
    ///     fires on its own declaration is a false positive nobody can remove — the same shape as an
    ///     exemption list whose first entry excuses the rule from itself. Prose in these files says
    ///     "creates a device" for the same reason.
    /// </remarks>
    public const string Creates = "VulkanDevice" + ".TryCreate(";

    /// <summary>What one file looked like to the walk.</summary>
    /// <param name="Name">Its file name.</param>
    /// <param name="Text">Its whole text, which is what both detectors are matched against.</param>
    public sealed record Source(string Name, string Text);

    /// <summary>Every file in a project's own directory, read once.</summary>
    /// <param name="directory">The directory, from a caller's <c>[CallerFilePath]</c>.</param>
    /// <param name="anchor">The calling file's own name, which the directory has to contain.</param>
    /// <returns>One entry per <c>.cs</c> file directly in the directory.</returns>
    /// <remarks>
    ///     ⚠ <b>Top level only, and anchored at the caller rather than at the repository root.</b>
    ///     <c>.claude/worktrees</c> holds a whole checkout per agent, so a walk that climbs would be
    ///     comparing other people's copies of these files with each other — the trap the golden walk
    ///     and <c>CheckStrings</c> each hit once.
    /// </remarks>
    public static Source[] Read(string directory, string anchor) {
        Assert.True(
            Directory.Exists(directory),
            $"'{directory}' does not exist, so this roll call read no files at all. It is anchored at the "
            + "calling file's compiled path; a run whose sources are not on the machine cannot take it."
        );

        var sources = Directory.GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(path => new Source(Path.GetFileName(path), File.ReadAllText(path)))
            .ToArray();

        Assert.Contains(sources, source => string.Equals(source.Name, anchor, StringComparison.Ordinal));

        return sources;
    }

    /// <summary>
    ///     ⚠ Every file that opens a device names the adapter, or is on a written list.
    /// </summary>
    /// <param name="sources">The project's files, from <see cref="Read" />.</param>
    /// <param name="opens">The text a file uses to open a device — there is one way per project.</param>
    /// <param name="names">The text that naming the adapter looks like.</param>
    /// <param name="harness">The file that owns <paramref name="opens" />, required to be a match.</param>
    /// <param name="least">
    ///     The smallest match set that can still be the device suites. Below it the detector has
    ///     drifted and the walk is reading an almost empty set, which is a pass that means nothing.
    /// </param>
    /// <param name="anonymous">
    ///     The files excused from naming the adapter, each with the reason. Checked from both ends: a
    ///     name here whose file names the adapter now is an allowance that has outlived what it
    ///     excused, so the list can only shrink.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>The detector is anchored to the harness, and the roll call checks the anchor.</b> If
    ///     the opening call is renamed, every file stops matching and the roll call would pass over an
    ///     empty set — the silent-success failure this whole file is about — so the harness itself is
    ///     required to match, and the failure says what to update.
    /// </remarks>
    public static void Take(
        Source[] sources,
        string opens,
        string names,
        string harness,
        int least,
        (string File, string Reason)[] anonymous
    ) {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(anonymous);

        var opening = sources
            .Where(source => source.Text.Contains(opens, StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(opening, source => string.Equals(source.Name, harness, StringComparison.Ordinal));

        Assert.True(
            opening.Length >= least,
            $"Only {opening.Length} files here contain '{opens}', and there were at least {least} when this was "
            + "written. The way a device is opened in this project has changed and this roll call is now reading "
            + "an almost empty set — which is the failure it exists to prevent, not a pass."
        );

        var unnamed = opening
            .Where(source => !source.Text.Contains(names, StringComparison.Ordinal))
            .Select(source => source.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var excused = anonymous.Select(entry => entry.File).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(excused, unnamed);

        Assert.All(excused, name => Assert.Contains(opening, source => string.Equals(source.Name, name, StringComparison.Ordinal)));
        Assert.All(anonymous, entry => Assert.True(entry.Reason.Length > 40, entry.File));
    }

    /// <summary>
    ///     ⚠ The harness is the only file here that creates a device, so no other file can have one
    ///     the harness has not named.
    /// </summary>
    /// <param name="sources">The project's files, from <see cref="Read" />.</param>
    /// <param name="harness">The file that is allowed to call <see cref="Creates" />.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The strong half of criterion 11, and the one <see cref="Take" /> cannot be.</b>
    ///         <c>Take</c> asks whether a file that <em>looks like</em> it opens a device names the
    ///         adapter; this asks whether any file other than the harness <em>can</em> open one at
    ///         all. The difference is the eighth device file: it is invisible to <c>Take</c> if its
    ///         author gives the helper another name, and it cannot be
    ///         invisible to this, because the only way to get a device is the call this matches.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its instrument check is the assertion itself rather than a floor beside it.</b>
    ///         The set is required to be exactly the harness, so the two ways for this to run over
    ///         nothing — a walk that read no files, and a backend that renamed the call — both leave
    ///         the harness missing from the set and both fail. There is no arrangement in which it
    ///         reports success without having looked.
    ///     </para>
    /// </remarks>
    public static void Sole(Source[] sources, string harness) {
        ArgumentNullException.ThrowIfNull(sources);

        var creating = sources
            .Where(source => source.Text.Contains(Creates, StringComparison.Ordinal))
            .Select(source => source.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            creating.Length == 1 && string.Equals(creating[0], harness, StringComparison.Ordinal),
            $"'{Creates}' is expected in '{harness}' and nowhere else here, and it is in "
            + (creating.Length == 0
                ? "no file at all — either this walk read nothing, or the backend renamed the call and this "
                + "roll call is now matching an empty set, which is the silent success it exists to prevent"
                : $"[{string.Join(", ", creating)}]. A file that creates its own device goes round the harness, "
                + "so its adapter is named only if that file remembered to — which is the convention the harness "
                + "replaced. Call the harness instead.")
        );
    }
}
