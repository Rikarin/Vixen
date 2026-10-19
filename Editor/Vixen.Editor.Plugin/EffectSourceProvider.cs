// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>Raven a module compiles out of a project's assets, for the editor's own effect compilation.</summary>
/// <remarks>
///     <para>
///         <b>#1300's seam.</b> The editor compiles the shader library together with every Raven source a
///         project contributes, so a material naming a shader graph resolves. It used to fetch the graphs
///         by calling <c>ShaderGraphSources.All</c>, which put the shader graph's compiler in the
///         application's reference closure through the import pipeline. A module that turns an asset
///         format into Raven publishes one of these with <see cref="PluginContext.Publish{T}" />, and the
///         editor asks it on every effect rebuild — so an unloaded module stops contributing.
///     </para>
///     <para>
///         ⚠ <b>One per editor, because <see cref="PluginServices" /> holds one service per type.</b>
///         The shader graph is the only format that compiles to Raven today; a second would need this
///         to become a registry contribution.
///     </para>
/// </remarks>
public interface IEffectSourceProvider {
    /// <summary>Adds the sources a project's assets compile to, and what those that would not had to say.</summary>
    /// <param name="assets">The project's <c>Assets/</c> directory.</param>
    /// <param name="sources">Where each compiled source goes: its path, for diagnostics, and its Raven text.</param>
    /// <param name="refusals">Where each file that would not compile says why, one line per file, starting with its name.</param>
    void Collect(string assets, ICollection<(string Path, string Text)> sources, ICollection<string> refusals);
}
