// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui.Documents;
using Vixen.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § D6's <c>ServiceReachTest</c>: a context service's verb that nothing outside the service calls.</summary>
/// <remarks>
///     <para>
///         <b>CLAUDE.md's commonest defect, applied to the new surface</b> (#1296): every public method
///         on a published context service is a use case, and one that no command, menu, module or panel
///         calls is a finished thing nothing calls.
///     </para>
///     <para>
///         ⚠ <b>A binding, not a name (#1517).</b> This used to count <c>.Name</c> anywhere in the editor's
///         source as a caller, so <c>IWorkspace.Open</c> was reached by every other <c>Open</c> and the
///         list could only err green. It now reads the IL of every editor assembly and counts an
///         instruction whose method operand <i>is</i> the verb — <c>call</c>, <c>callvirt</c>,
///         <c>newobj</c>, <c>ldftn</c> or <c>ldvirtftn</c>, so a method group handed to a command counts —
///         either on the interface or on a class implementing it. A <c>.vxml</c>'s <c>@code</c> is compiled
///         into its assembly, so markup is read the same way.
///     </para>
///     <para>
///         ⚠ <b>"Outside the service" is outside every class implementing it</b>, nested and
///         compiler-generated types included — so a verb only the implementation calls on itself is not
///         reached. The name match excused the interface's own file instead, which for
///         <c>ISceneEditing</c> was the interface alone and for a self-call was nothing.
///     </para>
///     <para>
///         ⚠ <b>Shrink-only, the <c>CheckWhitespace</c> shape.</b> <see cref="ExemptFile" /> lists what is
///         unreached today; a new unreached method fails, and so does a listed one that has gained a
///         caller. Regenerate with <c>VIXEN_REGENERATE=1</c> and read the diff — a line added is a verb
///         nobody can reach.
///     </para>
/// </remarks>
public sealed class ServiceReachTests {
    /// <summary>The committed list of methods nothing reaches yet, relative to the repository root.</summary>
    const string ExemptFile = "Editor/Vixen.Editor.App.Tests/ServiceReach.txt";

    /// <summary>The context services doc 50 § D2 publishes, whose methods are use cases.</summary>
    static readonly Type[] Services = [
        typeof(ISceneEditing),
        typeof(IContentLibrary),
        typeof(ISourceControl),
        typeof(IProjectSession),
        typeof(IPlaySession),
        typeof(IPlayerDelivery),
        typeof(IDocumentHost),
        typeof(IWorkspace)
    ];

    /// <summary>The production projects under <c>Editor/</c> whose assemblies this test cannot load, and why that is safe.</summary>
    /// <remarks>
    ///     ⚠ <b>Checked, not trusted:</b> each must name none of <see cref="Services" /> in its source, which
    ///     is what calling a verb without this walk seeing it would take. One that starts to is a failure
    ///     saying to reference it from this project.
    /// </remarks>
    static readonly string[] Unwalked = [
        // The executable: it composes the application and names no service.
        "Vixen.Editor.Host",

        // Editor plugins with no reference from this project.
        "Vixen.Editor.Gameplay.Loot",
        "Vixen.Editor.Gameplay.Quests"
    ];

