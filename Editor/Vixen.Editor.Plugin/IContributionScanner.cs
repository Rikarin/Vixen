// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace Vixen.Editor.Plugin;

/// <summary>Reads a loaded assembly's declarations and registers what it finds.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D3, and the seam that makes the attributes symmetric.</b> A plugin's assembly
///         and a project's script assembly are the same kind of thing — one discrete assembly the
///         editor has just loaded — and both should get the same treatment from
///         <c>[EditorMenu]</c>, <c>[CustomInspector]</c>, <c>[CustomDrawer]</c> and
///         <c>[EditorTool]</c>. Before this, the first worked only in a script and the other three
///         nowhere, which is an asymmetry nobody could have predicted from the attributes.
///     </para>
///     <para>
///         ⚠ <b>An interface here and the implementation elsewhere, and that is the whole reason it
///         exists.</b> The attributes name <c>CustomInspector</c>, <c>DrawerRegistry</c> and
///         <c>SceneTool</c> — types in <c>Vixen.Editor.Inspector</c> and
///         <c>Vixen.Editor.SceneView</c> — and this assembly must not reference either. That is P2's
///         rule: a method per contribution kind on <c>PluginContext</c> would put the whole kind list
///         in the contract assembly and make it reference every feature assembly that owns one, which
///         is F2's problem one layer down. What crosses this boundary is a <c>PluginContext</c> and an
///         <c>Assembly</c>, and neither is a feature type.
///     </para>
///     <para>
///         ⚠ <b>Everything a scanner registers goes through the context it is handed</b>, so it is in
///         the plugin's own registration scope and goes away when the plugin does. A scanner that
///         registered anywhere else would be the one leak this whole arrangement is built to prevent.
///     </para>
/// </remarks>
public interface IContributionScanner {
    /// <summary>Registers whatever an assembly declares.</summary>
    /// <param name="context">The plugin's context, which is what everything is registered through.</param>
    /// <param name="assembly">The assembly to read.</param>
    /// <remarks>
    ///     ⚠ <b>Throwing refuses the plugin.</b> It is called inside the same <c>try</c> that runs
    ///     <c>Activate</c>, so a declaration the editor cannot honour — a wrong signature, a missing
    ///     constructor — rolls the plugin back and becomes a diagnostic naming it, rather than a
    ///     half-registered plugin nobody can see is half-registered.
    /// </remarks>
    void Scan(PluginContext context, Assembly assembly);
}
