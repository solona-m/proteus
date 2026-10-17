namespace Proteus.Services;

/// <summary>
/// One lock for the writers that read a mod's manifest, change it and write it back while another of them may be doing
/// the same to the same mod: body refits (<see cref="BodyRetargetWriter"/>) and hair ports (<see cref="HairPortWriter"/>),
/// which an outfit change can set off together — and an outfit pack ships its hair and its gear in one mod.
/// <para/>
/// Partial on purpose: Proteus's other manifest writers run from a button in the UI, one at a time, and Penumbra writes
/// with no lock of ours at all. Each of those reads the manifest afresh right before writing, which narrows the race but
/// does not close it.
/// </summary>
internal static class ModManifestLock
{
    public static readonly object Gate = new();
}
