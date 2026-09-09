// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Behaviors;

namespace Vixen.Cli;

/// <summary>`vixen doctor behaviors` — which behaviour types a project has, and how many of each.</summary>
/// <remarks>
///     <para>
///         <b>The question this answers.</b> [04](../../../docs/plan/04-ecs-and-scripting.md) §
///         <i>When to write one, and when to write a component and a system</i> rests on one
///         judgement — "one instance, or a handful" against "many instances, the same operation over
///         all of them" — and an author who guessed wrong finds out at ten thousand instances, when
///         re-authoring is expensive, rather than at two hundred, when it is not. The number that
///         judgement is about was unreachable until <see cref="BehaviorStore.Population" />
///         ([#1199](https://github.com/Rikarin/Vixen/issues/1199)): it lived on a private nested
///         bucket and the store summed it away.
///     </para>
///     <para>
///         ⚠ <b>What it counts is what a scene <em>authors</em>, and in this repository that is
///         nothing.</b> All fourteen committed <c>.vxscene</c> files name components and no
///         behaviours; every behaviour instance in <c>Samples/13</c> is attached from code —
///         <c>loop.Behaviors.Add(lamps[index], new LampFlicker …)</c>, one per point light the level
///         placed. So a report over a real project's scenes can legitimately be a list of zeroes,
///         and a command that printed that as a clean bill of health would be the exact instrument
///         failure a doctor exists to catch. It says so instead, in a finding, whenever a scene
///         named no behaviour at all.
///     </para>
///     <para>
///         ⚠ <b>It runs no lifecycle, which is the same line <see cref="SystemsRunner" /> does not
///         cross.</b> Attaching a behaviour queues <c>Awake</c>; draining that queue is running
///         somebody's game code to find out how much of it there is. So the enabled/disabled split
///         is read from <see cref="Behavior.Enabled" /> on the authored instances rather than from
///         the bucket's partition, which before a drain is zero for everything.
///     </para>
/// </remarks>
internal static class BehaviorsRunner {
    /// <summary>
    ///     How many instances of one type is enough to be worth a second look, per doc 04 §
    ///     <i>When to write one</i>. ⚠ An opinion the document states and this reads, not a number
    ///     this command invented — a threshold a tool picks for itself is one nobody can argue with.
    /// </summary>
    const int Many = 200;

    /// <summary>Examines the behaviours the named assemblies declare and the named scenes author.</summary>
    /// <param name="assemblyPaths">The built game assemblies, as <c>--assembly</c> gave them.</param>
    /// <param name="scenePaths">The authored scenes to count instances in, as <c>--scene</c> gave them.</param>
    /// <returns>What it found, in the order a report should print it.</returns>
    public static List<Finding> Examine(IReadOnlyList<string> assemblyPaths, IReadOnlyList<string> scenePaths) {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        ArgumentNullException.ThrowIfNull(scenePaths);

        var findings = new List<Finding>();
        var assemblies = SystemsRunner.Load(assemblyPaths, findings, "a behaviour");

        if (assemblies.Count == 0) {
            return findings;
        }

        Declared(assemblies, findings);

        foreach (var scene in scenePaths) {
            Count(scene, findings);
        }

        if (scenePaths.Count == 0) {
            findings.Add(
                new(
                    Health.Concerning,
                    "instances",
                    "no --scene was named, so nothing here counted anything. An assembly says which "
                    + "behaviour types exist and cannot say how many of each a level has — that is "
                    + "the number doc 04's authoring rule is about."
                )
            );
        }

        return findings;
    }

