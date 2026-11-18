# XivLiveMesh

Poses a worn model's vertices onto the live FFXIV character on the CPU, so a plugin can draw over it or
pick a point on it.

Licensed under the Proteus Licence with the rest of Proteus; see `LICENSE` (a copy of the repository's root
`LICENSE`). Copies received earlier under the GNU AGPL v3.0 stay under that licence.

## Third-party sources

None remain: every source file in this library is Proteus's own. (`PbdFile.cs` is a clean-room
implementation written from `docs/pbd-format-spec.md`; see `docs/pbd-clean-room-log.md`.)

Uses [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) (MIT), referenced from Dalamud's own
copy and not redistributed.

Research references consulted for which game structures exist, with no code copied: Ktisis and Brio
(GPL-3.0), DragAndDropTexturing (AGPL-3.0). Proteus's own code moved here (the .mdl skin reading pattern,
brush geometry helpers) is by the same author and is under the Proteus Licence with the rest of Proteus.
