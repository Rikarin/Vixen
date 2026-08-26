/* SPDX-FileCopyrightText: Copyright (c) Rikarin
   SPDX-License-Identifier: Apache-2.0

   A pipe around somebody else's BCn decoder.

   Vixen's own decoders are asserted against blocks worked out by hand, which catches a misread of
   the specification but not a misunderstanding of it: the fixture and the code encode the same
   reading. This wraps bcdec.h — an unrelated, independently written decoder — so
   BcnReferenceDecoderTests can put the same block through both and compare.

   The only thing this file does is move bytes. bcdec.h is NOT part of this repository; build.sh
   downloads it into a cache outside the tree. See README.md for why.

   Usage: bcn-oracle <format>, blocks on stdin, texels on stdout.

       bc1   8-byte blocks  -> 64 bytes, 16 RGBA8 texels
       bc3   16-byte blocks -> 64 bytes, 16 RGBA8 texels
       bc4   8-byte blocks  -> 16 bytes, 16 R8 texels
       bc5   16-byte blocks -> 32 bytes, 16 RG8 texels
       bc6h  16-byte blocks -> 96 bytes, 16 RGB half texels (unsigned)
       bc7   16-byte blocks -> 64 bytes, 16 RGBA8 texels
*/

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* BC4 and BC5's default path in bcdec is a deliberate approximation of the interpolation, traded
   for speed. This is an oracle, so it takes the slow exact one. */
#define BCDEC_BC4BC5_PRECISE
#define BCDEC_IMPLEMENTATION
#include "bcdec.h"

static int run(int blockBytes, int texelBytes, int pitch,
               void (*decode)(const void *, void *, int)) {
    unsigned char block[16];
    unsigned char texels[96];

    for (;;) {
        size_t got = fread(block, 1, (size_t)blockBytes, stdin);

        if (got == 0) {
            return 0;
        }

        if (got != (size_t)blockBytes) {
            fprintf(stderr, "bcn-oracle: a partial block of %zu bytes\n", got);
            return 1;
        }

        memset(texels, 0, sizeof texels);
        decode(block, texels, pitch);

        if (fwrite(texels, 1, (size_t)texelBytes, stdout) != (size_t)texelBytes) {
            fprintf(stderr, "bcn-oracle: could not write\n");
            return 1;
        }
    }
}

static void bc4_unsigned(const void *in, void *out, int pitch) { bcdec_bc4(in, out, pitch, 0); }
static void bc5_unsigned(const void *in, void *out, int pitch) { bcdec_bc5(in, out, pitch, 0); }
static void bc6h_unsigned(const void *in, void *out, int pitch) { bcdec_bc6h_half(in, out, pitch, 0); }

int main(int argc, char **argv) {
    if (argc != 2) {
        fprintf(stderr, "usage: bcn-oracle bc1|bc3|bc4|bc5|bc6h|bc7\n");
        return 2;
    }

    /* ⚠ The pitch is a row stride in DESTINATION ELEMENTS, and BC6H's element is a short while
       everything else's is a byte. Four texels of three halves is a stride of 12, not of the 24
       bytes they occupy — getting that wrong makes rows 1 to 3 come back as zeros, which reads
       exactly like a decoder disagreement and is not one. */
    if (strcmp(argv[1], "bc1") == 0)  return run(8, 64, 16, bcdec_bc1);
    if (strcmp(argv[1], "bc3") == 0)  return run(16, 64, 16, bcdec_bc3);
    if (strcmp(argv[1], "bc4") == 0)  return run(8, 16, 4, bc4_unsigned);
    if (strcmp(argv[1], "bc5") == 0)  return run(16, 32, 8, bc5_unsigned);
    if (strcmp(argv[1], "bc6h") == 0) return run(16, 96, 12, bc6h_unsigned);
    if (strcmp(argv[1], "bc7") == 0)  return run(16, 64, 16, bcdec_bc7);

    fprintf(stderr, "bcn-oracle: no such format '%s'\n", argv[1]);
    return 2;
}
