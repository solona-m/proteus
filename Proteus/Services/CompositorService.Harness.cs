#if PROTEUS_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// The test harness's view into the boot probe (ADR 0002): re-arm it as a zone change would, and read where its back-off
/// stands. Compiled out of every CI build with the rest of the harness. Any thread (the gate is locked).
/// </summary>
public partial class CompositorService
{
    /// <summary>What the character going away does: the probe looks again from the start, its latch cleared. A re-arm
    /// with a Proteus mod enabled runs a normal boot composite, as every zone change does.</summary>
    internal void HarnessRearmBoot()
    {
        _boot.Rearm(Environment.TickCount64);
        Volatile.Write(ref _bootIdleLogged, 0);
    }

    /// <summary>The probe's latch, ladder step and time to the next look.</summary>
    internal (bool Latched, int Step, long NextDueInMs) HarnessBootState() => _boot.Snapshot(Environment.TickCount64);

    // ── issue sweep D (#91): the auto-redraw override and the shell load fault ──────────────────────────────────────

    /// <summary>The folder of Proteus's own output mod.</summary>
    internal string ManagedOutputDir => managedModDir;

    /// <summary>The harness's session-only auto redraw switch (null = the setting). Never saved; put-back and unload clear it.</summary>
    internal bool? HarnessAutoRedrawOverride { get; set; }

    private readonly object _shellFaultGate = new();
    private ShellFault? _shellFault;

    /// <summary>An armed shell load fault: which shell's which texture slot to leave out of the manifest, until when, and
    /// what it left out at the last publish.</summary>
    internal sealed class ShellFault
    {
        public required string Shell { get; init; }
        public required string Slot { get; init; }
        public required string Until { get; init; }
        public List<(string GamePath, string Rel)> Withheld { get; set; } = [];
        public string? LastHeal { get; set; }
    }

    /// <summary>Arms the fault; false when one is armed already.</summary>
    internal bool HarnessArmShellFault(string shell, string slot, string until)
    {
        lock (_shellFaultGate)
        {
            if (_shellFault is { LastHeal: null }) return false;
            _shellFault = new ShellFault { Shell = shell, Slot = slot, Until = until };
            return true;
        }
    }

    /// <summary>Clears the fault (true when one was there); the caller recomposites so the manifest is whole again.</summary>
    internal bool HarnessClearShellFault()
    {
        lock (_shellFaultGate)
        {
            bool had = _shellFault != null;
            _shellFault = null;
            return had;
        }
    }

    /// <summary>The fault's state for /state, or null: no names, only game paths of Proteus's own output.</summary>
    internal object? HarnessShellFaultState()
    {
        lock (_shellFaultGate)
            return _shellFault is not { } f ? null : new
            {
                armed = f.LastHeal == null,
                shell = f.Shell,
                slot = f.Slot,
                until = f.Until,
                withheld = f.Withheld.Select(w => w.GamePath).ToList(),
                lastHeal = f.LastHeal,
            };
    }

    /// <summary>The write-retry latch and the drawn check's last steps, for /state.</summary>
    internal object HarnessRecoveryState()
    {
        long now = Environment.TickCount64;
        return new
        {
            writeRetry = new
            {
                lastDecision = _writeRetry.LastKind switch
                {
                    OutputWriteRetry.Kind.Clean => "clean",
                    OutputWriteRetry.Kind.Retry => "retry",
                    OutputWriteRetry.Kind.GiveUp => "give-up",
                    _ => null,
                },
                attempt = _writeRetry.LastAttempt,
                scheduledRetries60s = _writeRetry.ScheduledInLast60s(now),
                outputIncomplete = _shellOutputIncomplete,
                timerPending = _writeRetryTimers.Count > 0,
            },
            shellDrawn = new
            {
                lastStep = _shellDrawnLastStep,
                autoRedraws5m = AutoRedrawsInWindow(),
                autoRedrawKeySet = _shellAutoRedrawKey != null,
                noticeKeySet = _shellNotDrawnNoticeKey != null,
                checksInFlight = _shellChecksInFlight.Count,
            },
            shellFault = HarnessShellFaultState(),
            moveFault = HarnessMoveFault.State is { } m ? new { patterns = m.Patterns, movesLeft = m.MovesLeft } : null,
        };
    }

