// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Core.Imaging.BlockCompression;
using Vixen.Core.Imaging.Tests;
using Vixen.Graphics;

// A report rather than a BenchmarkDotNet suite: what is being measured is minutes of encoding and a
// PSNR per picture, and neither wants a harness repeating it until the statistics settle.
//
//     dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Imaging
//     dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Imaging -- --quick
//
// --quick leaves out 4096² and the Best timings, which are the slow half.
var quick = args.Contains("--quick");
var qualities = Enum.GetValues<BlockQuality>();

const int PsnrSize = 1024;

Console.WriteLine($"{RuntimeSummary()}");
Console.WriteLine();
Console.WriteLine($"PSNR at {PsnrSize}², dB");
Console.WriteLine($"{"image",-28}{"format",-8}{string.Join("", qualities.Select(quality => $"{quality,10}"))}");

foreach (var (name, make) in ReferenceImages.LowDynamicRange) {
    var image = make(PsnrSize);

    Row(name, "BC7", image, PixelFormat.Bc7RgbaUNorm, (original, decoded) => ReferenceImages.Psnr(original, decoded));
}

Row("HDR light edge", "BC6H", ReferenceImages.HighDynamicRange(PsnrSize), PixelFormat.Bc6HRgbUFloat, ReferenceImages.HalfPsnr);
Row("HDR, signed", "BC6Hs", ReferenceImages.SignedHighDynamicRange(PsnrSize), PixelFormat.Bc6HRgbSFloat, ReferenceImages.HalfPsnr);

Console.WriteLine();
Console.WriteLine("Encode time, seconds, whole process on every core");
Console.WriteLine($"{"image",-28}{"format",-8}{"size",-7}{string.Join("", qualities.Select(quality => $"{quality,10}"))}");

foreach (var size in quick ? [2048] : new[] { 2048, 4096 }) {
    Time("filtered noise", "BC7", ReferenceImages.FilteredNoise(size), PixelFormat.Bc7RgbaUNorm, size);
    Time("two-colour edges", "BC7", ReferenceImages.TwoColourEdges(size), PixelFormat.Bc7RgbaUNorm, size);
    Time("independent alpha", "BC7", ReferenceImages.IndependentAlpha(size), PixelFormat.Bc7RgbaUNorm, size);
    Time("HDR light edge", "BC6H", ReferenceImages.HighDynamicRange(size), PixelFormat.Bc6HRgbUFloat, size);
}

return;

void Row(string name, string format, TextureData image, PixelFormat target, PsnrOf psnr) {
    Console.Write($"{name,-28}{format,-8}");

    foreach (var quality in qualities) {
        var decoded = BlockCompressor.Decode(BlockCompressor.Encode(image, target, new() { Quality = quality }));

        Console.Write(string.Create(CultureInfo.InvariantCulture, $"{psnr(image.Pixels, decoded.Pixels),10:F2}"));
    }

    Console.WriteLine();
}

void Time(string name, string format, TextureData image, PixelFormat target, int size) {
    Console.Write($"{name,-28}{format,-8}{size,-7}");

    foreach (var quality in qualities) {
        if (quick && quality == BlockQuality.Best) {
            Console.Write($"{"-",10}");
            continue;
        }

        var watch = Stopwatch.StartNew();
        BlockCompressor.Encode(image, target, new() { Quality = quality });

        Console.Write(string.Create(CultureInfo.InvariantCulture, $"{watch.Elapsed.TotalSeconds,10:F2}"));
    }

    Console.WriteLine();
}

static string RuntimeSummary() =>
    $"{Environment.ProcessorCount} logical processors, {Environment.Version}, "
    + (Debugger.IsAttached ? "debugger attached" : "no debugger");

delegate double PsnrOf(ReadOnlySpan<byte> original, ReadOnlySpan<byte> decoded);
