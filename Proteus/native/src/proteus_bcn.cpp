// Proteus native block-compression shim: fast BC7 (bc7enc, modes 1/6) + BC5 (rgbcx).
// Each entry point encodes a RANGE of 4x4 block-rows so the C# caller can parallelise across cores.
// Output is linear block order (matching what FFXIV/Lumina expect and what the managed path produced).
#include <stdint.h>
#include <string.h>

#define RGBCX_IMPLEMENTATION
#include "rgbcx.h"
#include "bc7enc.h"

static void ensure_init()
{
    // C++11 guarantees function-local static init runs exactly once, thread-safely (MSVC "magic statics"),
    // so concurrent first calls from the parallel C# encoder can't double-init the global tables.
    static bool init = []() { bc7enc_compress_block_init(); rgbcx::init(); return true; }();
    (void)init;
}

// Gather a 4x4 RGBA block (R first) from the full image into a 64-byte scratch buffer.
static inline void gather(const uint8_t* rgba, int width, int br, int bx, uint8_t* block)
{
    for (int y = 0; y < 4; y++)
        memcpy(block + y * 16, rgba + (((size_t)(br * 4 + y) * width) + bx * 4) * 4, 16);
}

// Encode block-rows [blockRowStart, blockRowStart+blockRowCount) to BC7. 'out' points at the first
// output block for this range; 16 bytes per block, (width/4) blocks per row.
extern "C" __declspec(dllexport)
void proteus_encode_bc7(const uint8_t* rgba, int width, int /*height*/, int blockRowStart, int blockRowCount, uint8_t* out)
{
    ensure_init();
    bc7enc_compress_block_params p;
    bc7enc_compress_block_params_init(&p);
    p.m_perceptual = BC7ENC_FALSE;                                   // linear RGB error (neutral, faster)
    p.m_weights[0] = p.m_weights[1] = p.m_weights[2] = p.m_weights[3] = 1;
    p.m_uber_level = 0;                                              // fastest search
    p.m_max_partitions_mode = 0;                                    // mode 6 only — fastest, still high quality
    const int bw = width / 4;
    uint8_t block[64];
    for (int br = blockRowStart; br < blockRowStart + blockRowCount; br++)
        for (int bx = 0; bx < bw; bx++, out += 16)
        {
            gather(rgba, width, br, bx, block);
            bc7enc_compress_block(out, block, &p);
        }
}

// BC5 (two-channel: R->chan0, G->chan1). Same block-row-range contract as BC7.
extern "C" __declspec(dllexport)
void proteus_encode_bc5(const uint8_t* rgba, int width, int /*height*/, int blockRowStart, int blockRowCount, uint8_t* out)
{
    ensure_init();
    const int bw = width / 4;
    uint8_t block[64];
    for (int br = blockRowStart; br < blockRowStart + blockRowCount; br++)
        for (int bx = 0; bx < bw; bx++, out += 16)
        {
            gather(rgba, width, br, bx, block);
            rgbcx::encode_bc5(out, block, 0, 1, 4);
        }
}
