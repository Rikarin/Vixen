// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// A pipe around the GPU's own BCn decoder.
//
// bcn-oracle.c asks another person's C what a block means. This asks the silicon: it uploads raw
// blocks into a compressed Metal texture, reads every texel back through a compute shader and prints
// what the texture unit returned. Nothing here decodes a block, which is the whole point: a mismatch
// in a partition table, an anchor or a bit layout is a mismatch with the hardware every shipped
// texture ends up on.
//
// Usage: bcn-metal-oracle <format>, blocks on stdin, texels on stdout.
//        bcn-metal-oracle --probe, says what the device is and whether it has BC formats.
//
//     bc1    8-byte blocks  -> 64 bytes, 16 RGBA8 texels
//     bc3    16-byte blocks -> 64 bytes, 16 RGBA8 texels
//     bc4    8-byte blocks  -> 16 bytes, 16 R8 texels
//     bc5    16-byte blocks -> 32 bytes, 16 RG8 texels
//     bc6h   16-byte blocks -> 96 bytes, 16 RGB half texels, unsigned
//     bc6hs  16-byte blocks -> 96 bytes, 16 RGB half texels, signed
//     bc7    16-byte blocks -> 64 bytes, 16 RGBA8 texels
//
// Exit codes: 0 fine, 1 failure, 2 usage, 3 this device has no BC formats.

import Foundation
import Metal

struct Format {
    let pixelFormat: MTLPixelFormat
    let blockBytes: Int
    let channels: Int
    let half: Bool
}

let formats: [String: Format] = [
    "bc1": Format(pixelFormat: .bc1_rgba, blockBytes: 8, channels: 4, half: false),
    "bc3": Format(pixelFormat: .bc3_rgba, blockBytes: 16, channels: 4, half: false),
    "bc4": Format(pixelFormat: .bc4_rUnorm, blockBytes: 8, channels: 1, half: false),
    "bc5": Format(pixelFormat: .bc5_rgUnorm, blockBytes: 16, channels: 2, half: false),
    "bc6h": Format(pixelFormat: .bc6H_rgbuFloat, blockBytes: 16, channels: 3, half: true),
    "bc6hs": Format(pixelFormat: .bc6H_rgbFloat, blockBytes: 16, channels: 3, half: true),
    "bc7": Format(pixelFormat: .bc7_rgbaUnorm, blockBytes: 16, channels: 4, half: false),
]

let kernelSource = """
    #include <metal_stdlib>
    using namespace metal;

    kernel void readAll(
        texture2d<float, access::read> source [[texture(0)]],
        device float4 *texels [[buffer(0)]],
        uint2 at [[thread_position_in_grid]]
    ) {
        texels[(at.y * source.get_width()) + at.x] = source.read(at);
    }
    """

func fail(_ message: String, _ code: Int32 = 1) -> Never {
    FileHandle.standardError.write(("bcn-metal-oracle: " + message + "\n").data(using: .utf8)!)
    exit(code)
}

guard CommandLine.arguments.count == 2 else {
    fail("usage: bcn-metal-oracle <\(formats.keys.sorted().joined(separator: "|"))|--probe>", 2)
}

guard let device = MTLCreateSystemDefaultDevice() else {
    fail("no Metal device", 3)
}

if CommandLine.arguments[1] == "--probe" {
    print("\(device.name) supportsBCTextureCompression=\(device.supportsBCTextureCompression)")
    exit(device.supportsBCTextureCompression ? 0 : 3)
}

guard let format = formats[CommandLine.arguments[1]] else {
    fail("no such format: \(CommandLine.arguments[1])", 2)
}

guard device.supportsBCTextureCompression else {
    fail("\(device.name) has no BC formats", 3)
}

let input = FileHandle.standardInput.readDataToEndOfFile()

guard input.count % format.blockBytes == 0 else {
    fail("a partial block: \(input.count) bytes in \(format.blockBytes)-byte blocks")
}

let library: MTLLibrary
let pipeline: MTLComputePipelineState

do {
    library = try device.makeLibrary(source: kernelSource, options: nil)
    pipeline = try device.makeComputePipelineState(function: library.makeFunction(name: "readAll")!)
} catch {
    fail("the kernel did not compile: \(error)")
}

guard let queue = device.makeCommandQueue() else {
    fail("no command queue")
}

/// Writing a batch at a time keeps the texture within the 16384 limit and the buffer sensible.
let columns = 1024
let rowsPerBatch = 1024
let blocksPerBatch = columns * rowsPerBatch
let totalBlocks = input.count / format.blockBytes
var output = Data(capacity: totalBlocks * 16 * format.channels * (format.half ? 2 : 1))

var firstBlock = 0

while firstBlock < totalBlocks {
    let blocks = min(blocksPerBatch, totalBlocks - firstBlock)
    let rows = (blocks + columns - 1) / columns
    let width = columns * 4
    let height = rows * 4

    let descriptor = MTLTextureDescriptor.texture2DDescriptor(
        pixelFormat: format.pixelFormat,
        width: width,
        height: height,
        mipmapped: false
    )
    descriptor.usage = .shaderRead
    descriptor.storageMode = .shared

    guard let texture = device.makeTexture(descriptor: descriptor) else {
        fail("no \(width)x\(height) texture")
    }

    // A short last row is padded with zero blocks, which are read back and dropped.
    var payload = Data(count: columns * rows * format.blockBytes)
    payload.replaceSubrange(
        0..<(blocks * format.blockBytes),
        with: input[(firstBlock * format.blockBytes)..<((firstBlock + blocks) * format.blockBytes)]
    )

    payload.withUnsafeBytes { bytes in
        texture.replace(
            region: MTLRegionMake2D(0, 0, width, height),
            mipmapLevel: 0,
            withBytes: bytes.baseAddress!,
            bytesPerRow: columns * format.blockBytes
        )
    }

    guard let readBack = device.makeBuffer(length: width * height * 16, options: .storageModeShared),
        let commands = queue.makeCommandBuffer(),
        let encoder = commands.makeComputeCommandEncoder()
    else {
        fail("no buffer or command encoder")
    }

    encoder.setComputePipelineState(pipeline)
    encoder.setTexture(texture, index: 0)
    encoder.setBuffer(readBack, offset: 0, index: 0)
    encoder.dispatchThreads(
        MTLSize(width: width, height: height, depth: 1),
        threadsPerThreadgroup: MTLSize(width: 64, height: 1, depth: 1)
    )
    encoder.endEncoding()
    commands.commit()
    commands.waitUntilCompleted()

    if let error = commands.error {
        fail("the GPU refused: \(error)")
    }

    let texels = readBack.contents().bindMemory(to: SIMD4<Float>.self, capacity: width * height)

    for block in 0..<blocks {
        let originX = (block % columns) * 4
        let originY = (block / columns) * 4

        for y in 0..<4 {
            for x in 0..<4 {
                let texel = texels[((originY + y) * width) + originX + x]

                for channel in 0..<format.channels {
                    if format.half {
                        var bits = Float16(texel[channel]).bitPattern
                        withUnsafeBytes(of: &bits) { output.append(contentsOf: $0) }
                    } else {
                        output.append(UInt8(max(0, min(255, (texel[channel] * 255).rounded()))))
                    }
                }
            }
        }
    }

    firstBlock += blocks
}

FileHandle.standardOutput.write(output)
