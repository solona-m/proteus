using System.Collections.Generic;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// What <see cref="MeshVolumeService.Inflate"/> needs of an edit, and nothing else: a per-vertex displacement, the
/// normal that goes with it, the wind, and the mesh spans that map a concatenated vertex back into the file.
/// <para/>
/// Extracted so that a pass whose rules are the OPPOSITE of the brush's can be written from nothing rather than as a
/// mode inside <see cref="MeshVolumeSolve"/>. The body retarget is that pass: it must move the garment's own embedded
/// body-skin mesh by rules of its own. The brush holds skin still unless the user asks for "Move skin too", and then by
/// one rule (<c>MeshVolumeSolve.Held</c>) captured once per operation — not a flag threaded through every site, the
/// mistake that <c>UnfoldTriangles</c> already taught this codebase once.
/// </summary>
internal interface IMeshEdit
{
    /// <summary>Where each mesh's vertices sit in the concatenated numbering the accessors below are indexed by.</summary>
    IReadOnlyList<MeshSpan> Spans { get; }

    /// <summary>The furthest any vertex moved, which the bounding boxes have to grow by.</summary>
    float Worst { get; }

    /// <summary>Anything to write at all. A clean edit can be skipped.</summary>
    bool Dirty { get; }

    /// <summary>The wind channel was edited, so it has to be written alongside the geometry.</summary>
    bool WindEdited { get; }

    /// <summary>How far vertex <paramref name="vertex"/> moved from where its author put it.</summary>
    Vec3 DeltaAt(int vertex);

    /// <summary>Vertex <paramref name="vertex"/>'s normal AFTER the edit.</summary>
    Vec3 NormalAt(int vertex);

    /// <summary>Vertex <paramref name="vertex"/>'s wind, 0..1. Only read when <see cref="WindEdited"/>.</summary>
    float WindAt(int vertex);
}
