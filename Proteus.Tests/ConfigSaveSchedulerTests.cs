using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using NSubstitute;
using Proteus;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The settings save: written on the framework thread only, inside <see cref="Configuration.Gate"/>, and never thrown
/// at its caller. A player's auto refit reported "Couldn't refit" after the option had been switched on, because the
/// save in the same step serialised <see cref="Configuration.KnownBodyMods"/> while a worker added to it.
/// </summary>
public class ConfigSaveSchedulerTests
{
    /// <summary>A fake framework: a flag the test flips, and the posted callbacks queued until it pumps them.</summary>
    private sealed class Rig
    {
        public readonly object Gate = new();
        public readonly IPluginLog Log = Substitute.For<IPluginLog>();
        public readonly Queue<Action> Posted = new();
        public bool OnFramework = true;
        public int Writes;
        public readonly List<(bool Framework, bool InGate)> Seen = [];
        public Action? WriteBody;
        public Action<Action>? PostOverride;
        public readonly ConfigSaveScheduler Saver;

        public Rig()
        {
            Saver = new ConfigSaveScheduler(Gate, () =>
                {
                    Seen.Add((OnFramework, Monitor.IsEntered(Gate)));
                    Writes++;
                    WriteBody?.Invoke();
                }, () => OnFramework,
                a => { if (PostOverride != null) PostOverride(a); else Posted.Enqueue(a); }, Log);
        }

        public void Pump()
        {
            bool was = OnFramework;
            OnFramework = true;
            while (Posted.Count > 0) Posted.Dequeue()();
            OnFramework = was;
        }
    }

    // [AC-config-save-race-1] [EDGE-config-save-race-7]
    [Fact]
    [Trait("Plan", "AC-config-save-race-1")]
    [Trait("Plan", "EDGE-config-save-race-7")]
    public void A_save_whose_write_throws_does_not_throw_to_the_caller()
    {
        var rig = new Rig
        {
            WriteBody = () => throw new InvalidOperationException(
                "Collection was modified; enumeration operation may not execute."),
        };
        var config = new Configuration();
        config.AttachSaver(rig.Saver);

        // The exact call AutoRefitWatcher.Apply makes inside its framework step, after the option is switched on.
        config.Save();

        Assert.Equal(1, rig.Writes);
        Assert.True(rig.Saver.Pending);
        rig.Log.Received(1).Error(Arg.Any<Exception>(), Arg.Any<string>(), Arg.Any<object[]>());
    }

    // [AC-config-save-race-2] [EDGE-config-save-race-12]
    [Fact]
    [Trait("Plan", "AC-config-save-race-2")]
    [Trait("Plan", "EDGE-config-save-race-12")]
    public void Writes_run_only_on_the_framework_thread_inside_the_gate()
    {
        var rig = new Rig();
        rig.Saver.Save("ui");
        rig.OnFramework = false;
        rig.Saver.Save("worker");
        rig.Saver.Save("worker");
        Assert.Equal(1, rig.Writes);
        rig.Pump();

        Assert.Equal(2, rig.Writes);
        Assert.All(rig.Seen, s => Assert.True(s.Framework && s.InGate));
    }

    /// <summary>
    /// Real threads, real serialiser, a real config. The worker ADDS new keys under the gate (only an add breaks a
    /// running enumeration on .NET), the framework thread changes other collections in place and saves, as Apply and
    /// the GUI do. Without the gate the framework's serialise trips on the worker's adds.
    /// </summary>
    // [AC-config-save-race-2]
    [Fact]
    [Trait("Plan", "AC-config-save-race-2")]
    public void Concurrent_changes_never_break_the_save()
    {
        var config = new Configuration();
        var log = Substitute.For<IPluginLog>();
        var queue = new BlockingCollection<Action>();
        Thread? fw = null;
        string json = "";
        var saver = new ConfigSaveScheduler(config.Gate,
            () => json = JsonConvert.SerializeObject(config, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Objects,
            }),
            () => Thread.CurrentThread == fw, a => queue.Add(a), log);
        config.AttachSaver(saver);
        var pref = new AutoRefitPreference();
        config.AutoRefitByCollection["c"] = pref;

        const int rounds = 600;
        fw = new Thread(() =>
        {
            foreach (var a in queue.GetConsumingEnumerable()) a();
        }) { IsBackground = true };
        fw.Start();

