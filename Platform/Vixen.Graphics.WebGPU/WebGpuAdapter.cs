// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>The adapter a WebGPU device runs on, as much as it will say.</summary>
/// <remarks>
///     <para>
///         Less than any other backend reports, and on purpose: a browser deliberately does not name
///         the GPU, its vendor or its driver, because between them they identify a machine. So the
///         name is often <c>"WebGPU adapter"</c>, the kind is
///         <see cref="AdapterKind.Unknown" /> and the memory size is zero, and none of that is a
///         failure to look it up.
///     </para>
///     <para>
///         That matters beyond the log line. <c>DeviceMemory</c> is what a streaming budget would be
///         sized from, and on the web there is no number to size it from — so a budget on this
///         backend has to come from the application rather than the adapter.
///     </para>
/// </remarks>
sealed class WebGpuAdapter(WebGpuAdapterInfo info, GraphicsDeviceFeatures features) : IGraphicsAdapter {
    /// <inheritdoc />
    public string Name { get; } = string.IsNullOrEmpty(info.Name) ? "WebGPU adapter" : info.Name;

    /// <inheritdoc />
    public AdapterKind Kind { get; } = info.Kind switch {
        WgpuAdapterType.DiscreteGpu => AdapterKind.Discrete,
        WgpuAdapterType.IntegratedGpu => AdapterKind.Integrated,
        WgpuAdapterType.Cpu => AdapterKind.Software,
        _ => AdapterKind.Unknown
    };

    /// <inheritdoc />
    public string DriverVersion { get; } =
        string.IsNullOrEmpty(info.DriverDescription) ? "unknown" : info.DriverDescription;

    /// <inheritdoc />
    /// <remarks>Always zero. WebGPU reports no memory size on any implementation.</remarks>
    public ulong DeviceMemory => 0;

    /// <inheritdoc />
    public GraphicsDeviceFeatures Features { get; } = features;
}
