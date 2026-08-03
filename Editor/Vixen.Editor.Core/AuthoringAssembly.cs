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
/// </remarks>
/// <param name="Marker">
///     Any type in the assembly. A type rather than an <c>Assembly</c> because the caller has one to
///     hand and writing <c>typeof(AudioSource)</c> says which components are meant, where
///     <c>typeof(X).Assembly</c> says the same thing one indirection later.
/// </param>
public sealed record AuthoringAssembly(Type Marker) {
    /// <summary>Runs the declaring assembly's module initializers, if they have not run.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Module</c> rather than a bare <c>typeof</c>.</b> A bare <c>typeof</c> is a token
    ///     load the JIT can satisfy without running the module's initializer, which is precisely the
    ///     thing being forced here — asking for the module handle makes it concrete. Idempotent: the
    ///     runtime runs a module constructor once however many times it is asked.
    /// </remarks>
    public void Touch() => RuntimeHelpers.RunModuleConstructor(Marker.Module.ModuleHandle);
}