        // Adds new keys the whole time the framework thread is serialising, and clears now and then to stay small;
        // both break a running enumeration, an overwrite would not.
        using var stop = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            for (int i = 0; !stop.IsSet; i++)
            {
                lock (config.Gate)
                {
                    if (i % 512 == 0) config.KnownBodyMods.Clear();
                    config.KnownBodyMods["mod" + i] = new BodyModCacheEntry { Fingerprint = i };
                }
                if (i % 64 == 63) config.Save();
                // A real worker is not a hot loop; without this the gate starves the framework thread under load.
                Thread.Yield();
            }
            lock (config.Gate) config.KnownBodyMods["mod-last"] = new BodyModCacheEntry();
            config.Save();
        }) { IsBackground = true };
        worker.Start();

        for (int i = 0; i < rounds; i++)
        {
            int n = i;
            queue.Add(() =>
            {
                pref.SwitchedOn.Add("dir|" + n);
                config.RetargetTargets["slot" + n] = "body";
                config.OverlayStackOrder["k" + n] = ["a"];
                config.Save();
            });
        }

        using var framed = new ManualResetEventSlim();
        queue.Add(framed.Set);
        Assert.True(framed.Wait(TimeSpan.FromSeconds(180)));
        stop.Set();
        Assert.True(worker.Join(TimeSpan.FromSeconds(60)));
        using var done = new ManualResetEventSlim();
        queue.Add(() => { saver.Save("final"); done.Set(); });
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)));
        queue.CompleteAdding();

        log.DidNotReceive().Error(Arg.Any<Exception>(), Arg.Any<string>(), Arg.Any<object[]>());
        Assert.Contains("mod-last", json);
        Assert.Contains("slot" + (rounds - 1), json);
        Assert.Contains("dir|" + (rounds - 1), json);
    }

    // [AC-config-save-race-3] [EDGE-config-save-race-1]
    [Fact]
    [Trait("Plan", "AC-config-save-race-3")]
    [Trait("Plan", "EDGE-config-save-race-1")]
    public void Background_saves_merge_into_one_write()
    {
        var rig = new Rig { OnFramework = false };
        for (int i = 0; i < 100; i++) rig.Saver.Save("worker");

        Assert.Equal(0, rig.Writes);
        Assert.Single(rig.Posted);
        rig.Pump();
        Assert.Equal(1, rig.Writes);
        Assert.False(rig.Saver.Pending);
    }

    // [AC-config-save-race-3] [EDGE-config-save-race-5]
    [Fact]
    [Trait("Plan", "AC-config-save-race-3")]
    [Trait("Plan", "EDGE-config-save-race-5")]
    public void A_save_after_the_tick_posts_again()
    {
        var rig = new Rig { OnFramework = false };
        rig.Saver.Save("worker");
        rig.Pump();
        rig.Saver.Save("worker");
        Assert.Single(rig.Posted);
        rig.Pump();
        Assert.Equal(2, rig.Writes);
    }

    /// <summary>A worker's save that lands while the posted write is running is posted again, not lost.</summary>
    // [AC-config-save-race-3] [EDGE-config-save-race-5]
    [Fact]
    [Trait("Plan", "AC-config-save-race-3")]
    [Trait("Plan", "EDGE-config-save-race-5")]
    public void A_save_during_the_posted_write_posts_again()
    {
        var rig = new Rig { OnFramework = false };
        bool once = true;
        rig.WriteBody = () =>
        {
            if (!once) return;
            once = false;
            rig.OnFramework = false;
            rig.Saver.Save("during");
            rig.OnFramework = true;
        };
        rig.Saver.Save("worker");
        rig.Pump();

        Assert.Equal(2, rig.Writes);
        Assert.False(rig.Saver.Pending);
    }

    // [AC-config-save-race-4] [EDGE-config-save-race-8]
    [Fact]
    [Trait("Plan", "AC-config-save-race-4")]
    [Trait("Plan", "EDGE-config-save-race-8")]
    public void Unload_writes_pending_changes_then_drops_later_ones()
    {
        var rig = new Rig { OnFramework = false };
        rig.Saver.Save("worker");

        rig.Saver.FlushAndClose();
        Assert.Equal(1, rig.Writes);

        // The stale callback from before the unload, and a save from a task still running after it.
        rig.Pump();
        rig.Saver.Save("late worker");
        rig.OnFramework = true;
        rig.Saver.Save("late framework");

        Assert.Equal(1, rig.Writes);
        Assert.Empty(rig.Posted);
        rig.Log.Received().Debug(Arg.Is<string>(s => s.Contains("dropped")), Arg.Any<object[]>());
    }

    // [AC-config-save-race-5] [EDGE-config-save-race-14]
    [Fact]
    [Trait("Plan", "AC-config-save-race-5")]
    [Trait("Plan", "EDGE-config-save-race-14")]
    public void A_failed_write_is_retried_by_the_next_save()
    {
        var rig = new Rig();
        bool fail = true;
        rig.WriteBody = () => { if (fail) throw new System.IO.IOException("disk"); };

        rig.Saver.Save("ui");
        Assert.True(rig.Saver.Pending);
        // Not retried by itself every tick: nothing is posted.
        Assert.Empty(rig.Posted);

        fail = false;
        rig.Saver.Save("ui");
        Assert.Equal(2, rig.Writes);
        Assert.False(rig.Saver.Pending);
    }

    // [AC-config-save-race-5]
    [Fact]
    [Trait("Plan", "AC-config-save-race-5")]
    public void A_lasting_failure_logs_one_error_not_one_per_save()
    {
        var rig = new Rig { WriteBody = () => throw new System.IO.IOException("disk") };
        for (int i = 0; i < 10; i++) rig.Saver.Save("ui");

        Assert.Equal(10, rig.Writes);
        rig.Log.Received(1).Error(Arg.Any<Exception>(), Arg.Any<string>(), Arg.Any<object[]>());
    }

    // [AC-config-save-race-6] [EDGE-config-save-race-10]
    [Fact]
    [Trait("Plan", "AC-config-save-race-6")]
    [Trait("Plan", "EDGE-config-save-race-10")]
    public void Gate_and_scheduler_are_not_persisted()
    {
        var config = new Configuration { AutoHatCompat = false, DecodeCacheBudgetMb = 777 };
        config.KnownBodyMods["m"] = new BodyModCacheEntry { Fingerprint = 5 };
        config.AttachSaver(new Rig().Saver);
        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects };

        var json = JsonConvert.SerializeObject(config, settings);
        Assert.DoesNotContain("Gate", json);
        Assert.DoesNotContain("saver", json, StringComparison.OrdinalIgnoreCase);

        var back = JsonConvert.DeserializeObject<Configuration>(json, settings)!;
        Assert.NotNull(back.Gate);
        Assert.False(back.AutoHatCompat);
        Assert.Equal(777, back.DecodeCacheBudgetMb);
        Assert.Equal(5, back.KnownBodyMods["m"].Fingerprint);
    }

    // [EDGE-config-save-race-2]
    [Fact]
    [Trait("Plan", "EDGE-config-save-race-2")]
    public void A_framework_save_waits_for_a_worker_change_in_progress()
    {
        var rig = new Rig();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            lock (rig.Gate)
            {
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10)));

        var save = new Thread(() => rig.Saver.Save("ui")) { IsBackground = true };
        save.Start();
        Assert.False(save.Join(200));
        Assert.Equal(0, rig.Writes);

        release.Set();
        Assert.True(save.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, rig.Writes);
    }

    // [EDGE-config-save-race-3]
    [Fact]
    [Trait("Plan", "EDGE-config-save-race-3")]
    public void A_framework_save_from_inside_the_gate_is_reentrant()
    {
        var rig = new Rig();
        lock (rig.Gate) rig.Saver.Save("ui");
        Assert.Equal(1, rig.Writes);
    }

    // [EDGE-config-save-race-4]
    [Fact]
    [Trait("Plan", "EDGE-config-save-race-4")]
    public void A_framework_save_settles_a_pending_posted_flush()
    {
        var rig = new Rig { OnFramework = false };
        rig.Saver.Save("worker");
        rig.OnFramework = true;
        rig.Saver.Save("ui");
        rig.Pump();
        Assert.Equal(1, rig.Writes);
    }

    // [EDGE-config-save-race-6]
    [Fact]
    [Trait("Plan", "EDGE-config-save-race-6")]
    public void A_post_that_throws_keeps_the_change_and_the_next_save_posts_again()
    {
        var rig = new Rig { OnFramework = false, PostOverride = _ => throw new ObjectDisposedException("framework") };
        rig.Saver.Save("worker");

        Assert.True(rig.Saver.Pending);
        rig.Log.Received().Warning(Arg.Any<Exception>(), Arg.Any<string>(), Arg.Any<object[]>());

        // The framework back: the next worker save posts again rather than waiting for the unload.
        rig.PostOverride = null;
        rig.Saver.Save("worker");
        Assert.Single(rig.Posted);
        rig.Pump();
        Assert.Equal(1, rig.Writes);
        Assert.False(rig.Saver.Pending);
    }

    /// <summary>A framework save stuck behind a worker holding the gate is logged even when the write is quick.</summary>
    // [EDGE-config-save-race-13]
    [Fact]
    [Trait("Plan", "EDGE-config-save-race-13")]
    public void A_slow_wait_for_the_gate_is_logged()
    {
        var rig = new Rig();
        var save = new Thread(() => rig.Saver.Save("ui")) { IsBackground = true };
        lock (rig.Gate)
        {
            save.Start();
            // Held until the save is blocked on the gate, then a while longer: its wait is measured, not guessed.
            var until = DateTime.UtcNow.AddSeconds(10);
            while ((save.ThreadState & ThreadState.WaitSleepJoin) == 0 && DateTime.UtcNow < until) Thread.Yield();
            Thread.Sleep((int)ConfigSaveScheduler.SlowWriteMs + 40);
        }
        Assert.True(save.Join(TimeSpan.FromSeconds(10)));
        rig.Log.Received(1).Warning(Arg.Is<string>(m => m.Contains("waited")), Arg.Any<object[]>());
    }

    /// <summary>The auto refit's worker reads a copy of the collection's preference, every member of it.</summary>
    // [AC-config-save-race-2]
    [Fact]
    [Trait("Plan", "AC-config-save-race-2")]
    public void A_preference_copy_has_every_member_and_shares_no_collection()
    {
        var live = new AutoRefitPreference
        {
            CollectionName = "c", BodyDir = "body",
            Chest = new BodySizeRef { Rel = "chest" }, Legs = new BodySizeRef { Rel = "legs" },
        };
        live.SwitchedOn.Add("m|g");
        live.AuthorSwitchedOn["m|g"] = "S";
        live.Before["m|g"] = ["a"];
        live.HeldBefore["m|g"] = ["b"];
        live.InheritedBefore.Add("m");
        live.Declined.Add("p|m");

        var copy = live.Copy();
        Assert.Equal(JsonConvert.SerializeObject(live), JsonConvert.SerializeObject(copy));
        // What the worker decides on. Replaced, never changed in place, so shared on purpose.
        Assert.Same(live.Chest, copy.Chest);
        Assert.Same(live.Legs, copy.Legs);
        // A member added later fails here until it is copied in Copy() and filled on `live` above.
        Assert.True(typeof(AutoRefitPreference).GetProperties().Length == 10,
                    "AutoRefitPreference gained a member: copy it in Copy(), then fill it here and update this count");

        live.SwitchedOn.Add("x");
        live.AuthorSwitchedOn["x"] = "x";
        live.Before["m|g"].Add("x");
        live.HeldBefore["x"] = [];
        live.InheritedBefore.Add("x");
        live.Declined.Add("x");
        Assert.DoesNotContain("\"x\"", JsonConvert.SerializeObject(copy));
        Assert.Contains("M|G", copy.SwitchedOn);   // case-insensitive, as the live sets
        Assert.True(copy.AuthorSwitchedOn.ContainsKey("M|G"));
    }

    // [EDGE-config-save-race-9]
    [Fact]
    [Trait("Plan", "EDGE-config-save-race-9")]
    public void Flush_with_nothing_pending_writes_nothing()
    {
        var rig = new Rig();
        rig.Saver.FlushAndClose();
        Assert.Equal(0, rig.Writes);
    }

    // [EDGE-config-save-race-11]
    [Fact]
    [Trait("Plan", "EDGE-config-save-race-11")]
    public void An_unattached_config_builds_migrates_and_flushes_as_a_no_op()
    {
        var config = new Configuration();
        config.Migrate();
        Assert.NotNull(config.Gate);
        // Nothing attached: the unload flush is a no-op rather than a throw.
        config.FlushAndCloseSaves();
    }

    // [EDGE-config-save-race-13]
    [Fact]
    [Trait("Plan", "EDGE-config-save-race-13")]
    public void A_slow_write_is_logged_once_not_on_every_save()
    {
        var rig = new Rig { WriteBody = () => Thread.Sleep((int)ConfigSaveScheduler.SlowWriteMs + 20) };
        rig.Saver.Save("ui");
        rig.Saver.Save("ui");
        rig.Saver.Save("ui");
        rig.Log.Received(1).Warning(Arg.Is<string>(s => s.Contains("took")), Arg.Any<object[]>());
        rig.Log.Received(2).Debug(Arg.Is<string>(s => s.Contains("took")), Arg.Any<object[]>());
    }

    /// <summary>Dalamud may run a RunOnTick action inline on the caller while it unloads.</summary>
    // [EDGE-config-save-race-15]
    [Fact]
    [Trait("Plan", "EDGE-config-save-race-15")]
    public void A_post_that_runs_inline_writes_under_the_gate_without_deadlock()
    {
        var rig = new Rig { OnFramework = false };
        rig.PostOverride = a => a();
        var t = new Thread(() => rig.Saver.Save("worker")) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, rig.Writes);
        Assert.True(rig.Seen[0].InGate);
        Assert.False(rig.Saver.Pending);
    }
}
