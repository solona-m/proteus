using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// A feature whose use is counted for the opt-in usage statistics. Each value's wire name is in
/// <see cref="UsageStats.WireKey"/>, and that list must match <c>stats/src/features.js</c>, which the
/// server checks every report against (UsageStatsTests enforces the match).
/// </summary>
public enum UsageFeature
{
    Composite,
    SecondSkin,
    StudioOpen,
    LiveBrush,
    MeshToggle,
    ImportOnion,
    ImportPmp,
    ImportTtmp2,
    ImportEmissive,
    ImportEye,
    ImportInstalled,
    PresetSave,
    PresetApply,
    DesignBindCapture,
    DesignBindRestore,
    HatCompat,
    ColorsetEdit,
    MasksEdit,
    ModCreate,
    ModExport,
}

/// <summary>
/// Opt-in, per-day feature counts, sent to <see cref="Endpoint"/> at most once per finished day. What is
/// sent and why is PRIVACY.md at the repo root; that file is a promise, so change it whenever this does.
/// <para/>
/// Without consent every entry point is a no-op: nothing is counted, nothing is written to disk and nothing
/// is sent. Consent is <see cref="Configuration.UsageStatsConsent"/>, given through the one-time prompt
/// (<c>Gui.UsageConsentWindow</c>) or Settings → Privacy.
/// <para/>
/// Counts are kept per UTC day in <c>usage-pending.json</c> in the config directory, so a session that ends
/// before its day does still gets reported. A finished day is sent as one report and removed once the
/// server accepts it; today's counts wait for tomorrow. Days older than <see cref="MaxPendingDays"/> are
/// dropped unsent (the server refuses them anyway).
/// </summary>
public sealed class UsageStats : IDisposable
{
    /// <summary>
    /// Bump when what is collected changes. Everyone asked under an older version is asked again rather than
    /// having their earlier answer carried over — PRIVACY.md promises exactly that.
    /// </summary>
    public const int ConsentVersion = 1;

    public const string Endpoint = "https://stats.solona.info/";
    public const string PrivacyUrl = "https://dl.solona.info/PRIVACY.md";

    /// <summary>The server accepts a report up to 8 days old; one day of slack for clocks and time zones.</summary>
    internal const int MaxPendingDays = 7;

    private const string PendingFile = "usage-pending.json";

    /// <summary>
    /// The live instance, for the counting points scattered through services and windows that would
    /// otherwise all need it threaded through their constructors. Null before load and after unload, and
    /// <see cref="Count"/> tolerates that.
    /// </summary>
    public static UsageStats? Current { get; private set; }

    /// <summary>Counts one use of <paramref name="feature"/>. Safe from any thread; a no-op without consent.</summary>
    public static void Count(UsageFeature feature) => Current?.Add(feature, oncePerSession: false);

    /// <summary>
    /// Counts <paramref name="feature"/> at most once per plugin session, for actions that fire per edit or per
    /// frame (colour rows, tab visits) where "used it today" is the useful fact and a raw count is noise.
    /// </summary>
    public static void CountOncePerSession(UsageFeature feature) => Current?.Add(feature, oncePerSession: true);

    private readonly Configuration config;
    private readonly Action saveConfig;
    private readonly IPluginLog log;
    private readonly Func<string> language;
    private readonly Func<DateTime> utcNow;
    private readonly string pendingPath;
    private readonly string version;
    private readonly int build;
    private readonly HttpClient http;
    private readonly CancellationTokenSource cts = new();

    private readonly object gate = new();
    /// <summary>UTC day (yyyy-MM-dd) → wire key → count. Guarded by <see cref="gate"/>.</summary>
    private SortedDictionary<string, Dictionary<string, int>> pending = new(StringComparer.Ordinal);
    private readonly HashSet<UsageFeature> countedThisSession = new();
    private bool dirty;
    private int sending;

