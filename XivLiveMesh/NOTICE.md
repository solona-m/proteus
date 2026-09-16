# XivLiveMesh

Poses a worn model's vertices onto the live FFXIV character on the CPU, so a plugin can draw over it or
pick a point on it.

Licensed under the GNU Affero General Public License v3.0 or later — see `LICENSE`.

## Third-party sources

| File | Ported from | License |
|---|---|---|
| `PbdFile.cs` | [Meddle](https://github.com/PassiveModding/Meddle) `Meddle.Utils/Files/PbdFile.cs`, `Meddle.Utils/RaceDeformer.cs` (as vendored in SkinTattoo @bc3663a) | AGPL-3.0 |

Uses [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) (MIT), referenced from Dalamud's own
copy and not redistributed.

Research references consulted for which game structures exist, with no code copied: Ktisis and Brio
(GPL-3.0), DragAndDropTexturing (AGPL-3.0). Proteus's own code moved here (the .mdl skin reading pattern,
brush geometry helpers) is by the same author and relicensed under AGPL-3.0 with the rest of Proteus.
