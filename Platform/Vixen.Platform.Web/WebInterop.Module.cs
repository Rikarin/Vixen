// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform.Web;

/// <summary>
///     Which JavaScript module the interop in <c>WebInterop.cs</c> talks to, and where it is fetched
///     from.
/// </summary>
/// <remarks>
///     <para>
///         <b>A separate file from the rest of the class so that a desktop test can compile it.</b>
///         Everything else in <c>WebInterop</c> is <c>[JSImport]</c>, which exists only in the
///         browser runtime pack — so <c>Vixen.Platform.Web.Tests</c>, which targets plain
///         <c>net10.0</c>, cannot see any of it. It links this file instead, exactly as it links
///         <c>WebContentManifest.cs</c> and for the reason that project file gives.
///     </para>
///     <para>
///         The point is not tidiness. <see cref="DefaultModuleUrl" /> was wrong for months, in three
///         bindings at once, and no compiler could have said so — the value is a string and the
///         thing it has to agree with is a published directory layout. <c>BrowserModuleUrlTests</c>
///         asserts the agreement, and it can only do that if the constant is reachable from an
///         assembly a test runner can host.
///     </para>
/// </remarks>
internal static partial class WebInterop {
    /// <summary>What the module is called once imported.</summary>
    public const string ModuleName = "vixen-platform";

    /// <summary>Where it is fetched from when the caller does not say.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>../</c>, and the two dots are the whole point.</b>
    ///         <see cref="System.Runtime.InteropServices.JavaScript.JSHost.ImportAsync" /> resolves a
    ///         relative URL against the <em>runtime's</em> module, which
    ///         <c>Microsoft.NET.Sdk.WebAssembly</c> publishes into <c>_framework/</c> — not against
    ///         the page. This file is a content file and lands at the site root. So <c>./</c>, which
    ///         this was, asked for <c>_framework/vixen-platform.js</c> and got a 404 dressed up as
    ///         <c>TypeError: Failed to fetch dynamically imported module</c> from inside
    ///         <c>WebPlatform.CreateAsync</c>, for the layout the SDK produces by default — which is
    ///         to say for every head that did not already pass
    ///         <see cref="WebPlatformOptions.ModuleUrl" />. Measured by publishing a head and
    ///         running it; there is no build-time diagnostic for it.
    ///     </para>
    ///     <para>
    ///         A page that arranges its assets differently still passes its own URL.
    ///     </para>
    /// </remarks>
    public const string DefaultModuleUrl = "../vixen-platform.js";
}