    /// <param name="saveConfig">Persists <paramref name="config"/>; a delegate so tests need no plugin interface.</param>
    /// <param name="language">The Dalamud UI language code, read at send time.</param>
    /// <param name="handler">Test seam only; null in the plugin.</param>
    /// <param name="utcNow">Test seam only; null in the plugin.</param>
    public UsageStats(Configuration config, Action saveConfig, string dataDir, IPluginLog log,
                      Func<string> language, string version, int build,
                      HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null)
    {
        this.config = config;
        this.saveConfig = saveConfig;
        this.log = log;
        this.language = language;
        this.version = version;
        this.build = build;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        pendingPath = Path.Combine(dataDir, PendingFile);

        // One client per instance, disposed with it: a static one's pool timers would root the plugin's
        // collectible load context past unload (see ResilientDownloader). No User-Agent: the server reads no
        // headers, and nothing is sent that it does not need.
        http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        http.Timeout = TimeSpan.FromSeconds(10);

        if (config.UsageStatsConsent && config.UsageStatsInstallId != null)
            Load();
    }

    /// <summary>Whether counting is on. Both halves, so a hand-edited config with one and not the other stays off.</summary>
    public bool Enabled => config.UsageStatsConsent && config.UsageStatsInstallId != null;

    /// <summary>The user has never answered, or answered a previous version of the question.</summary>
    public bool NeedsConsentPrompt => config.UsageStatsConsentVersion < ConsentVersion;

    /// <summary>An erasure request that has not reached the server yet; retried on every load.</summary>
    public bool DeletionPending => config.UsageStatsPendingDeletion != null;

    public Guid? InstallId => config.UsageStatsInstallId;

    /// <summary>Makes this the instance <see cref="Count"/> reaches, and starts the background sender.</summary>
    public void Start()
    {
        Current = this;
        if (Enabled) MarkActiveToday();
        Task.Run(() => RunAsync(cts.Token));
    }

    // ── consent ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The user said yes: a fresh random install ID, and today counts as an active day.</summary>
    public void OptIn()
    {
        config.UsageStatsConsent = true;
        config.UsageStatsConsentVersion = ConsentVersion;
        config.UsageStatsInstallId ??= Guid.NewGuid();
        saveConfig();
        MarkActiveToday();
        log.Information("[Proteus] Usage statistics: opted in");
    }

    /// <summary>The user said no. Remembered, so the prompt does not come back until <see cref="ConsentVersion"/> changes.</summary>
    public void Decline()
    {
        config.UsageStatsConsentVersion = ConsentVersion;
        saveConfig();
        if (config.UsageStatsConsent || config.UsageStatsInstallId != null) WithdrawAndDelete();
        log.Information("[Proteus] Usage statistics: declined");
    }

    /// <summary>
    /// Stops collecting, forgets the install ID and everything not yet sent, and erases what the server holds
    /// for that ID. The erasure is recorded before it is attempted, so a failure (offline, server down) is
    /// retried on the next load instead of being lost.
    /// </summary>
    public void WithdrawAndDelete()
    {
        var id = config.UsageStatsInstallId;
        config.UsageStatsConsent = false;
        config.UsageStatsInstallId = null;
        if (id != null) config.UsageStatsPendingDeletion = id;
        saveConfig();

        lock (gate)
        {
            pending.Clear();
            countedThisSession.Clear();
            dirty = false;
        }
        TryDeleteFile();

        if (id != null) _ = Task.Run(() => SendPendingDeletionAsync(cts.Token));
        log.Information("[Proteus] Usage statistics: withdrawn, local data cleared");
    }

    // ── counting ────────────────────────────────────────────────────────────────────────────────────

    private void Add(UsageFeature feature, bool oncePerSession)
    {
        if (!Enabled) return;
        lock (gate)
        {
            if (oncePerSession && !countedThisSession.Add(feature)) return;
            var day = Today();
            if (!pending.TryGetValue(day, out var counts)) pending[day] = counts = new(StringComparer.Ordinal);
            var key = WireKey(feature);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            dirty = true;
        }
    }

    /// <summary>An empty entry for today, so a day the plugin ran but no counted feature was used still reports.</summary>
    private void MarkActiveToday()
    {
        lock (gate)
        {
            var day = Today();
            if (pending.ContainsKey(day)) return;
            pending[day] = new(StringComparer.Ordinal);
            dirty = true;
        }
    }

