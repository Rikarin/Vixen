// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>What a plugin implements. One public type per assembly, with a parameterless constructor.</summary>
/// <remarks>
///     <para>
///         <b>Two methods, and the second is usually empty.</b> Everything registered through
///         <see cref="PluginContext" /> is undone for the plugin when it is unloaded, so a plugin
///         that only adds commands, panels and menu entries needs no teardown at all.
///         <see cref="Deactivate" /> is for what the context cannot know about: a file watcher, a
///         socket, a background thread.
///     </para>
///     <para>
///         ⚠ <b>The constructor runs before <see cref="Activate" /> and should do nothing.</b> A
///         plugin whose constructor throws is a plugin the loader cannot report against as
///         precisely, and one whose constructor touches the editor is one that ran before its
///         dependencies did.
///     </para>
/// </remarks>
/// <example>
///     <code language="csharp">
///     public sealed class TerrainPlugin : IEditorPlugin {
///         public void Activate(PluginContext context) {
///             context.AddCommand(
///                 "terrain.sculpt",
///                 new StringId("terrain.command.sculpt", "Sculpt Terrain"),
///                 () => { /* … */ }
///             );
///         }
///     }
///     </code>
/// </example>
public interface IEditorPlugin {
    /// <summary>Adds everything the plugin contributes.</summary>
    /// <param name="context">The editor, and the scope that will undo what is registered here.</param>
    /// <remarks>
    ///     <para>
    ///         Runs on the frame thread, after every plugin this one declared a dependency on has
    ///         activated. Throwing is allowed and is how a plugin refuses a host it cannot work
    ///         with: the loader rolls back whatever was registered before the throw, unloads the
    ///         assembly and reports it.
    ///     </para>
    /// </remarks>
    void Activate(PluginContext context);

    /// <summary>Releases what the registration scope does not know about.</summary>
    /// <remarks>
    ///     ⚠ <b>Anything still holding a reference into this assembly keeps it loaded.</b> A timer
    ///     that was not stopped, a static event that was not unsubscribed from, a thread still
    ///     running — each of them turns an unload into a leak that nothing reports, because the
    ///     runtime's answer to "this context cannot be collected" is silence.
    /// </remarks>
    void Deactivate() { }
}
