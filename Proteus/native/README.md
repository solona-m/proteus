# proteus_bcn.dll — native SIMD block codec

Fast BC7/BC5 **encoder** and BC1/BC3/BC5/BC7 **decoder**, P/Invoked by `TextureLoader` (see
`EnsureNativeCompressor` / `EncodeBlockCompressedNative` / `DecodeBlockCompressedNative`). The
managed paths — `BCnEncoder.Net` for encode, Lumina for decode — are kept as automatic fallbacks if
this DLL is missing or a call throws, so the plugin still works without it.

- **BC7 encode:** [bc7enc](https://github.com/richgel999/bc7enc) (`bc7enc.c/.h`), modes 1/6,
  `m_uber_level = 0`, mode-6-only, linear weights — the fast config.
- **BC5 encode:** rgbcx (`rgbcx.h` + `rgbcx_table4.h`), `encode_bc5` (R→chan0, G→chan1).
- **Decode:** `bc7decomp.cpp/.h` (same bc7enc upstream) for BC7; rgbcx's `unpack_bc1/bc3` for those two;
  a hand-rolled BC4 for BC5. Emits **RGBA directly** — the managed path decoded to BGRA and then ran a
  serial channel swap over the whole surface, which this removes. BC2 is deliberately absent: rgbcx has
  no unpack for it and the game does not ship it.
- **Why BC5 is hand-rolled:** rgbcx builds BC4 interpolants with truncating division,
  `(l*5 + h*2) / 7`, while the D3D spec, Lumina and the GPU round: `(l*5 + h*2 + 3) / 7`. That is a
  ±1 difference on a minority of values — ~1200 bytes of a 2048² normal map, measured. It matters
  because Proteus names each composited texture by a hash of its content, so a channel off by one
  renames every baked output and re-uploads the lot through the sync plugins.
- **Shim:** `proteus_bcn.cpp` exposes `proteus_encode_bc7` / `proteus_encode_bc5` /
  `proteus_decode_bcn`, each handling a range of 4×4 block-rows so the C# caller fans the work across
  cores. Block order is linear (what FFXIV/Lumina expect). One-time table init is a thread-safe C++11
  magic static.

Note the decode call's pointer convention, which differs from the encoders': `blocks` points at this
worker's **first block**, but `rgbaOut` points at the **whole image**, because `scatter()` computes
absolute row offsets from the block-row index.

## Rebuild (Windows, MSVC)

```
src\build.bat        # locates VS via vswhere, then: cl /LD /O2 /MT proteus_bcn.cpp bc7enc.c bc7decomp.cpp
```

### Do not add `/arch:AVX2`

It was there once, and it crashed a user's game. These sources contain no SIMD intrinsics, so the flag
only permits autovectorisation — but MSVC has no runtime dispatch, so it also emits AVX/FMA/BMI/LZCNT
through ordinary scalar code. The DLL still *loads* on any CPU and then dies of
`STATUS_ILLEGAL_INSTRUCTION` on the first call, which no `catch` can see: the CLR fail-fasts the whole
game. Pentium and Celeron parts had no AVX at all until Alder Lake.

Measured, 2048², single-threaded, AVX2 build vs baseline: BC7 encode 1.05x, BC7 decode 1.18x, BC5
either way within noise — and the two builds' output is **byte-identical** on every size and input
tried, which matters because each baked texture is named by a hash of its content. A few percent does
not justify a binary that cannot run on the machines it ships to.

If you ever do want the flag back, it needs two builds and a runtime `Avx2.IsSupported` pick between
them — not a check that disables the shim, because the managed fallback is ~30x slower (26s to BC7 a
2048² surface, against 0.9s native) and a composite that slow never outruns the triggers that restart
it. To verify a build is baseline-safe: `dumpbin /disasm:nobytes proteus_bcn.dll` must contain no
BMI/LZCNT mnemonics and no VEX (`v*`) ones outside the static CRT's `memcpy`, whose AVX path sits
behind a `cmp dword ptr [__isa_available],3` dispatch.

Then copy the resulting `proteus_bcn.dll` up to this folder (`native\`). The csproj ships it next to
`Proteus.dll` via a `<Content>` copy. Third-party licenses are in `THIRD_PARTY_NOTICES.txt`.

**You do not need to restart the game to pick up a rebuilt DLL.** `TextureLoader.ShadowCopyNative`
copies it to `%TEMP%\Proteus.native\<length>-<mtime>\` and loads *that*, so the build output is never
memory-mapped and `dotnet build` can always overwrite it. A rebuilt DLL gets a new stamp folder and is
picked up on the next plugin load; older folders are swept on startup, skipping any still mapped by a
running instance.

Without that, the lock outlives a plugin unload — a plugin load context only unloads once nothing
references it, and one straggling task or timer is enough to keep the DLL mapped — so the next build
fails with *"used by another process: FINAL FANTASY XIV"* and the only remedy is closing the game.

## Validation

Encode was validated by round-trip decode: BC7 max error ≈9/255, BC5 R/G error ≤1.

Decode is validated twice, because a decoder that is merely *plausible* produces silently wrong
pixels in a baked texture rather than an error anyone would notice:

1. **Offline, when rebuilding the DLL.** Round-trip (encode → decode ≈ original), plus the two checks
   that actually catch shim bugs: decoding in several block-row ranges must be **byte-identical** to
   decoding in one call, and every output byte must be written (verify by decoding twice over two
   different pre-fills and comparing — counting leftover fill bytes gives ~1/256 false positives).
2. **At runtime, once per format per session.** `TextureLoader` compares the first native decode of each
   format against Lumina's, byte for byte, on both the `.tex` and `.dds` paths, and falls back to Lumina
   **for that format only** if they disagree. Per-format in both directions, learned the hard way: a
   session-wide "verified" flag checks whichever format loads first and waves the rest through (that is
   how a BC1 block-stride bug survived), and a session-wide "disable" surrenders BC7 — the bulk of the
   cost, and provably identical — to fix one format's rounding.

   This is the check that caught the BC5 rounding difference above, on a real file, after the offline
   tests had passed. Worth remembering why they missed it: the round-trip tests encode with the shim's
   own encoder, so any convention the two halves share is invisible to them.
