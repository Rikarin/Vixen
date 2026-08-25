// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using Vixen.Ecs.Systems;
using Vixen.Engine.Frames;

namespace Vixen.Cli;

/// <summary>`vixen doctor systems` — what a project's frame actually looks like.</summary>
/// <remarks>
///     <para>
///         <b>The question this answers.</b> A system graph is the thing a project most often has
///         wrong and cannot see: the order is decided by attributes spread across a dozen files, an
///         <c>[UpdateAfter]</c> naming a system that is not there is dropped without a word, and
///         whether a system is in the frame at all depends on whether somebody remembered
///         <c>[GameSystem]</c>. None of that is visible by reading one file.
///     </para>
///     <para>
///         ⚠ <b>It builds no systems, and that is the line it does not cross.</b> A declared
///         system's constructor takes services, and the registry those come from is assembled when
///         the game runs — see <c>VixenApplication.Initialise</c>. This has an assembly and no game,
///         so it reads <see cref="SystemGraph.Plan" />, which is the runner's own sort over types.
///         The alternative — standing up a world and calling <c>Game.OnInitialise</c> — is running
///         somebody's game to find out what order it runs things in.
///     </para>
///     <para>
///         ⚠ <b>What it therefore cannot say.</b> Two things, both stated in the report rather than
///         guessed at: which systems run <em>concurrently</em>, because that comes from
///         <see cref="SystemAccess" /> and <see cref="IDeclaredAccess" /> is an instance property;
///         and whether a service will be registered, because nothing has registered anything yet.
///     </para>
/// </remarks>
internal static class SystemsRunner {
    /// <summary>Examines the frame the named assemblies declare.</summary>
    /// <param name="assemblyPaths">The built game assemblies, as <c>--assembly</c> gave them.</param>
    /// <returns>What it found, in the order a report should print it.</returns>
    /// <remarks>
    ///     ⚠ <b>The findings come back in reading order, not worst-first.</b>
    ///     <see cref="DoctorRunner.Examine" /> sorts by health because its findings are a list of
    ///     unrelated checks; here the order <em>is</em> the answer, and sorting it would destroy the
    ///     one thing the command was asked for.
    /// </remarks>
    public static List<Finding> Examine(IReadOnlyList<string> assemblyPaths) {
        ArgumentNullException.ThrowIfNull(assemblyPaths);

        var findings = new List<Finding>();
        var assemblies = Load(assemblyPaths, findings);

        if (assemblies.Count == 0) {
            return findings;
        }

        // Only the named assemblies. GameSystemRegistry is process-wide and this tool has the whole
        // engine loaded, so an unfiltered read would report Vixen's own declarations as the
        // project's — the same reason PlayMode passes the project's assembly as `where`.
        var declared = GameSystemRegistry.Declared
            .Where(declaration => assemblies.Contains(declaration.SystemType.Assembly))
            .ToArray();

        Frame(declared, findings);
        Services(declared, findings);
        Undeclared(assemblies, declared, findings);

        return findings;
    }

    /// <summary>Loads each assembly, and says so when one is not where it was said to be.</summary>
    /// <remarks>
    ///     ⚠ <b>A path that does not exist is broken here and skipped everywhere else.</b>
    ///     <see cref="GameAssemblies" /> skips it on purpose — the first build of a project imports
    ///     its assets before the compiler has produced anything to load. But a doctor that quietly
    ///     examined nothing and printed a clean report would be the exact failure this command is
    ///     supposed to catch, so the same event has to mean something different here.
    /// </remarks>
    static List<Assembly> Load(IReadOnlyList<string> paths, List<Finding> findings) {
        var missing = paths.Where(path => !File.Exists(Path.GetFullPath(path))).ToArray();

        foreach (var path in missing) {
            findings.Add(
                new(
                    Health.Broken,
                    "assembly",
                    $"there is nothing at '{path}'. Build the project first; a frame cannot be read "
                    + "from an assembly that does not exist yet."
                )
            );
        }

        GameAssemblies.Load(
            paths,
            complaint => findings.Add(new(Health.Broken, "assembly", $"could not be loaded — {complaint}"))
        );

        var loaded = new List<Assembly>();

        foreach (var path in paths) {
            var full = Path.GetFullPath(path);

            if (!File.Exists(full)) {
                continue;
            }

            try {
                // Idempotent for a path already loaded a moment ago: this hands back the same
                // Assembly rather than a second copy of it. GameAssemblies did the loading and the
                // module constructors; this only needs the handle, to filter the registry by.
                loaded.Add(Assembly.LoadFrom(full));
            } catch (Exception failure) when (failure is BadImageFormatException
                                                 or FileLoadException
                                                 or FileNotFoundException) {
                findings.Add(new(Health.Broken, "assembly", $"'{path}' could not be read: {failure.Message}"));
            }
        }

        if (loaded.Count == 0 && missing.Length == 0) {
            findings.Add(new(Health.Broken, "assembly", "nothing was loaded, so there is no frame to look at."));
        }

        return loaded;
    }