    /// <summary>
    /// Leaves the armed fault's shell texture out of the manifest about to be written (just before WriteManagedModJson in
    /// the composite), recording what it left out. The game path then resolves to nothing, the shell's material fails to
    /// load and the shell is partly drawn. Not a write failure: nothing is counted.
    /// </summary>
    internal void HarnessFilterPublish(IDictionary<string, string> redirects)
    {
        lock (_shellFaultGate)
        {
            if (_shellFault is not { LastHeal: null } f) return;
            f.Withheld = WithholdShellTexture(redirects, f.Shell, f.Slot);
            if (f.Withheld.Count > 0)
                log.Information("[Proteus] test harness: shell fault — left {0} out of the manifest",
                    string.Join(", ", f.Withheld.Select(w => w.GamePath)));
        }
    }

    /// <summary>
    /// Removes <c>…/texture/{shell}_{slot}.tex</c> redirects (<paramref name="shell"/> "ss_1", or "any" for the
    /// lowest-lettered shell present) and returns what it removed.
    /// </summary>
    internal static List<(string GamePath, string Rel)> WithholdShellTexture(IDictionary<string, string> redirects,
                                                                            string shell, string slot)
    {
        string Leaf(string k) => k[(k.LastIndexOf('/') + 1)..];
        var suffix = "_" + slot + ".tex";
        var candidates = redirects.Keys
            .Where(k => k.Contains("/texture/", StringComparison.OrdinalIgnoreCase)
                     && Leaf(k).StartsWith("ss_", StringComparison.OrdinalIgnoreCase)
                     && Leaf(k).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        string? target = shell;
        if (string.Equals(shell, "any", StringComparison.OrdinalIgnoreCase))
            target = candidates.Select(k => Leaf(k)[..^suffix.Length])
                               .OrderBy(s => s.Length).ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                               .FirstOrDefault();
        if (target == null) return [];
        var hit = candidates.Where(k => string.Equals(Leaf(k), target + suffix, StringComparison.OrdinalIgnoreCase)).ToList();
        var removed = new List<(string, string)>();
        foreach (var k in hit)
        {
            removed.Add((k, redirects[k]));
            redirects.Remove(k);
        }
        return removed;
    }

    /// <summary>
    /// Just before Proteus's own automatic shell redraw: heal an <c>until:"auto-redraw"</c> fault, so the redraw draws the
    /// shell. Rebuilt from the live manifest — files and manipulations verbatim (dropping the manipulations un-loads the
    /// carrier host, see the upstream prime) — plus only the withheld paths, recorded as a publish so prune and verify see
    /// it. Gives up, writing nothing, when a composite became busy or the probe was superseded.
    /// </summary>
    private void HarnessBeforeOwnShellRedraw(ShellDrawnProbe expected)
    {
        List<(string GamePath, string Rel)> withheld;
        lock (_shellFaultGate)
        {
            if (_shellFault is not { LastHeal: null, Until: "auto-redraw" } f || f.Withheld.Count == 0) return;
            withheld = f.Withheld;
        }

        string result;
        try
        {
            if (CompositeBusy || !ReferenceEquals(_shellDrawnCheck, expected))
                result = "gave-up";
            else if (PenumbraModMeta.TryReadDefaultData(managedModDir) is not { } live)
                result = "gave-up";
            else
            {
                var healed = new Dictionary<string, string>(live.Files, StringComparer.OrdinalIgnoreCase);
                foreach (var (gamePath, rel) in withheld) healed[gamePath] = rel;
                WriteManagedModJson(healed, live.Manipulations);
                penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
                var previous = RecordPublish(healed);
                WaitForManifestLive(healed, previous);
                result = "healed";
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] test harness: shell fault heal failed");
            result = "gave-up";
        }

        lock (_shellFaultGate)
            if (_shellFault != null) _shellFault.LastHeal = result;   // disarmed either way
        log.Information("[Proteus] test harness: shell fault {0} before the automatic redraw", result);
    }
}
#endif