    /// <summary>Every behaviour type in the assemblies, and whether a scene may name it.</summary>
    /// <remarks>
    ///     ⚠ <b>A behaviour with no <c>[DataContract]</c> is not a defect.</b> It is code-only: a
    ///     game attaching it from <c>Game.OnInitialise</c> is doing something the design supports,
    ///     and the registry never hears about it. What it is, is invisible to a scene and to the
    ///     count below — which is worth one line each, and not a complaint.
    /// </remarks>
    static void Declared(IReadOnlyList<Assembly> assemblies, List<Finding> findings) {
        var types = new List<Type>();

        foreach (var assembly in assemblies) {
            Type?[] found;

            try {
                found = assembly.GetTypes();
            } catch (ReflectionTypeLoadException partial) {
                found = partial.Types;

                findings.Add(
                    new(
                        Health.Concerning,
                        assembly.GetName().Name ?? "assembly",
                        "some of its types could not be loaded, so this list may be short. A "
                        + "reference it was built against is probably not beside it."
                    )
                );
            }

            types.AddRange(
                found.Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false }
                        && typeof(Behavior).IsAssignableFrom(type)
                    )!
            );
        }

        if (types.Count == 0) {
            findings.Add(
                new(
                    Health.Concerning,
                    "behaviours",
                    "no Behavior subclass is in these assemblies. Either this project uses systems "
                    + "and components throughout, which is the design's preference, or the assembly "
                    + "named is not the one with the game code in it."
                )
            );

            return;
        }

        findings.Add(
            new(
                Health.Fine,
                "behaviours",
                string.Create(CultureInfo.InvariantCulture, $"{types.Count} behaviour type(s) declared.")
            )
        );

        foreach (var type in types.OrderBy(one => one.Name, StringComparer.Ordinal)) {
            findings.Add(
                new(
                    Health.Fine,
                    type.Name,
                    SceneBehaviorRegistry.TryGet(type, out var binder)
                        ? $"a scene may name it as !{binder.Name}."
                        : "carries no [DataContract], so no scene can name it and it is attached from "
                        + "code only. Nothing here can count those."
                )
            );
        }
    }

    /// <summary>Loads one scene's behaviours into a scratch world and reports the per-type count.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="ISceneBehaviorBinder.AttachTo" /> and a real
    ///     <see cref="BehaviorStore" />, rather than by counting the parsed objects.</b> The store is
    ///     what buckets by concrete type, and the bucket is what the authoring rule is about — a
    ///     tally kept here would be a second implementation of the thing being measured, and would
    ///     agree with it right up until <c>Add&lt;T&gt;</c>'s bucketing changed.
    /// </remarks>
    static void Count(string path, List<Finding> findings) {
        var full = Path.GetFullPath(path);

        if (!File.Exists(full)) {
            findings.Add(new(Health.Broken, "scene", $"there is nothing at '{path}'."));
            return;
        }

        SceneFile file;

        try {
            file = SceneFile.FromYaml(File.ReadAllText(full));
        } catch (Exception failure) when (failure is YamlParseException
                                             or YamlBindingException
                                             or NotSupportedException) {
            // A binding failure is usually a name this build does not claim, which is a real answer
            // to "what is wrong with my project" and not a reason to say nothing.
            findings.Add(new(Health.Broken, Path.GetFileName(full), $"could not be read: {failure.Message}"));
            return;
        }

        using var world = new World($"doctor:{file.Name}");
        var store = new BehaviorStore(world);
        var disabled = new Dictionary<Type, int>();

        foreach (var authored in file.All()) {
            foreach (var component in authored.Components) {
                if (component is not Behavior behavior
                    || !SceneBehaviorRegistry.TryGet(component.GetType(), out var binder)) {
                    continue;
                }

                binder.AttachTo(store, world.Create(), component);

                if (!behavior.Enabled) {
                    disabled[component.GetType()] = disabled.GetValueOrDefault(component.GetType()) + 1;
                }
            }
        }

        var population = store.Population;

        if (population.Count == 0) {
            findings.Add(
                new(
                    Health.Concerning,
                    Path.GetFileName(full),
                    "names no behaviour at all, so this counted nothing. That is the normal shape of a "
                    + "project that attaches its behaviours from code — every behaviour instance in "
                    + "Samples/13 is added by Arena.cs and none of them is in the .vxscene — and it "
                    + "means the number doc 04's rule is about is still not visible for this project."
                )
            );

            return;
        }

        foreach (var (type, total, _) in population) {
            var off = disabled.GetValueOrDefault(type);
            var detail = string.Create(CultureInfo.InvariantCulture, $"{total} authored, {total - off} of them enabled.");

            if (total >= Many) {
                // The number as a string first: interpolating an int would take the current culture,
                // and a threshold that reads "1 000" in one locale and "1000" in another is a report
                // nobody can grep.
                var many = Many.ToString(CultureInfo.InvariantCulture);

                detail += $" ⚠ Past {many}, which doc 04 calls many: the same operation over all of "
                    + "them is what a component and a system are for, and converting later is a "
                    + "re-authoring nothing does for you.";
            }

            findings.Add(new(total >= Many ? Health.Concerning : Health.Fine, $"{Path.GetFileName(full)} {type.Name}", detail));
        }
    }
}
