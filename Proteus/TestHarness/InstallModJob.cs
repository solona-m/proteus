using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>
/// The install action (plan genital-switches, in-game QA): Penumbra installs a local .pmp with its own importer
/// (<c>Penumbra.InstallMod.V5</c>), and the job waits for the folder it makes. Proteus writes no file and deletes nothing
/// under Penumbra's mod root; it only compares Penumbra's mod list before and after. The installed mod stays — the owner's
/// choice — and is never put back.
/// </summary>
internal static class InstallModJob
{
    /// <param name="Pmp">The package, an existing absolute .pmp path.</param>
    /// <param name="Before">The mod folders Penumbra listed before the install was asked for.</param>
    /// <param name="Name">The mod name the package's meta.json gives: the new folder must carry it.</param>
    internal sealed record Args(string Pmp, IReadOnlySet<string> Before, string Name);

    /// <summary>The mod name a package's own meta.json gives, read only; null when it cannot be read.</summary>
    internal static string? PackName(string pmp)
    {
        try
        {
            using var zip = ZipFile.OpenRead(pmp);
            var meta = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), "meta.json", StringComparison.OrdinalIgnoreCase));
            if (meta == null || meta.Length > 1 << 20) return null;
            using var s = meta.Open();
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch (Exception) { return null; }
    }

    internal static async Task Run(HarnessJobs jobs, HarnessJob job, Args args)
    {
        var ct = job.Cancel.Token;
        var deadline = DateTime.UtcNow + jobs.Timings.InstallWait;
        job.Progress("waiting for Penumbra to list the new mod");
        while (true)
        {
            ModList? mods;
            try { mods = await jobs.Dispatcher.RunAsync(() => jobs.Game.Mods(), jobs.Timings.FrameworkStart, ct).ConfigureAwait(false); }
            catch (FrameworkTimeoutException) { mods = null; }
            catch (OperationCanceledException) { job.Fail(new JobError("cancelled", "the install wait was cancelled")); return; }
            // Only folders Penumbra did not list before: an existing mod is never reported.
            var fresh = mods?.Mods.Keys.Where(d => !args.Before.Contains(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            // The new folder carrying the package's own name — never one only by count: a mod the user installed meanwhile
            // is new too.
            var named = fresh.Where(d => string.Equals(mods!.Mods[d], args.Name, StringComparison.Ordinal)).ToList();
            if (named.Count == 1)
            {
                job.SetModDir(named[0]);
                job.SetResult(new { modDir = named[0], candidates = fresh });
                job.Progress("");
                return;
            }
            if (named.Count > 1)
            {
                job.SetResult(new { candidates = fresh });
                job.Fail(new JobError("install-ambiguous", $"Penumbra listed {named.Count} new mods named \"{args.Name}\""));
                return;
            }
            if (DateTime.UtcNow >= deadline)
            {
                if (fresh.Count > 0) job.SetResult(new { candidates = fresh });
                job.Fail(new JobError(fresh.Count > 0 ? "install-ambiguous" : "install-timeout",
                    $"Penumbra did not list a new mod named \"{args.Name}\"; it may still be importing — look for it in /state before installing again"));
                return;
            }
            try { await Task.Delay(jobs.Timings.InstallPoll, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { job.Fail(new JobError("cancelled", "the install wait was cancelled")); return; }
        }
    }
}