    /// <summary>The resolved run order, by phase, and every ordering attribute that does nothing.</summary>
    static void Frame(IReadOnlyList<GameSystemDeclaration> declared, List<Finding> findings) {
        if (declared.Count == 0) {
            findings.Add(
                new(
                    Health.Concerning,
                    "frame",
                    "no system in these assemblies carries [GameSystem], so nothing here declares a "
                    + "frame. Systems added by hand in Game.OnInitialise still run — this cannot see "
                    + "them, because seeing them would mean running the game."
                )
            );

            return;
        }

        var types = declared.Select(declaration => declaration.SystemType).ToArray();
        SystemPlan plan;

        try {
            plan = SystemGraph.Plan(types);
        } catch (InvalidOperationException cycle) {
            // The sort's own refusal names every system in the cycle. Repeating it would say it
            // worse, and this is the one input for which there is no order to print.
            findings.Add(new(Health.Broken, "frame", cycle.Message));
            return;
        }

        findings.Add(
            new(
                Health.Fine,
                "frame",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{plan.Placements.Count} declared, in {plan.Phases.Count()} of the "
                    + $"{Enum.GetValues<SystemPhase>().Length} phases."
                )
            )
        );

        foreach (var phase in plan.Phases) {
            var members = plan.InPhase(phase);

            foreach (var placement in members) {
                findings.Add(
                    new(
                        Health.Fine,
                        string.Create(CultureInfo.InvariantCulture, $"{phase} {placement.Order + 1}/{members.Count}"),
                        placement.Name
                    )
                );
            }
        }

        foreach (var problem in plan.Unsatisfied) {
            findings.Add(new(Health.Concerning, "ordering", problem));
        }
    }

    /// <summary>What each declared system asks for, and the one kind of request that can never be met.</summary>
    /// <remarks>
    ///     ⚠ <b>The value-type case is reported, not solved.</b> <c>ServiceRegistry.Add&lt;T&gt;</c>
    ///     is constrained <c>where T : class</c>, so a constructor parameter that is a struct is a
    ///     service nothing can ever register — the declaration compiles, the generator emits the
    ///     factory, and <c>AddDeclaredSystems</c> names the system as missing every single run. What
    ///     to do about that is an open decision, not this command's to take.
    /// </remarks>
    static void Services(IReadOnlyList<GameSystemDeclaration> declared, List<Finding> findings) {
        if (declared.Count == 0) {
            return;
        }

        foreach (var declaration in declared.OrderBy(one => one.Name, StringComparer.Ordinal)) {
            findings.Add(
                new(
                    Health.Fine,
                    declaration.Name,
                    declaration.Requires.Count == 0
                        ? "needs no services."
                        : "needs " + string.Join(", ", declaration.Requires.Select(type => type.Name)) + "."
                )
            );

            foreach (var required in declaration.Requires.Where(type => type.IsValueType)) {
                findings.Add(
                    new(
                        Health.Broken,
                        declaration.Name,
                        $"asks for a {required.Name}, which is a value type. ServiceRegistry.Add<T> is "
                        + "constrained to reference types, so nothing can ever provide it and this "
                        + "system will be named as missing on every run."
                    )
                );
            }
        }

        findings.Add(
            new(
                Health.Concerning,
                "services",
                "whether the rest of these are registered is not knowable here. The registry is built "
                + "when the game runs, and this read the assembly without running it — "
                + "`AddDeclaredSystems` names any that are absent, at startup."
            )
        );
    }

    /// <summary>Systems that are in the assembly and not in the declared frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Concerning rather than broken, because hand-registration is supported.</b>
    ///     <c>[GameSystem]</c> is additive: a project may go on constructing its systems in
    ///     <c>Game.OnInitialise</c>, and one that does is not wrong. What it is, is invisible — to
    ///     this command, to the editor's Play mode, and to every other tool — so the honest finding
    ///     is "either it is added by hand or it never runs, and nothing here can tell which".
    /// </remarks>
    static void Undeclared(
        IReadOnlyList<Assembly> assemblies,
        IReadOnlyList<GameSystemDeclaration> declared,
        List<Finding> findings
    ) {
        var known = declared.Select(declaration => declaration.SystemType).ToHashSet();
        var strays = new List<Type>();

        foreach (var assembly in assemblies) {
            Type?[] types;

            try {
                types = assembly.GetTypes();
            } catch (ReflectionTypeLoadException partial) {
                // The types that did load are still worth reporting on. An assembly referencing
                // something absent is a real situation and a report about the half that loaded beats
                // no report at all — as long as it says so, which the finding below does.
                types = partial.Types;

                findings.Add(
                    new(
                        Health.Concerning,
                        assembly.GetName().Name ?? "assembly",
                        "some of its types could not be loaded, so this list may be short. A "
                        + "reference it was built against is probably not beside it."
                    )
                );
            }

            strays.AddRange(
                types.Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false }
                        && typeof(ISystem).IsAssignableFrom(type)
                        && !known.Contains(type)
                    )!
            );
        }

        if (strays.Count == 0) {
            return;
        }

        foreach (var stray in strays.OrderBy(type => type.Name, StringComparer.Ordinal)) {
            findings.Add(
                new(
                    Health.Concerning,
                    stray.Name,
                    "is a system and carries no [GameSystem], so nothing here puts it in a frame. "
                    + "Either it is added by hand in the game's own code — which this cannot see — or "
                    + "it never runs."
                )
            );
        }
    }
}
