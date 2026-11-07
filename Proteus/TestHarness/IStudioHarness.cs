using System.Collections.Generic;
using System.Numerics;

namespace Proteus.TestHarness;

/// <summary>A file a harness save wrote, mod-relative with forward slashes, and its SHA-256 as on disk after it.</summary>
internal sealed record StudioFile(string File, string Sha256);

/// <summary>The Studio's status line in <c>/studio</c>: free text the plugin wrote, scrubbed of the session's names by
/// the harness (and only it: keys, folders and labels are left as they are).</summary>
internal sealed record StudioStatusLine(string text, bool isError);

/// <summary>A refusal from the Studio: status, code and message, as the routes answer them.</summary>
internal sealed record StudioRefusal(int Status, string Code, string Message);

/// <summary>What <c>studio-open</c> did: refused, or opened <see cref="File"/> with the panel as it was kept to put back.</summary>
/// <param name="Snapshot">The panel as it was before the harness first changed it: the Studio's own, opaque here.</param>
/// <param name="WindowOpened">The harness opened the window (it was closed), so put-back closes it again.</param>
/// <param name="EditVersion">The new solve's edit counter as opened.</param>
internal sealed record StudioOpenOutcome(StudioRefusal? Refused, string? File, object? Snapshot, bool WindowOpened,
                                         int EditVersion, IReadOnlyDictionary<string, object?>? State);

/// <summary>The controls <c>studio-set</c> changes, already validated; null or empty for those not given.</summary>
internal sealed record StudioSetRequest(string? Tool, string? Mode, string? View, bool? Mirror, bool? Adjacent, bool? MoveSkin,
                                        bool UnlockAll, IReadOnlyList<string> Lock, IReadOnlyList<string> Unlock, string? Part);

/// <summary>What <c>studio-set</c> did: per lock label locked / unlocked / unchanged / refused, and whether the part was taken.</summary>
/// <param name="EditVersion">The solve's edit counter after the controls changed.</param>
internal sealed record StudioSetOutcome(StudioRefusal? Refused, IReadOnlyList<(string Label, string Result)> Locks, bool? Chosen,
                                        int EditVersion, IReadOnlyDictionary<string, object?>? State);

/// <summary>One drag, already validated: a Move by <see cref="Offset"/> (metres), a Rotate by <see cref="Degrees"/>
/// about <see cref="Axis"/>, a Scale by <see cref="Factors"/>, or <c>undo</c>.</summary>
internal sealed record StudioDragRequest(string Kind, Vector3 Offset, Vector3 Axis, float Degrees, Vector3 Factors);

/// <summary>What <c>studio-drag</c> did, and the files the harness has saved in the mod since the session began.</summary>
/// <param name="Nodes">How many vertices moved.</param>
/// <param name="Saved">The edit is on disk (no save owed).</param>
/// <param name="Files">Every file of the mod's Studio record this session's saves wrote, hashed after this one.</param>
/// <param name="EditVersion">The solve's edit counter after the drag.</param>
internal sealed record StudioDragOutcome(StudioRefusal? Refused, string Kind, bool Moved, int Nodes, float WorstMm, Vector3? Pivot,
                                         bool Saved, string? Status, bool StatusIsError, IReadOnlyList<StudioFile> Files,
                                         int EditVersion, IReadOnlyDictionary<string, object?>? State);

/// <summary>The harness's Studio session: what it opened, what it saved, and the panel to put back.</summary>
internal sealed class StudioSession(string modDir, string file, object snapshot, bool windowOpened)
{
    public string ModDir { get; } = modDir;
    public string File { get; } = file;
    public object Snapshot { get; } = snapshot;
    public bool WindowOpened { get; } = windowOpened;

    /// <summary>The files this session's saves wrote, by file, with the hash each last had on disk after a harness save.</summary>
    public Dictionary<string, string> Files { get; } = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>The solve's edit counter after the harness's last drag; -1 before any.</summary>
    public int EditVersion { get; set; } = -1;

    /// <summary>Record a harness save: the files it wrote with their hashes now, and the edit counter.</summary>
    public void Record(IReadOnlyList<StudioFile> files, int editVersion)
    {
        foreach (var f in files) Files[f.File] = f.Sha256;
        EditVersion = editVersion;
    }
}

/// <summary>What put-back did to the Studio session.</summary>
/// <param name="Files">reverted / changed / none / failed.</param>
/// <param name="Panel">restored / changed / none.</param>
/// <param name="Keep">The session stays recorded: a failure to retry, or something else touched it.</param>
internal sealed record StudioPutBack(string Mod, string Files, string Panel, string? Code, bool Keep);

/// <summary>The Matcaps tool's dependencies, as the harness's matcap routes reach them.</summary>
/// <param name="Index">The shared library index (null before the plugin hands it over).</param>
/// <param name="ImageFolder">The global matcap image folder, or null without Penumbra.</param>
/// <param name="Penumbra">The library-selection seam (framework thread).</param>
/// <param name="Env">The environment <see cref="Services.Matcap.MatcapStudio.Prepare"/> runs in.</param>
/// <param name="ModsRoot">Penumbra's mods folder, or null.</param>
/// <param name="Mods">Penumbra's mod list (folder → name), or null.</param>
internal sealed record MatcapHarnessPort(
    Services.Matcap.MatcapIndex? Index, string? ImageFolder, Services.Matcap.IMatcapPenumbra Penumbra,
    Services.Matcap.MatcapStudio.Environment Env, string? ModsRoot, IReadOnlyDictionary<string, string>? Mods,
    System.Action<string, bool>? ForgetFaceUpstream = null,   // (game path, the face is the game's own again)
    // (mod root, game path, written files) -> the panel's drawn-file line after an Apply; framework thread.
    System.Func<string, string, IReadOnlyList<string>, string?>? DrawnStatus = null);

/// <summary>
/// The Studio as the test harness drives it: the panel's own code for each control, run on the framework thread between
/// frames. Implemented by the Studio panel in harness builds only.
/// </summary>
internal interface IStudioHarness
{
    /// <summary>The Studio's state, for <c>GET /studio</c>. Framework thread.</summary>
    IReadOnlyDictionary<string, object?> View();

    /// <summary>Whether <paramref name="file"/> of <paramref name="modDir"/> is the model open. Framework thread.</summary>
    bool IsOpen(string modDir, string file);

    /// <summary>A Studio edit is under way or not saved yet (a matcap job refuses rather than flush it). Framework thread.</summary>
    bool EditPending();

    /// <summary>What the Matcaps tool works with — its library index, image folder, Penumbra seam and environment — so the
    /// harness's matcap routes run the panel's own entry points. Framework thread.</summary>
    MatcapHarnessPort Matcaps();

    /// <summary>Open a mod's model as picking it does, keeping the panel as it was. Framework thread.</summary>
    StudioOpenOutcome Open(string modDir, string model, bool show);

    /// <summary>Set the controls as each one does — refused (studio-changed) unless the session's model is exactly as the
    /// harness left it. Framework thread.</summary>
    StudioSetOutcome Set(StudioSetRequest request, StudioSession session);

    /// <summary>Run one drag through the gizmo's own code, and save it — refused (studio-changed) unless the session's
    /// model and files are exactly as the harness left them, so a later put-back can never take the user's work for
    /// the harness's. Framework thread.</summary>
    StudioDragOutcome Drag(StudioDragRequest request, StudioSession session);

    /// <summary>End the session at put-back: check, revert and restore in one call. Framework thread.</summary>
    StudioPutBack EndSession(StudioSession session, bool refreshGame);
}
