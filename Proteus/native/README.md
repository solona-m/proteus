# proteus_bcn.dll — native SIMD block compressor

Fast BC7/BC5 encoder P/Invoked by `TextureLoader` (see `EnsureNativeCompressor` /
`EncodeBlockCompressedNative`). The managed `BCnEncoder.Net` path is kept as an automatic
fallback if this DLL is missing or a call throws, so the plugin still works without it.

- **BC7:** [bc7enc](https://github.com/richgel999/bc7enc) (`bc7enc.c/.h`), modes 1/6, `m_uber_level = 0`,
  mode-6-only, linear weights — the fast config.
- **BC5:** rgbcx (`rgbcx.h` + `rgbcx_table4.h`), `encode_bc5` (R→chan0, G→chan1).
- **Shim:** `proteus_bcn.cpp` exposes `proteus_encode_bc7` / `proteus_encode_bc5`, each encoding a range of
  4×4 block-rows so the C# caller fans the work across cores. Output is linear block order (what
  FFXIV/Lumina expect). One-time table init is a thread-safe C++11 magic static.

## Rebuild (Windows, MSVC)

```
src\build.bat        # runs vcvars64 then: cl /LD /O2 /arch:AVX2 proteus_bcn.cpp bc7enc.c
```

Then copy the resulting `proteus_bcn.dll` up to this folder (`native\`). The csproj ships it next to
`Proteus.dll` via a `<Content>` copy. Third-party licenses are in `THIRD_PARTY_NOTICES.txt`.

Validated by round-trip decode: BC7 max error ≈9/255, BC5 R/G error ≤1.
