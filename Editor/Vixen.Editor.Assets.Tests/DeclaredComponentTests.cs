// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Engine.Scenes;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     Every component a scene may name, in every assembly this build can reach, is one a scene can
///     actually name.
/// </summary>
/// <remarks>
///     <para>
///         <b>The family, rather than the four instances of it.</b> A component is scene data when it
///         carries <c>[Component]</c> and <c>[DataContract]</c>; it becomes <em>loadable</em> when its
///         assembly also runs <c>Vixen.Engine.Generators</c>, which emits the
///         <c>[ModuleInitializer]</c> that declares it. Those are two facts in two files, and nothing
///         checks that the second follows the first — a missing analyzer reference produces no code,
///         so there is nothing for the compiler to reject and no warning to notice. It surfaces at a
///         scene load, in a project, as <c>SceneComponentException</c>.
///     </para>
///     <para>
///         <c>Vixen.Rendering.Water</c> and <c>Vixen.Water.Physics</c> shipped that way and a full
///         suite stayed green, because every other test names its components in C#. This is the check
///         that would have caught them the day either assembly was added, and it costs one reflection
///         pass over the output directory.
///     </para>
///     <para>
///         ⚠ <b>The scope is what this test project's own output folder contains</b>, which is its
///         transitive project closure and not the solution. That is a real limit: an assembly nothing
///         here references is not examined, and the fix is a reference rather than a cleverer scan —
///         a check that loaded the whole repository's binaries would have to know where they are, and
///         would go stale differently. What it does guarantee is that the closure the editor's asset
///         pipeline is built from stays whole, which is the closure a scene is compiled against.
///     </para>
/// </remarks>
public sealed class DeclaredComponentTests {
    /// <summary>
    ///     Assemblies whose components are deliberately not scene data, keyed by why.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Empty, and it should stay that way.</b> The exclusion this check relies on is
    ///     structural rather than listed: a component that is not scene data carries no
    ///     <c>[DataContract]</c> — <c>PhysicsBody</c> holds a handle and is excluded by construction —
    ///     so there is nothing for a denylist to hold. It is named here so that a future entry has to
    ///     be argued for in the same place it is added.
    /// </remarks>
    static readonly HashSet<string> Exempt = new(StringComparer.Ordinal);

    public static TheoryData<string> SceneComponents {
        get {
            var data = new TheoryData<string>();

            foreach (var component in Discover()) {
                data.Add(component.AssemblyQualifiedName!);
            }

            return data;
        }
    }

    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One case per component rather than one assertion over the set.</b> A single test
    ///         would report the first failure and hide the rest, and these fail in groups — one
    ///         missing reference undeclares every component in its assembly at once, and the useful
    ///         question is which assemblies rather than which type.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The declaring module is initialised here and not in <see cref="SceneComponents" />,
    ///         which is the part that is easy to get wrong.</b> A theory row of strings is serializable,
    ///         so the runner records the rows during discovery and replays them at execution without
    ///         calling the member data again — and discovery and execution are different processes.
    ///         Anything the sweep did to a registry is therefore not there when the assertion runs, and
    ///         a check written that way fails on every component including the correctly wired ones.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(SceneComponents))]
    public void AComponentAnAssemblyDeclaresAsSceneDataIsOneASceneCanName(string qualifiedName) {
        var component = Type.GetType(qualifiedName)
            ?? throw new InvalidOperationException($"'{qualifiedName}' could not be resolved.");

        Initialize(component.Assembly);

        Assert.True(
            SceneComponentRegistry.TryGet(component, out var binder),
            $"'{component}' carries [Component] and [DataContract] but nothing declared it to "
            + $"SceneComponentRegistry, so a scene naming it cannot be loaded. '{component.Assembly.GetName().Name}' "
            + "needs a ProjectReference to Vixen.Engine.Generators with OutputItemType=\"Analyzer\" — analyzers do "
            + "not flow transitively, and a missing one emits no code and so no error."
        );

        Assert.Equal(component, binder.ComponentType);
    }

    /// <summary>That the sweep found the assemblies it is supposed to be watching.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the check passes vacuously.</b> A discovery pass that silently found
    ///     nothing — a renamed attribute, an output directory laid out differently, a load that
    ///     started throwing — is a green test that has stopped testing, and that is exactly the shape
    ///     of the failure it exists to catch.
    /// </remarks>
    [Fact]
    public void TheSweepReachesTheAssembliesItIsFor() {
        var assemblies = Discover().Select(component => component.Assembly.GetName().Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Vixen.Rendering", assemblies);
        Assert.Contains("Vixen.Rendering.Terrain", assemblies);
        Assert.Contains("Vixen.Rendering.Water", assemblies);
        Assert.Contains("Vixen.Water.Physics", assemblies);
    }

    /// <summary>Runs an assembly's <c>[ModuleInitializer]</c>, which is what declares its components.</summary>
    /// <remarks>
    ///     ⚠ <b>Loading an assembly does not run one.</b> A module initializer runs on first access to
    ///     one of the module's types, and a reflection walk never does that — so without this every
    ///     component would look undeclared, including the ones that are wired correctly. The generated
    ///     initializer only enqueues; the registry drains on the first lookup, so the order here is
    ///     initialise, then ask.
    /// </remarks>
    static void Initialize(Assembly assembly) {
        foreach (var module in assembly.GetModules()) {
            RuntimeHelpers.RunModuleConstructor(module.ModuleHandle);
        }
    }

    /// <summary>Every <c>[Component]</c> <c>[DataContract]</c> type in the loadable closure.</summary>
    static IEnumerable<Type> Discover() {
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "Vixen.*.dll").Order(StringComparer.Ordinal)) {
            Assembly assembly;

            try {
                assembly = Assembly.LoadFrom(file);
            } catch (BadImageFormatException) {
                // A native payload under a managed name. Nothing declares components.
                continue;
            }

            if (Exempt.Contains(assembly.GetName().Name ?? string.Empty)) {
                continue;
            }

            Type?[] types;

            try {
                types = assembly.GetTypes();
            } catch (ReflectionTypeLoadException failure) {
                types = failure.Types;
            }

            foreach (var type in types) {
                if (type is null || !type.IsValueType) {
                    continue;
                }

                if (type.GetCustomAttribute<ComponentAttribute>() is null) {
                    continue;
                }

                if (type.GetCustomAttribute<DataContractAttribute>() is null) {
                    continue;
                }

                // ⚠ A generic component cannot be declared and the generator says so with VXS0401,
                // which is a build warning rather than a silent omission — so it is not this check's.
                if (type.IsGenericTypeDefinition) {
                    continue;
                }

                yield return type;
            }
        }
    }
}
