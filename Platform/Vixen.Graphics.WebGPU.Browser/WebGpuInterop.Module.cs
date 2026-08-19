// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU.Browser;

/// <summary>
///     Which JavaScript module the interop in <c>WebGpuInterop.cs</c> talks to, and where it is
///     fetched from.
/// </summary>
/// <remarks>
///     A separate file from the rest of the class for the reason
///     <c>Vixen.Platform.Web/WebInterop.Module.cs</c> gives: everything else in the class is
///     <c>[JSImport]</c> and therefore browser-only, and <see cref="DefaultModuleUrl" /> is a string
///     that has to agree with a published directory layout — which no compiler checks and which was
///     wrong here too. <c>BrowserModuleUrlTests</c> links this file and asserts the agreement.
/// </remarks>
internal static partial class WebGpuInterop {
    /// <summary>What the module is called once imported.</summary>
    public const string ModuleName = "vixen-webgpu";

    /// <summary>Where it is fetched from when the caller does not say.</summary>
    /// <remarks>
    ///     ⚠ <c>../</c>, for the reason set out on <c>WebInterop.DefaultModuleUrl</c>:
    ///     <c>JSHost.ImportAsync</c> resolves against the runtime's module in <c>_framework/</c>, and
    ///     this file is a content file at the site root.
    /// </remarks>
    public const string DefaultModuleUrl = "../vixen-webgpu.js";
}
