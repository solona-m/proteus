using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The opt-in usage statistics. These guard the promises in PRIVACY.md: nothing is counted, written or sent
/// without consent; a report carries exactly the listed fields; withdrawing erases locally and on the server.
/// </summary>
public sealed class UsageStatsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "proteus-usage-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler handler = new();
    private DateTime now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    public UsageStatsTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }

    private string PendingPath => Path.Combine(dir, "usage-pending.json");

    private UsageStats Make(Configuration config)
        => new(config, () => { }, dir, Substitute.For<IPluginLog>(), () => "en", "2609.1.0.0", 924,
               handler, () => now);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Requests = new();
        public HttpStatusCode Status = HttpStatusCode.NoContent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(Status);
        }
    }

    // ── no consent, nothing happens ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutConsentNothingIsCountedWrittenOrSent()
    {
        var config = new Configuration();
        using var stats = Make(config);

        Assert.False(stats.Enabled);
        Assert.True(stats.NeedsConsentPrompt);

        stats.Start();
        UsageStats.Count(UsageFeature.Composite);
        UsageStats.CountOncePerSession(UsageFeature.StudioOpen);
        stats.Flush();

        now = now.AddDays(1);
        Assert.Equal(0, await stats.SendFinishedDaysAsync(CancellationToken.None));
        Assert.False(File.Exists(PendingPath));
        Assert.Empty(handler.Requests);
        Assert.Null(config.UsageStatsInstallId);
    }

    [Fact]
    public void DecliningIsRememberedAndCreatesNoId()
    {
        var config = new Configuration();
        using var stats = Make(config);

        stats.Decline();

        Assert.False(stats.NeedsConsentPrompt);
        Assert.False(config.UsageStatsConsent);
        Assert.Null(config.UsageStatsInstallId);
        Assert.Null(config.UsageStatsPendingDeletion);
    }

    [Fact]
    public void AnAnswerToAnOlderQuestionIsAskedAgain()
    {
        var config = new Configuration { UsageStatsConsentVersion = UsageStats.ConsentVersion - 1 };
        using var stats = Make(config);
        Assert.True(stats.NeedsConsentPrompt);
    }

    // ── counting and sending ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FinishedDaysAreSentOnceAndTodayWaits()
    {
        var config = new Configuration();
        using var stats = Make(config);
        stats.Start();
        stats.OptIn();

        UsageStats.Count(UsageFeature.Composite);
        UsageStats.Count(UsageFeature.Composite);
        UsageStats.CountOncePerSession(UsageFeature.ColorsetEdit);
        UsageStats.CountOncePerSession(UsageFeature.ColorsetEdit);
        stats.Flush();
        Assert.True(File.Exists(PendingPath));

        // Same day: nothing to send yet.
        Assert.Equal(0, await stats.SendFinishedDaysAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);

        now = now.AddDays(1);
        Assert.Equal(1, await stats.SendFinishedDaysAsync(CancellationToken.None));
        var (method, url, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal(UsageStats.Endpoint + "v1/report", url);

        using var doc = JsonDocument.Parse(body);
        var counts = doc.RootElement.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("composite").GetInt32());
        Assert.Equal(1, counts.GetProperty("colorset_edit").GetInt32());   // once per session
        Assert.Equal("2026-09-18", doc.RootElement.GetProperty("day").GetString());

        // Sent days are gone; a second attempt sends nothing.
        Assert.Equal(0, await stats.SendFinishedDaysAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AServerFailureKeepsTheDayForNextTime()
    {
        var config = new Configuration();
        using var stats = Make(config);
        stats.Start();
        stats.OptIn();
        UsageStats.Count(UsageFeature.HatCompat);

        now = now.AddDays(1);
        handler.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(0, await stats.SendFinishedDaysAsync(CancellationToken.None));

        handler.Status = HttpStatusCode.NoContent;
        Assert.Equal(1, await stats.SendFinishedDaysAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DaysTooOldForTheServerAreDroppedUnsent()
    {
        var config = new Configuration();
        using var stats = Make(config);
        stats.Start();
        stats.OptIn();
        UsageStats.Count(UsageFeature.Composite);

        now = now.AddDays(UsageStats.MaxPendingDays + 1);
        Assert.Equal(0, await stats.SendFinishedDaysAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CountsSurviveARestart()
    {
        var config = new Configuration();
        using (var first = Make(config))
        {
            first.Start();
            first.OptIn();
            UsageStats.Count(UsageFeature.ModExport);
        }   // Dispose flushes

        using var second = Make(config);
        second.Start();
        now = now.AddDays(1);
        Assert.Equal(1, await second.SendFinishedDaysAsync(CancellationToken.None));
        Assert.Contains("\"mod_export\":1", handler.Requests.Single().Body);
    }

    // ── what leaves the machine ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AReportCarriesExactlyTheFieldsPrivacyMdLists()
    {
        var body = UsageStats.BuildReport(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), "2026-09-18",
            "2609.1.0.0", 924, "en", new Dictionary<string, int> { ["composite"] = 3, ["mod_export"] = 0 });

        using var doc = JsonDocument.Parse(body);
        var fields = doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "build", "counts", "day", "id", "lang", "v", "version" }, fields);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", doc.RootElement.GetProperty("id").GetString());

        // Zero counts are not sent; the server refuses them.
        var counts = doc.RootElement.GetProperty("counts").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "composite" }, counts);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("ZH", "zh")]
    [InlineData("", "xx")]
    [InlineData("zh-TW", "xx")]
    [InlineData("english", "xx")]
    public void LanguageIsReducedToWhatTheServerAccepts(string given, string sent)
        => Assert.Equal(sent, UsageStats.NormalizeLang(given));

    /// <summary>
    /// The server refuses a report carrying any key it does not list, so a key added here and not there
    /// would have every report for that day dropped. Read from the worker source, the single allow-list.
    /// </summary>
    [Fact]
    public void WireKeysMatchTheServersAllowList()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, "Could not find proteus.slnx above the test binaries.");

        var js = File.ReadAllText(Path.Combine(dir!.FullName, "stats", "src", "features.js"));
        var list = Regex.Match(js, @"FEATURES\s*=\s*\[(?<body>[^\]]*)\]").Groups["body"].Value;
        var server = Regex.Matches(list, "'([a-z0-9_]+)'").Select(m => m.Groups[1].Value).Order().ToArray();

        var client = Enum.GetValues<UsageFeature>().Select(UsageStats.WireKey).Order().ToArray();

        Assert.Equal(client.Length, client.Distinct().Count());
        Assert.Equal(server, client);
    }

    // ── withdrawal ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithdrawingClearsEverythingAndErasesTheServerCopy()
    {
        var config = new Configuration();
        using var stats = Make(config);
        stats.Start();
        stats.OptIn();
        var id = config.UsageStatsInstallId!.Value;
        UsageStats.Count(UsageFeature.Composite);
        stats.Flush();
        Assert.True(File.Exists(PendingPath));

        stats.WithdrawAndDelete();

        Assert.False(stats.Enabled);
        Assert.Null(config.UsageStatsInstallId);
        Assert.False(File.Exists(PendingPath));

        // The erasure is recorded first, then carried out; running it again is what a reload does.
        await stats.SendPendingDeletionAsync(CancellationToken.None);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Delete
                                            && r.Url == UsageStats.Endpoint + "v1/install/" + id.ToString("D"));
        Assert.Null(config.UsageStatsPendingDeletion);

        // Counting after withdrawal does nothing.
        UsageStats.Count(UsageFeature.Composite);
        stats.Flush();
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task AFailedErasureIsKeptForRetry()
    {
        var config = new Configuration();
        using var stats = Make(config);
        stats.Start();
        stats.OptIn();
        var id = config.UsageStatsInstallId;

        handler.Status = HttpStatusCode.ServiceUnavailable;
        stats.WithdrawAndDelete();
        await stats.SendPendingDeletionAsync(CancellationToken.None);
        Assert.Equal(id, config.UsageStatsPendingDeletion);
        Assert.True(stats.DeletionPending);

        handler.Status = HttpStatusCode.NoContent;
        await stats.SendPendingDeletionAsync(CancellationToken.None);
        Assert.Null(config.UsageStatsPendingDeletion);
    }

    [Fact]
    public void OptingInAgainAfterWithdrawingGetsANewId()
    {
        var config = new Configuration();
        using var stats = Make(config);
        stats.OptIn();
        var first = config.UsageStatsInstallId;
        stats.WithdrawAndDelete();
        stats.OptIn();
        Assert.NotNull(config.UsageStatsInstallId);
        Assert.NotEqual(first, config.UsageStatsInstallId);
    }
}
