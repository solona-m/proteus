// Proteus native block-compression shim: fast BC7 (bc7enc, modes 1/6) + BC5 (rgbcx).
// Each entry point encodes a RANGE of 4x4 block-rows so the C# caller can parallelise across cores.
// Output is linear block order (matching what FFXIV/Lumina expect and what the managed path produced).
#include <stdint.h>
#include <string.h>

#define RGBCX_IMPLEMENTATION
#include "rgbcx.h"
#include "bc7enc.h"
#include "bc7decomp.h"

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

// ── Decode ──────────────────────────────────────────────────────────────────
// Scatter a decoded 4x4 RGBA block (R first) back into the full image.
static inline void scatter(uint8_t* rgba, int width, int br, int bx, const uint8_t* block)
{
    for (int y = 0; y < 4; y++)
        memcpy(rgba + (((size_t)(br * 4 + y) * width) + bx * 4) * 4, block + y * 16, 16);
}

// Block-compression formats this shim can decode. Values are arbitrary to this ABI — the C# side
// maps FFXIV/Lumina format codes onto them.
enum proteus_bcn_format { PROTEUS_BC1 = 1, PROTEUS_BC3 = 3, PROTEUS_BC5 = 5, PROTEUS_BC7 = 7 };

// ── BC4 channel decode ──────────────────────────────────────────────────────
// Hand-rolled rather than rgbcx::unpack_bc4, for ONE reason: rounding.
//
// rgbcx builds its interpolants with truncating integer division — (l*5 + h*2) / 7 — while the D3D
// spec (and Lumina, and the GPU) round: (l*5 + h*2 + 3) / 7. The two agree on most values and differ
// by exactly 1 on the rest, which measured out at ~700-1200 bytes of a 2048x2048 BC5 normal map.
//
// A one-bit difference is invisible, but it is NOT harmless here: Proteus names every composited
// texture by a hash of its content, so a decoder that shifts a channel by 1 changes every output
// filename, rewrites every baked texture and re-uploads the lot through the sync plugins. Matching
// Lumina bit-for-bit is what keeps a decode-speed change from becoming a re-bake of everything.
static inline void bc4_values(uint8_t* v, uint32_t l, uint32_t h)
{
    v[0] = (uint8_t)l;
    v[1] = (uint8_t)h;
    if (l > h)
        for (int i = 2; i < 8; i++) v[i] = (uint8_t)(((8 - i) * l + (i - 1) * h + 3) / 7);
    else
    {
        for (int i = 2; i < 6; i++) v[i] = (uint8_t)(((6 - i) * l + (i - 1) * h + 2) / 5);
        v[6] = 0;
        v[7] = 255;
    }
}

// One BC4 sub-block (8 bytes) into every 4th byte of a 16-pixel RGBA scratch block.
static inline void unpack_bc4_channel(const uint8_t* src, uint8_t* block, int channel)
{
    uint8_t v[8];
    bc4_values(v, src[0], src[1]);

    // 16 three-bit selectors packed little-endian across bytes 2..7.
    uint64_t bits = 0;
    for (int i = 0; i < 6; i++) bits |= (uint64_t)src[2 + i] << (8 * i);

    for (int p = 0; p < 16; p++)
        block[p * 4 + channel] = v[(bits >> (3 * p)) & 7];
}

// Compressed bytes per 4x4 block. BC1 packs a block into EIGHT bytes — it carries one colour endpoint
// pair and 2-bit indices, with no separate alpha block — while BC3/BC5/BC7 all use sixteen. Getting this
// wrong does not fail loudly: a 16-byte stride over BC1 data reads every other block and decodes a
// scrambled image while still reporting success. 0 for a format this shim does not handle.
extern "C" __declspec(dllexport)
int proteus_bcn_block_bytes(int format)
{
    switch (format)
    {
    case PROTEUS_BC1: return 8;
    case PROTEUS_BC3:
    case PROTEUS_BC5:
    case PROTEUS_BC7: return 16;
    default:          return 0;
    }
}

// Decode block-rows [blockRowStart, blockRowStart+blockRowCount) to RGBA8 (R first).
// 'blocks' points at the FIRST BLOCK OF THIS RANGE (not the start of the surface); 'rgba' points at
// the start of the WHOLE image, since scatter() computes absolute row offsets from br. Same
// block-row-range contract as the encoders above, so the C# caller fans it across cores identically.
//
// Emits RGBA directly rather than BGRA: the managed path decoded to BGRA and then ran a serial
// byte-at-a-time channel swap over the whole 64 MB surface, which this removes outright.
//
// Returns 0 on an unknown format, else 1. BC5 has no third channel, so blue is left at 0 and alpha at
// 255 — matching what a two-channel normal map means, what Lumina's own decoder produces, and what the
// callers of this data expect.
extern "C" __declspec(dllexport)
int proteus_decode_bcn(int format, const uint8_t* blocks, int width, int blockRowStart, int blockRowCount, uint8_t* rgba)
{
    ensure_init();
    const int stride = proteus_bcn_block_bytes(format);
    if (stride == 0) return 0;
    const int bw = width / 4;
    uint8_t block[64];

    for (int br = blockRowStart; br < blockRowStart + blockRowCount; br++)
        for (int bx = 0; bx < bw; bx++, blocks += stride)
        {
            switch (format)
            {
            case PROTEUS_BC7:
                if (!bc7decomp::unpack_bc7(blocks, (bc7decomp::color_rgba*)block)) memset(block, 0, sizeof(block));
                break;
            case PROTEUS_BC1:
                rgbcx::unpack_bc1(blocks, block, true);
                break;
            case PROTEUS_BC3:
                rgbcx::unpack_bc3(blocks, block);
                break;
            case PROTEUS_BC5:
                // Two BC4 sub-blocks: red then green. Blue stays 0 and alpha 255 — the convention
                // Lumina's own decoder produces and the one the shell's normal-map callers rely on.
                memset(block, 0, sizeof(block));
                for (int i = 0; i < 16; i++) block[i * 4 + 3] = 255;
                unpack_bc4_channel(blocks, block, 0);
                unpack_bc4_channel(blocks + 8, block, 1);
                break;
            default:
                return 0;
            }
            scatter(rgba, width, br, bx, block);
        }
    return 1;
}