    [Fact]
    public void Every_context_service_verb_has_a_caller_outside_the_service() {
        var root = RepositoryFiles.Root;
        var walk = Walk.Of(EditorAssemblies(root));

        // The instrument first: a walk that read no IL reaches nothing and would agree with a list of
        // everything. `ISceneEditing.Group` is Ctrl+G, registered by `SceneModule` over the interface.
        Assert.True(walk.Methods > 10_000, $"only {walk.Methods} method bodies read — did the walk load the editor?");
        Assert.True(walk.Unresolved < walk.References / 100, $"{walk.Unresolved} of {walk.References} method operands did not resolve");

        var measured = new List<string>();
        var reached = new List<string>();

        foreach (var service in Services) {
            var implementations = walk.Types.Where(type => type is { IsInterface: false } && service.IsAssignableFrom(type)).ToList();

            Assert.NotEmpty(implementations);

            foreach (var verb in Verbs(service)) {
                var keys = new HashSet<(Guid, int)> { Key(verb) };

                foreach (var implementation in implementations) {
                    var map = implementation.GetInterfaceMap(service);
                    var index = Array.IndexOf(map.InterfaceMethods, verb);

                    if (index >= 0) {
                        keys.Add(Key(map.TargetMethods[index]));
                    }
                }

                var callers = keys.SelectMany(walk.CallersOf).Where(caller => !Within(caller, implementations));
                (callers.Any() ? reached : measured).Add(Line(service, verb));
            }
        }

        Assert.Contains("ISceneEditing.Group", reached);
        Assert.True(reached.Count > 50, $"only {reached.Count} verbs reached — did the walk read the tree?");

        var path = Path.Combine(root, ExemptFile.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable("VIXEN_REGENERATE") == "1") {
            File.WriteAllText(path, Render(measured), new UTF8Encoding(false));
        }

        var recorded = File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0 && !line.StartsWith('#')).ToList()
            : [];

        var unreached = measured.Except(recorded).ToList();
        var stale = recorded.Except(measured).ToList();

