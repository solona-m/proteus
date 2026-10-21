using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Microsoft.Win32.SafeHandles;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Shows a brush edit on the character while it is being painted, without redrawing the character.
/// Each preview is written to a new file (the game caches models by resolved path) and redirected with a
/// Penumbra temporary mod, then Glamourer reloads the gear in place. One preview at a time; a push while one
/// is in flight is refused.
/// <para/>
/// Previews mid-edit are private, in %TEMP%. Once an edit is saved, the saved file itself is shared: copied into
/// <see cref="ManagedModPaths.LivePreviewSubdir"/> inside the managed mod, under the Penumbra root, which is where sync
/// plugins look for the files a character draws. Otherwise a pair would not see the edit until the Studio is left.
/// </summary>
internal sealed class LiveBrushPreview(PenumbraBridge penumbra, CompositorService compositor, IPluginLog log)
{
    private const string Tag = "Proteus live brush";

    /// <summary>Above every regular mod, so the preview wins the game path whatever else redirects it.</summary>
    private const int Priority = int.MaxValue;

    /// <summary>Preview files kept after they are replaced, since the game may still be reading one asynchronously.</summary>
    private const int KeepFiles = 4;

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "proteus-live-brush");

    /// <summary>
    /// Tells this load of the plugin's files from an earlier one in the same game: the game caches models by path, so a
    /// reloaded plugin must not hand out a name it already gave the game.
    /// </summary>
    private static readonly string Nonce = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The managed mod folder shared previews were last put under, for <see cref="IsPreviewFile(string)"/>.</summary>
    private static volatile string? knownManagedRoot;

    private readonly PreviewFiles files = new(KeepFiles);
    private Task? inFlight;

    /// <summary>
    /// The save last shared, and the file it was shared as, which a later share of the same save reuses. Under
    /// <see cref="gate"/>: set by the push's task, read on the framework thread.
    /// </summary>
    private (string Stamp, string Path)? lastShared;

    private bool sharedSwept, warnedNoRoot, requestedManagedMod, warnedWrite;

    /// <summary>
    /// Guards <see cref="generation"/>, <see cref="endedAt"/> and <see cref="holding"/>. A push applies on the
    /// framework thread, but the unload can call <see cref="End"/> from another: without it a push could pass the
    /// <see cref="endedAt"/> check, End then find nothing to remove, and the push put its redirect on for good.
    /// </summary>
    private readonly object gate = new();

    private int generation;

    /// <summary>The last generation <see cref="End"/> took down; a push at or below it must not apply.</summary>
    private int endedAt;

    /// <summary>
    /// Every collection a redirect was put into and not yet taken out of, each from just before it is asked for. Removed
    /// from THOSE collections: the player's can change under a preview (a zone's assignment, a design), and removing
    /// from the new one left the old at <see cref="Priority"/> until the game restarted. One that will not let go stays
    /// here, and every push and End tries it again.
    /// </summary>
    private readonly HashSet<Guid> holding = [];

    /// <summary>A preview is being written or applied.</summary>
    public bool Busy => inFlight is { IsCompleted: false };

    /// <summary>A temporary redirect may be in place and has to be taken down.</summary>
    public bool Active { get { lock (gate) return holding.Count > 0; } }

    /// <summary>Glamourer could not reload gear in place; the caller should fall back to redrawing.</summary>
    public bool Unsupported { get; private set; }

    /// <summary>The <see cref="SavedEdit.Stamp"/> of the save last put on the character as a shared file; null when none.</summary>
    public string? SharedStamp { get { lock (gate) return lastShared?.Stamp; } }

    /// <summary>
    /// Whether previews can reach this kind of part in place. Never for hair, face, ears or tail: Glamourer's gear
    /// reload does not touch customization, and forcing <c>Human.UpdateDrawData</c> changes the hairstyle.
    /// </summary>
    public bool UnsupportedFor(bool customizePart) => customizePart || Unsupported;

    /// <summary>The model's game path is a customization part rather than gear: hair, face, ears or tail.</summary>
    public static bool IsCustomizePart(string gamePath)
        => gamePath.Contains("/obj/hair/", StringComparison.OrdinalIgnoreCase)
        || gamePath.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase)
        || gamePath.Contains("/obj/tail/", StringComparison.OrdinalIgnoreCase)
        || gamePath.Contains("/obj/zear/", StringComparison.OrdinalIgnoreCase);

    internal enum ShareTarget { Private, Shared, CreateManagedMod }

    internal enum PushResult { Refused, Private, Shared }

    /// <summary>Where a share can go: the managed mod, or nowhere until it exists, or nowhere at all.</summary>
    internal static ShareTarget ChooseShareTarget(string? managedRoot, bool managedModExists)
        => managedRoot == null ? ShareTarget.Private
         : managedModExists    ? ShareTarget.Shared
         :                       ShareTarget.CreateManagedMod;

    internal static string FileName(int pid, string nonce, int gen) => $"{pid}-{nonce}-{gen:D6}.mdl";

    /// <summary>Put <paramref name="model"/> on the character in place of the files at <paramref name="gamePaths"/>, privately.</summary>
    /// <param name="customizePart">The model is hair, face, ears or tail — see <see cref="IsCustomizePart"/>.</param>
    /// <returns>False when a preview is still in flight.</returns>
    public bool Push(byte[] model, IReadOnlyCollection<string> gamePaths, bool customizePart)
    {
        if (Busy || gamePaths.Count == 0) return false;
        int gen = NextGeneration();
        var file = Path.Combine(Dir, FileName(Environment.ProcessId, Nonce, gen));
        Start(gen, gamePaths, () =>
        {
            Directory.CreateDirectory(Dir);
            try { WriteTemporary(file, model); }
            catch { TryDelete(file); throw; }   // never leave half a model behind
            return (file, false);
        }, onApplied: null);
        return true;
    }

    /// <summary>
    /// Put the saved file <paramref name="saved"/> on the character, shared with sync pairs when the managed mod is there
    /// to share it in. A share of a save already shared reuses its file: the same bytes, so nothing new to upload.
    /// </summary>
    /// <returns>
    /// Refused while a preview is in flight; Private when there is nowhere to share it; Shared when a shared copy is
    /// being written. That write can still fall back to a private copy — <see cref="SharedStamp"/> says what landed.
    /// </returns>
    public PushResult PushShared(SavedEdit saved, IReadOnlyCollection<string> gamePaths)
    {
        if (Busy || gamePaths.Count == 0) return PushResult.Refused;

        var managedRoot = compositor.ManagedModFolder;
        if (managedRoot != null) knownManagedRoot = managedRoot;
        bool exists = managedRoot != null && File.Exists(Path.Combine(managedRoot, PenumbraModMeta.MetaFile));
        string? sharedDir = null;
        switch (ChooseShareTarget(managedRoot, exists))
        {
            case ShareTarget.Shared:
                sharedDir = ManagedModPaths.LivePreviewDir(managedRoot!);
                break;
            case ShareTarget.CreateManagedMod:
                // Once a session: if the composite that makes it cannot, asking on every save only repeats a composite.
                if (!requestedManagedMod)
                {
                    requestedManagedMod = true;
                    log.Information("[Proteus] live brush: no managed mod to share the preview in yet; asking for one");
                    compositor.RequestManagedModForPreview();
                }
                break;
            default:
                if (!warnedNoRoot)
                {
                    warnedNoRoot = true;
                    log.Warning("[Proteus] live brush: Penumbra's mod folder is unknown, so previews cannot be shared with sync pairs");
                }
                break;
        }

        int gen = NextGeneration();
        var name = FileName(Environment.ProcessId, Nonce, gen);
        (string Stamp, string Path)? before;
        lock (gate) before = lastShared;
        var reuse = sharedDir != null && before is { } last && last.Stamp == saved.Stamp && files.Contains(last.Path)
            ? last.Path : null;
        bool sweep = sharedDir != null && !sharedSwept;
        if (sweep) sharedSwept = true;
        var stamp = saved.Stamp;

        Start(gen, gamePaths, () =>
        {
            if (sweep) SweepShared(sharedDir!);
            if (reuse != null && File.Exists(reuse)) return (reuse, true);

            var written = CopyPreview(saved.FullPath, sharedDir, Dir, name);
            if (sharedDir != null && !written.Shared && !warnedWrite)
            {
                warnedWrite = true;
                log.Warning("[Proteus] live brush: could not write the shared preview into \"{0}\"; shown privately", sharedDir);
            }
            return written;
        }, onApplied: (path, shared) =>
        {
            if (!shared) return;
            lock (gate)
                if (gen > endedAt) lastShared = (stamp, path);   // not after End took it down
        });
        return sharedDir != null ? PushResult.Shared : PushResult.Private;
    }

    private const uint GenericWrite = 0x40000000, FileShareRead = 1, CreateAlways = 2, FileAttributeTemporary = 0x100;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
                                                     uint disposition, uint flags, IntPtr template);

    /// <summary>
    /// Write a private preview as a temporary file, which Windows keeps in memory rather than flushing to disk while it
    /// can. A stroke writes one every <c>PreviewIntervalMs</c> and most are deleted again within seconds. Only set when
    /// the file is created: .NET cannot ask for it, and setting it afterwards does not reach the open handle.
    /// </summary>
    internal static void WriteTemporary(string path, byte[] bytes)
    {
        using var handle = CreateFileW(path, GenericWrite, FileShareRead, IntPtr.Zero, CreateAlways,
                                       FileAttributeTemporary, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            // The reason in the message: an IOException's log line leaves its HResult out.
            throw new IOException($"Could not create \"{path}\": {Marshal.GetPInvokeErrorMessage(error)}",
                                  unchecked((int)0x80070000) | error);
        }
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.Write(bytes);
    }

    private int NextGeneration()
    {
        lock (gate) return ++generation;
    }

    /// <summary>Write the preview (off the framework thread), then put its redirect on and reload the gear in place.</summary>
    private void Start(int gen, IReadOnlyCollection<string> gamePaths, Func<(string Path, bool Shared)> write,
                       Action<string, bool>? onApplied)
    {
        inFlight = Task.Run(() =>
        {
            string? file = null;
            bool shared = false;
            try
            {
                (file, shared) = write();

                // Kept before it is applied, so an End landing between the two still finds it and deletes it.
                foreach (var gone in files.Add(file, shared)) TryDelete(gone);

                bool applied = Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    lock (gate)
                    {
                        // Taken down while this was being written: applying it now would put back what End removed.
                        if (gen <= endedAt) return false;
                        if (penumbra.GetPlayerCollectionId() is not { } collection) return false;

                        // The player moved to another collection since an earlier push: those still have the old preview.
                        holding.RemoveWhere(c => c != collection && penumbra.RemoveTemporaryMod(Tag, c, Priority));

                        // Recorded before asking, so End removes it even if Penumbra took it and still reported failure.
                        holding.Add(collection);
                        var map = gamePaths.ToDictionary(p => p, _ => file, StringComparer.OrdinalIgnoreCase);
                        if (!penumbra.SetTemporaryMod(Tag, collection, map, Priority)) return false;
                    }

                    // The gear reloads in place to pick the new file up — see ReloadGearInPlace.
                    if (!compositor.ReloadGearInPlace())
                    {
                        if (!Unsupported) log.Warning("[Proteus] live brush: Glamourer cannot reload gear in place; previews fall back to redraws");
                        Unsupported = true;
                    }
                    return true;
                }).GetAwaiter().GetResult();

                if (applied) onApplied?.Invoke(file, shared);
                LateFile fate;
                lock (gate) fate = LateFileFate(applied, takenDown: gen <= endedAt, holding.Count);
                if (fate == LateFile.Delete)
                {
                    files.Remove(file);
                    TryDelete(file);
                }
                else if (fate == LateFile.WarnRefused) log.Warning("[Proteus] live brush: Penumbra refused the preview redirect");
            }
            catch (Exception ex)
            {
                // A file already kept is left to End: the redirect may have gone on before the failure. A write that
                // failed deleted its own partial file.
                log.Warning(ex, "[Proteus] live brush: preview failed");
            }
        });
    }

    internal enum LateFile { Keep, Delete, WarnRefused }

    /// <summary>
    /// What becomes of a push's file once it has been applied, or not. Taken down while in flight with nothing
    /// redirecting to any preview: deleted, since End may have cleared the list before this file joined it. A collection
    /// that would not let go may still point at it, so then it stays for the next End. Not taken down and not applied:
    /// Penumbra refused it.
    /// </summary>
    internal static LateFile LateFileFate(bool applied, bool takenDown, int holdingCount)
        => takenDown && holdingCount == 0 ? LateFile.Delete
         : !applied && !takenDown          ? LateFile.WarnRefused
         :                                   LateFile.Keep;

    /// <summary>
    /// Copy <paramref name="source"/> into <paramref name="sharedDir"/>, or into <paramref name="privateDir"/> when there
    /// is no shared folder or it cannot be written. Byte for byte, so a shared copy hashes as the mod's own file.
    /// </summary>
    /// <param name="copy">The copy itself, <see cref="File.Copy(string, string, bool)"/> unless a test stands in for it.</param>
    internal static (string Path, bool Shared) CopyPreview(string source, string? sharedDir, string privateDir, string name,
                                                          Action<string, string>? copy = null)
    {
        copy ??= (from, to) => File.Copy(from, to, overwrite: true);
        // A copy that fails part way is deleted, never left for the game or a sync plugin to read.
        if (sharedDir != null)
        {
            var dest = Path.Combine(sharedDir, name);
            try
            {
                Directory.CreateDirectory(sharedDir);
                copy(source, dest);
                return (dest, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(dest);
            }
        }

        Directory.CreateDirectory(privateDir);
        var path = Path.Combine(privateDir, name);
        try { copy(source, path); }
        catch { TryDelete(path); throw; }
        return (path, false);
    }

    /// <summary>
    /// Take the preview down, so the character draws the mod's own file again.
    /// </summary>
    /// <param name="redraw">Redraw afterwards, the only sure way to replace the game's cached copy. False on teardown.</param>
    public void End(bool redraw)
    {
        // Not waited on (the push finishes on the framework thread, which may be this one); the generation mark makes
        // it stand down, and the gate makes the mark and the redirect it may already have added one reading.
        // Files are taken from the list under the gate, in the same reading as the generation: a push begun after this
        // mark (End may run off the framework thread on unload) bumps the generation first and adds its file later, so
        // it is never taken.
        Guid[] held;
        int mark;
        List<string> unheld = [];
        lock (gate)
        {
            mark = endedAt = generation;
            held = [.. holding];
            if (held.Length == 0)
            {
                // Nothing redirects to any preview: whatever was written, a push still landing included, can go now.
                lastShared = null;
                unheld = files.TakeAll();
            }
        }
        if (held.Length == 0)
        {
            foreach (var f in unheld) TryDelete(f);
            return;
        }

        var gone = held.Where(c => penumbra.RemoveTemporaryMod(Tag, c, Priority)).ToList();
        foreach (var stuck in held.Except(gone))
            log.Warning("[Proteus] live brush: Penumbra did not take the preview down from collection {0}", stuck);

        // A push begun since may have added its own redirect after those removals; then it is still Active, and its
        // file is in use.
        // A collection that would not let go still points at the preview files, so they stay; the next End tries again.
        List<string> released = [];
        lock (gate)
        {
            if (generation != mark) return;
            holding.ExceptWith(gone);
            if (holding.Count == 0)
            {
                lastShared = null;
                released = files.TakeAll();
            }
        }
        if (gone.Count > 0 && redraw) compositor.RedrawForChangedModel("live brush");
        foreach (var f in released) TryDelete(f);
    }

    /// <summary>A loaded model's file is one of these previews — the garment being brushed, drawn from its preview.</summary>
    public static bool IsPreviewFile(string path) => IsPreviewFile(path, knownManagedRoot);

    /// <summary>
    /// Learn where shared previews live as soon as it is known — Penumbra may come up after the plugin — so one still
    /// drawn from an earlier load of the plugin is recognised before this load shares anything.
    /// </summary>
    public static void RememberManagedRoot(string? managedRoot)
    {
        if (managedRoot != null) knownManagedRoot = managedRoot;
    }

    /// <summary><see cref="IsPreviewFile(string)"/> against a given managed mod folder (null: only private previews).</summary>
    internal static bool IsPreviewFile(string path, string? managedRoot)
        => BodyShapeReader.PathKey(path).StartsWith(BodyShapeReader.PathKey(Dir), StringComparison.Ordinal)
        || ManagedModPaths.IsLivePreview(path, managedRoot);

    /// <summary>Clear preview files a previous session left behind — a crash or an unload mid-preview.</summary>
    /// <param name="managedRoot">The managed mod folder, whose shared previews are swept too; null when not yet known.</param>
    public static void CleanUp(string? managedRoot)
    {
        try
        {
            if (Directory.Exists(Dir))
                foreach (var f in Directory.GetFiles(Dir, "*.mdl")) TryDelete(f);
        }
        catch { /* temp space; nothing depends on it being tidy */ }

        if (managedRoot == null) return;
        // Known from here on, so a preview still drawn from an earlier load of the plugin is recognised before this one
        // shares anything.
        knownManagedRoot = managedRoot;
        SweepShared(ManagedModPaths.LivePreviewDir(managedRoot));
    }

    /// <summary>Delete the shared previews no running game is using (see <see cref="StaleShared"/>).</summary>
    private static void SweepShared(string sharedDir)
    {
        try
        {
            if (!Directory.Exists(sharedDir)) return;
            foreach (var f in StaleShared(Directory.GetFiles(sharedDir, "*.mdl"), Environment.ProcessId, Nonce, ProcessAlive))
                TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* the folder could not be listed; left for the next sweep */
        }
    }

    /// <summary>
    /// Shared previews nothing can still be drawing: from a game that is no longer running, or from an earlier load of
    /// the plugin in this one. Another running game sharing the mods folder keeps its own; a name not in
    /// <see cref="FileName"/>'s form is not ours to judge.
    /// </summary>
    internal static IEnumerable<string> StaleShared(IEnumerable<string> files, int ownPid, string ownNonce,
                                                     Func<int, bool> processAlive)
    {
        foreach (var file in files)
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var pid)) continue;
            bool stale = pid == ownPid ? !string.Equals(parts[1], ownNonce, StringComparison.Ordinal)
                                       : !processAlive(pid);
            if (stale) yield return file;
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }   // no such process
        catch { return true; }                          // cannot tell: keep its files
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* still open in the game; left for the next CleanUp */ }
    }

    /// <summary>
    /// The preview files kept after they are replaced, a few of each kind. Separate, so a stroke's private previews never
    /// push out a shared file a sync plugin may still be reading. A file added again moves to the back.
    /// </summary>
    internal sealed class PreviewFiles(int keep)
    {
        private readonly object sync = new();
        private readonly List<string> priv = [];
        private readonly List<string> pub = [];

        /// <returns>The files pushed out, to be deleted.</returns>
        public List<string> Add(string path, bool shared)
        {
            lock (sync)
            {
                var list = shared ? pub : priv;
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                list.Add(path);
                var evicted = new List<string>();
                while (list.Count > keep)
                {
                    evicted.Add(list[0]);
                    list.RemoveAt(0);
                }
                return evicted;
            }
        }

        public void Remove(string path)
        {
            lock (sync)
            {
                priv.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                pub.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool Contains(string path)
        {
            lock (sync)
                return priv.Concat(pub).Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        public List<string> TakeAll()
        {
            lock (sync)
            {
                var all = priv.Concat(pub).ToList();
                priv.Clear();
                pub.Clear();
                return all;
            }
        }
    }
}
