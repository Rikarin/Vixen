// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace Vixen.Editor.Core;

/// <summary>The project's own C#, as <see cref="IProjectSession.Code" /> reports it: which project builds it and what is loaded.</summary>
/// <remarks>
///     <para>
///         <b>Read-only, and that is the point of it being an interface.</b> The application's
///         <c>ProjectAssemblies</c> builds, loads and unloads; a module that wants the code rebuilt calls
///         <see cref="IProjectSession.BuildCode" />, which is the one door that takes the scene's authored
///         behaviours off and puts them back around the reload. A module handed the loader itself could
///         unload the assembly under a scene still holding its instances.
///     </para>
///     <para>
///         ⚠ <b><see cref="Loaded" /> lives in a collectible context.</b> Holding the assembly — or any
///         type, delegate or instance out of it — past the next <see cref="IProjectSession.CodeBuilt" />
///         keeps the previous build in memory for ever; read it when it is needed and let it go.
///     </para>
/// </remarks>
public interface IProjectCode {
    /// <summary>The project's own <c>.csproj</c>, or <see langword="null" /> if the root has none — or more than one.</summary>
    string? Project { get; }

    /// <summary>The assembly the last build loaded, or <see langword="null" /> if none is.</summary>
    /// <remarks>After a failed build this is the last build that worked, loaded again, when it was still on disk.</remarks>
    Assembly? Loaded { get; }
}