        Assert.True(
            unreached.Count == 0 && stale.Count == 0,
            $"""
             ⚠ A context service verb nothing outside its service calls — give it a caller (a command, a
             menu line, a module) or take it off the interface:
             {string.Join("\n", unreached.Select(line => "  " + line))}

             Listed in {ExemptFile} and reached now — delete the line (the list may only shrink):
             {string.Join("\n", stale.Select(line => "  " + line))}
             """
        );
    }

    /// <summary>The projects this walk cannot load name no context service, so nothing in them can call a verb.</summary>
    [Fact]
    public void The_projects_the_walk_cannot_load_name_no_context_service() {
        var root = RepositoryFiles.Root;
        var names = new Regex(@"\b(" + string.Join('|', Services.Select(service => Regex.Escape(service.Name))) + @")\b", RegexOptions.CultureInvariant);

        foreach (var project in Unwalked) {
            var directory = Path.Combine(root, "Editor", project);

            Assert.True(Directory.Exists(directory), $"{project} is listed as unwalked and does not exist — take it off the list");

            var naming = RepositoryFiles.Files(directory, "*.cs", "*.vxml")
                .Where(file => names.IsMatch(File.ReadAllText(file)))
                .ToList();

            Assert.True(
                naming.Count == 0,
                $"{project} names a context service and this walk cannot read its IL — reference it from this test project:\n"
                + string.Join("\n", naming)
            );
        }
    }

    /// <summary>Every production assembly under <c>Editor/</c>, and a failure naming any project neither loaded nor excused.</summary>
    static IReadOnlyList<Assembly> EditorAssemblies(string root) {
        var projects = Directory.GetDirectories(Path.Combine(root, "Editor"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(static name => !name.EndsWith(".Tests", StringComparison.Ordinal)
                && !name.EndsWith(".Generator", StringComparison.Ordinal)
                && name != "Vixen.Editor.Testing"
                && File.Exists(Path.Combine(RepositoryFiles.Root, "Editor", name, name + ".csproj")))
            .ToList();

        var here = Path.GetDirectoryName(typeof(ServiceReachTests).Assembly.Location)!;
        var loaded = new List<Assembly>();
        var missing = new List<string>();

        foreach (var project in projects) {
            if (Unwalked.Contains(project)) {
                continue;
            }

            if (!File.Exists(Path.Combine(here, project + ".dll"))) {
                missing.Add(project);
                continue;
            }

            loaded.Add(Assembly.Load(new AssemblyName(project)));
        }

        Assert.True(missing.Count == 0, "Editor projects this walk neither reads nor excuses:\n" + string.Join("\n", missing));

        return loaded;
    }

    /// <summary>The service's own methods, without accessors or event plumbing.</summary>
    static IEnumerable<MethodInfo> Verbs(Type service) =>
        service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ThenBy(static method => Signature(method), StringComparer.Ordinal);

    /// <summary>Whether a type is one of the implementations, or nested in one — a lambda's closure, a state machine.</summary>
    static bool Within(Type type, List<Type> implementations) {
        for (Type? at = type; at is not null; at = at.DeclaringType) {
            if (implementations.Contains(at)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The line a verb is listed under: its name, and its parameters only when the name is overloaded.</summary>
    static string Line(Type service, MethodInfo verb) {
        var overloaded = service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(method => method.Name == verb.Name) > 1;

        return service.Name + "." + verb.Name + (overloaded ? "(" + Signature(verb) + ")" : string.Empty);
    }

    static string Signature(MethodInfo method) => string.Join(", ", method.GetParameters().Select(static parameter => parameter.ParameterType.Name));

    /// <summary>What identifies a method across every instantiation of it: its module and its definition's token.</summary>
    /// <remarks>A method of a constructed generic type, and a constructed generic method, carry their definition's token.</remarks>
    static (Guid, int) Key(MethodBase method) => (method.Module.ModuleVersionId, method.MetadataToken);

    static string Render(IEnumerable<string> lines) {
        var text = new StringBuilder()
            .Append("# Doc 50 § D6's ServiceReachTest: context service verbs nothing outside the service calls yet.\n")
            .Append("# Shrink-only. Regenerate with VIXEN_REGENERATE=1 on ServiceReachTests, and read the diff.\n\n");

        foreach (var line in lines.Order(StringComparer.Ordinal)) {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Every method operand in every method body of a set of assemblies, keyed by the method it names.</summary>
    sealed class Walk {
        static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(static code => code.Value);

        const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        readonly Dictionary<(Guid, int), HashSet<Type>> callers = [];

        /// <summary>Every type in the assemblies, nested and compiler-generated ones included.</summary>
        public List<Type> Types { get; } = [];

        /// <summary>How many method bodies were read.</summary>
        public int Methods { get; private set; }

        /// <summary>How many method operands were met.</summary>
        public int References { get; private set; }

        /// <summary>How many of them could not be resolved to a method.</summary>
        public int Unresolved { get; private set; }

        public static Walk Of(IEnumerable<Assembly> assemblies) {
            var walk = new Walk();

            foreach (var assembly in assemblies) {
                Type[] types;

                try {
                    types = assembly.GetTypes();
                } catch (ReflectionTypeLoadException exception) {
                    types = [.. exception.Types.OfType<Type>()];
                }

                foreach (var type in types) {
                    walk.Types.Add(type);

                    foreach (var method in type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything))) {
                        walk.Read(type, method);
                    }
                }
            }

            return walk;
        }

        /// <summary>The types whose code names the method, each the type the method body is declared in.</summary>
        public IEnumerable<Type> CallersOf((Guid, int) key) => callers.TryGetValue(key, out var found) ? found : [];

        void Read(Type type, MethodBase method) {
            if (method.GetMethodBody()?.GetILAsByteArray() is not { } il) {
                return;
            }

            Methods++;

            var typeArguments = type.IsGenericType ? type.GetGenericArguments() : null;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

            for (var offset = 0; offset < il.Length;) {
                OpCode code;

                if (il[offset] == 0xFE) {
                    code = OpCodesByValue[unchecked((short)(0xFE00 | il[offset + 1]))];
                    offset += 2;
                } else {
                    code = OpCodesByValue[il[offset]];
                    offset += 1;
                }

                if (code.OperandType == OperandType.InlineMethod) {
                    References++;

                    var token = BitConverter.ToInt32(il, offset);

                    try {
                        var named = method.Module.ResolveMethod(token, typeArguments, methodArguments);

                        if (named is not null) {
                            if (!callers.TryGetValue(Key(named), out var set)) {
                                callers[Key(named)] = set = [];
                            }

                            set.Add(type);
                        }
                    } catch (Exception exception) when (exception is ArgumentException or TypeLoadException or FileNotFoundException or BadImageFormatException or MissingMethodException) {
                        Unresolved++;
                    }
                }

                offset += code.OperandType switch {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
                    _ => 4
                };
            }
        }
    }
}
