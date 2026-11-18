# XivLiveMesh

Poses a worn model's vertices onto the live FFXIV character on the CPU, so a plugin can draw over it or
pick a point on it.

Licensed under the GNU Affero General Public License v3.0 or later — see `LICENSE`.

## Third-party sources

None remain: every source file in this library is Proteus's own. (`PbdFile.cs` is a clean-room
implementation written from `docs/pbd-format-spec.md`; see `docs/pbd-clean-room-log.md`.)

Uses [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) (MIT), referenced from Dalamud's own
copy and not redistributed.

Research references consulted for which game structures exist, with no code copied: Ktisis and Brio
(GPL-3.0), DragAndDropTexturing (AGPL-3.0). Proteus's own code moved here (the .mdl skin reading pattern,
brush geometry helpers) is by the same author and relicensed under AGPL-3.0 with the rest of Proteus.
