# Vixen.Benchmarks.Imaging

```bash
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Imaging            # about three minutes
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Imaging -- --quick # no 4096², no Best timings
```

A report rather than a BenchmarkDotNet suite. What is being measured is a PSNR per picture and whole
seconds of encoding, and neither wants a harness repeating it until the statistics settle. It prints
PSNR at 1024² per picture and per `BlockQuality`, then encode time at 2048² and 4096² on every core.

The pictures are `Core/Vixen.Core.Imaging.Tests/ReferenceImages.cs`, linked rather than copied, so the
report and the floors `BlockQualityTests` holds are about the same pixels. Those tests hold the
decibels; nothing holds the seconds, because a wall-clock assertion is a flake with a number in it.

The results, and what they replaced, are in
[the imaging README](../../Core/Vixen.Core.Imaging/README.md#what-each-level-is-worth).
