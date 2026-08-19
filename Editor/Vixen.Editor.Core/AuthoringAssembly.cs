// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Editor.Core;

/// <summary>An assembly that declares components or behaviours, named so its declarations run.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D5's fourth row, and F11's replacement.</b> That was three hardcoded
///         <c>RunModuleConstructor</c> calls inside the component panel — a list, in the application,
///         of which subsystems exist — so a plugin whose components lived in a runtime assembly of its
///         own could not appear in Add ▸ at all. This is a contribution: the application declares the
///         subsystems it ships and a module declares its own, through the same registry.
///     </para>
///     <para>
///         ⚠ <b>A module initializer does not run until something touches the module, and that is the
///         whole problem.</b> A component registers itself to <c>SceneComponentRegistry</c> from a
///         <c>[ModuleInitializer]</c> the generator emits — but the runtime is entitled to defer it
///         indefinitely, so a registry read during the editor's construction sees whatever happened to
///         have been loaded by then. What that looked like was an Add Component menu offering
///         <c>Camera</c> and nothing else, with every component drawn in the viewport arriving a
///         second later and never being offered.
///     </para>
///     <para>
///         ⚠ <b>A declaration rather than a scan, which is <c>SceneComponentRegistry</c>'s own
///         argument restated.</b> Walking the output directory reads metadata a trimmed publish has
///         already deleted, and it would make "what can be added" a question with a different answer
///         in the editor, in a worker process and in a shipped game. What is declared is what somebody
///         wrote down.
///     </para>
///     <para>
///         ⚠ <b>This does not make the components appear — it makes them appear <i>on time</i>.</b> The
///         panel re-reads the registries on every enumeration, so an assembly touched by anything at
///         all shows up eventually. The failure this prevents is the one where nothing ever touches it:
///         an audio subsystem the editor references and never calls into is a subsystem whose
///         components exist in the build and in no menu.
///     </para>
///     <para>
///         ⚠ <b>Measured: the <c>Type</c> in the parameter is what does the work, and <see cref="Touch" />
///         is belt to its braces.</b> <c>OutOfTreePluginTests</c> compiles a library nothing else in the
///         process has ever heard of, gives it a <c>[ModuleInitializer]</c> that declares a component,
///         and loads it beside an out-of-tree plugin. Naming <i>any</i> type in it from that plugin's
///         <c>Activate</c> — a bare <c>typeof</c>, on a type that is not the component and is not the
///         marker — registers the component; naming none of them registers nothing. And gutting
///         <see cref="Touch" /> to an empty body leaves all 527 tests in <c>Vixen.Editor.App.Tests</c>
///         passing, <c>ComponentTests.The_subsystems_that_declare_components_are_loaded_before_the_list_is_read</c>
///         included. So on this runtime a module's initializers have run by the time anybody is
///         holding a <c>Type</c> out of it — and nobody can build one of these without holding one.
///     </para>
///     <para>
///         ⚠ <b>Which is why the declaration is still worth writing and the call is still worth
///         making.</b> The declaration is the part that survives: it is the list of assemblies
///         somebody named, and deleting a line from it deletes a component from Add ▸ whatever runs
///         the initializer. The call stays because what was measured is one runtime's behaviour and
///         not a guarantee of the language — <see cref="Touch" /> costs one call that the runtime
///         answers by doing nothing.
///     </para>
///     <para>
///         ⚠ <b>The contrast that makes it clear which of the two an explicit run is needed for:
///         <c>ProjectAssemblies.Load</c> holds an <c>Assembly</c> and nothing else.</b> It calls
///         <c>RunModuleConstructor</c> on the manifest module because nothing in that path ever
///         names a type in it — "loading is not touching", as it says. Here the caller has named a
///         type by the time there is a record to add, so the touching has already happened.
///     </para>
/// </remarks>
/// <param name="Marker">
///     Any type in the assembly. A type rather than an <c>Assembly</c> because the caller has one to
///     hand and writing <c>typeof(AudioSource)</c> says which components are meant, where
///     <c>typeof(X).Assembly</c> says the same thing one indirection later.
/// </param>
public sealed record AuthoringAssembly(Type Marker) {
    /// <summary>Runs the declaring assembly's module initializers, if they have not run.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>Module</c> rather than a bare <c>typeof</c>.</b> A bare <c>typeof</c> is a token
    ///         load the JIT is entitled to satisfy without running the module's initializer, which is
    ///         what asking for the module handle makes concrete. Idempotent: the runtime runs a module
    ///         constructor once however many times it is asked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"Entitled to" is not "observed to", and what was observed is the initializer
    ///         running anyway.</b> See the type's remarks: every attempt to catch this method doing
    ///         work found the work already done by the <c>typeof</c> in its own caller's argument.
    ///         Do not write a test that turns on this having been called — there is nothing to see.
    ///     </para>
    /// </remarks>
    public void Touch() => RuntimeHelpers.RunModuleConstructor(Marker.Module.ModuleHandle);
}