    private string Today() => utcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The name a feature is sent under. Never rename one: that splits its history in two.</summary>
    public static string WireKey(UsageFeature f) => f switch
    {
        UsageFeature.Composite         => "composite",
        UsageFeature.SecondSkin        => "second_skin",
        UsageFeature.StudioOpen        => "studio_open",
        UsageFeature.LiveBrush         => "live_brush",
        UsageFeature.MeshToggle        => "mesh_toggle",
        UsageFeature.ImportOnion       => "import_onion",
        UsageFeature.ImportPmp         => "import_pmp",
        UsageFeature.ImportTtmp2       => "import_ttmp2",
        UsageFeature.ImportEmissive    => "import_emissive",
        UsageFeature.ImportEye         => "import_eye",
        UsageFeature.ImportInstalled   => "import_installed",
        UsageFeature.PresetSave        => "preset_save",
        UsageFeature.PresetApply       => "preset_apply",
        UsageFeature.DesignBindCapture => "design_bind_capture",
        UsageFeature.DesignBindRestore => "design_bind_restore",
        UsageFeature.HatCompat         => "hat_compat",
        UsageFeature.ColorsetEdit      => "colorset_edit",
        UsageFeature.MasksEdit         => "masks_edit",
        UsageFeature.ModCreate         => "mod_create",
        UsageFeature.ModExport         => "mod_export",
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, null),
    };

    // ── persistence ─────────────────────────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(pendingPath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(pendingPath));
            if (loaded == null) return;
            lock (gate)
                pending = new SortedDictionary<string, Dictionary<string, int>>(
                    loaded.ToDictionary(kv => kv.Key, kv => new Dictionary<string, int>(kv.Value, StringComparer.Ordinal)),
                    StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // A corrupt tally costs a few days of counts, nothing more; never let it break the plugin.
            log.Warning(ex, "[Proteus] Usage statistics: unreadable pending file, starting fresh");
            TryDeleteFile();
        }
    }

    /// <summary>Writes the tally if it changed. Never writes without consent: no consent, no file.</summary>
    internal void Flush()
    {
        string? json = null;
        lock (gate)
        {
            if (!dirty) return;
            dirty = false;
            if (Enabled) json = JsonSerializer.Serialize(pending);
        }
        if (json == null) return;
        try
        {
            var tmp = pendingPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, pendingPath, overwrite: true);
        }
        catch (Exception ex) { log.Warning(ex, "[Proteus] Usage statistics: could not save pending counts"); }
    }

    private void TryDeleteFile()
    {
        try { if (File.Exists(pendingPath)) File.Delete(pendingPath); }
        catch (Exception ex) { log.Warning(ex, "[Proteus] Usage statistics: could not delete pending counts"); }
    }

    // ── sending ─────────────────────────────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Not at load: the plugin is busy with its first composite, and nothing here is urgent.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            while (!ct.IsCancellationRequested)
            {
                Flush();
                await SendPendingDeletionAsync(ct);
                await SendFinishedDaysAsync(ct);
                // Hourly, so a session running past midnight UTC sends the day it just finished.
                await Task.Delay(TimeSpan.FromHours(1), ct);
                if (Enabled) MarkActiveToday();
            }
        }
        catch (OperationCanceledException) { /* plugin unloading */ }
        catch (Exception ex) { log.Warning(ex, "[Proteus] Usage statistics: sender stopped"); }
    }

    /// <summary>
    /// Sends every finished day, oldest first, removing each one the server accepts. Stops at the first
    /// failure and keeps the rest for the next attempt. Returns how many days were sent.
    /// </summary>
    internal async Task<int> SendFinishedDaysAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref sending, 1) != 0) return 0;
        try
        {
            int sent = 0;
            foreach (var (day, body) in TakeFinishedDays())
            {
                if (!Enabled) break;   // withdrawn mid-send
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                HttpResponseMessage res;
                try { res = await http.PostAsync(Endpoint + "v1/report", content, ct); }
                catch (HttpRequestException ex) { log.Debug("[Proteus] Usage statistics: offline ({0})", ex.Message); break; }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { break; }   // timeout

                using (res)
                {
                    if (res.IsSuccessStatusCode || (int)res.StatusCode == 400)
                    {
                        // 400 is "the server will never accept this" (a key it does not know yet, a day too old):
                        // dropped like a success, or it would be retried forever.
                        if (!res.IsSuccessStatusCode)
                            log.Warning("[Proteus] Usage statistics: report for {0} refused ({1}); dropped", day,
                                await res.Content.ReadAsStringAsync(ct));
                        lock (gate) { pending.Remove(day); dirty = true; }
                        sent++;
                    }
                    else break;   // 429 / 5xx: try again next hour
                }
            }
            Flush();
            return sent;
        }
        finally { Volatile.Write(ref sending, 0); }
    }

    /// <summary>Report bodies for every day before today, oldest first; drops days too old to send.</summary>
    internal List<(string Day, string Body)> TakeFinishedDays()
    {
        var id = config.UsageStatsInstallId;
        if (!Enabled || id == null) return new();

        var today = Today();
        var oldest = utcNow().AddDays(-MaxPendingDays).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var lang = language();
        var result = new List<(string, string)>();

        lock (gate)
        {
            foreach (var day in pending.Keys.ToList())
            {
                if (string.CompareOrdinal(day, oldest) < 0) { pending.Remove(day); dirty = true; continue; }
                if (string.CompareOrdinal(day, today) >= 0) continue;
                result.Add((day, BuildReport(id.Value, day, version, build, lang, pending[day])));
            }
        }
        return result;
    }

    /// <summary>
    /// The exact bytes that leave the machine. Every field here is listed in PRIVACY.md and accepted by
    /// <c>validateReport</c> in stats/src/index.js, which refuses anything else.
    /// </summary>
    internal static string BuildReport(Guid id, string day, string version, int build, string lang,
                                       IReadOnlyDictionary<string, int> counts)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["v"] = 1,
            ["id"] = id.ToString("D"),
            ["day"] = day,
            ["version"] = version,
            ["build"] = build,
            ["lang"] = NormalizeLang(lang),
            ["counts"] = counts.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
        });

    /// <summary>Dalamud's code, reduced to what the server accepts (2-3 lowercase letters); anything odd is "xx".</summary>
    internal static string NormalizeLang(string lang)
    {
        var l = (lang ?? "").Trim().ToLowerInvariant();
        return l.Length is >= 2 and <= 3 && l.All(c => c is >= 'a' and <= 'z') ? l : "xx";
    }

    /// <summary>Carries out a recorded erasure request; clears the record once the server confirms.</summary>
    internal async Task SendPendingDeletionAsync(CancellationToken ct)
    {
        var id = config.UsageStatsPendingDeletion;
        if (id == null) return;
        try
        {
            // A report already on the wire could otherwise land AFTER the delete and survive it. The client
            // timeout is 10 s, so waiting out one send is bounded.
            for (int i = 0; i < 30 && Volatile.Read(ref sending) != 0; i++)
                await Task.Delay(500, ct);

            using var res = await http.DeleteAsync(Endpoint + "v1/install/" + id.Value.ToString("D"), ct);
            if (!res.IsSuccessStatusCode)
            {
                log.Warning("[Proteus] Usage statistics: deletion returned {0}; will retry", (int)res.StatusCode);
                return;
            }
            // Only if it is still the same request: a newer opt-in-and-withdraw may have replaced it.
            if (config.UsageStatsPendingDeletion == id)
            {
                config.UsageStatsPendingDeletion = null;
                saveConfig();
            }
            log.Information("[Proteus] Usage statistics: server data deleted");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.Debug("[Proteus] Usage statistics: deletion not sent ({0}); will retry", ex.Message); }
    }

    /// <summary>Sends finished days now instead of waiting for the hourly tick. For <c>/proteus stats send</c>.</summary>
    public Task<int> SendNowAsync(bool includeToday)
    {
        if (includeToday)
        {
            // Testing aid: close today off early by relabelling it as yesterday, so it goes out now.
            lock (gate)
            {
                var today = Today();
                if (pending.Remove(today, out var counts))
                {
                    var yesterday = utcNow().AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    if (pending.TryGetValue(yesterday, out var y))
                        foreach (var (k, n) in counts) y[k] = y.TryGetValue(k, out var m) ? m + n : n;
                    else pending[yesterday] = counts;
                    dirty = true;
                }
            }
        }
        return SendFinishedDaysAsync(cts.Token);
    }

    /// <summary>A one-line readout of the tally for <c>/proteus stats</c>.</summary>
    public string Describe()
    {
        if (!Enabled) return DeletionPending ? "off (server deletion pending)" : "off";
        lock (gate)
            return $"on, id {config.UsageStatsInstallId}; pending: " + (pending.Count == 0 ? "none" :
                string.Join("; ", pending.Select(d => d.Key + " {" +
                    string.Join(", ", d.Value.Select(kv => $"{kv.Key}={kv.Value}")) + "}")));
    }

    public void Dispose()
    {
        if (Current == this) Current = null;
        cts.Cancel();
        Flush();
        http.Dispose();
    }
}
