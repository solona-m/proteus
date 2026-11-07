using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Interop;

namespace Proteus.Services.Matcap;

/// <summary>The Penumbra calls the library selection makes, as a seam so it is tested without the game.</summary>
internal interface IMatcapPenumbra
{
    (Guid Id, string Name)? PlayerCollection();
    PenumbraBridge.ModSettingsSnapshot? Settings(Guid collection, string mod);
    PenumbraBridge.TemporaryHold? Hold(Guid collection, string mod);
    bool SetEnabled(Guid collection, string mod, bool enabled);
    bool SetOption(Guid collection, string mod, string group, string option);
    IReadOnlyDictionary<string, string?>? Resolve(IReadOnlyList<string> gamePaths);
    string? ModsRoot();
}

/// <summary><see cref="IMatcapPenumbra"/> over the real bridge. Framework thread, as every IPC write.</summary>
internal sealed class PenumbraMatcapAdapter(PenumbraBridge penumbra) : IMatcapPenumbra
{
    public (Guid Id, string Name)? PlayerCollection() => penumbra.GetPlayerCollection();
    public PenumbraBridge.ModSettingsSnapshot? Settings(Guid c, string mod) => penumbra.GetModSettingsSnapshot(c, mod);
    public PenumbraBridge.TemporaryHold? Hold(Guid c, string mod) => penumbra.QueryTemporaryModSettings(c, mod);
    public bool SetEnabled(Guid c, string mod, bool enabled) =>
        penumbra.SetModEnabled(c, mod, enabled) is Penumbra.Api.Enums.PenumbraApiEc.Success or Penumbra.Api.Enums.PenumbraApiEc.NothingChanged;
    public bool SetOption(Guid c, string mod, string group, string option) =>
        penumbra.SetModOption(c, mod, group, [option]) is Penumbra.Api.Enums.PenumbraApiEc.Success or Penumbra.Api.Enums.PenumbraApiEc.NothingChanged;
    public IReadOnlyDictionary<string, string?>? Resolve(IReadOnlyList<string> gamePaths) => penumbra.ResolvePlayerPaths(gamePaths);
    public string? ModsRoot() => penumbra.GetModDirectory();
}

/// <summary>
/// Selecting a library's matcap: the chosen option switched on in the player's collection, with everything about that
/// write said first — the library is off (switching it on also publishes its other files, so only after the user says
/// so), the setting is inherited (a local one is made), it is the Base collection (everyone using it sees the change),
/// a temporary setting holds it (the choice shows when that is released), or another mod wins the slot path.
/// Undo never touches a library's settings: other mods may read the same slot.
/// </summary>
internal static class MatcapSelection
{
    /// <summary>How a library stands in the player's collection.</summary>
    public sealed record Status(bool Known, string Collection, bool Enabled, bool Inherited, string? HeldBy,
                                string? CurrentOption, bool IsBase);

    public static Status Read(IMatcapPenumbra p, MatcapLibrary lib)
    {
        if (p.PlayerCollection() is not { } c) return new Status(false, "", false, false, null, null, false);
        var s = p.Settings(c.Id, lib.Mod);
        var hold = p.Hold(c.Id, lib.Mod);
        string? current = s?.Options.TryGetValue(lib.Group, out var sel) == true ? sel.FirstOrDefault() : null;
        return new Status(true, c.Name, s?.Enabled ?? false, s?.Inherited ?? false,
                          hold is { Present: true } h ? (h.Source.Length > 0 ? h.Source : "?") : null, current, IsBaseName(c.Name));
    }

    /// <summary>Penumbra's base collection, which every actor without an assignment of their own uses.</summary>
    public static bool IsBaseName(string name) =>
        name.Equals("Base", StringComparison.OrdinalIgnoreCase) || name.Equals("Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a selection did, and what it was before (the harness puts that back).</summary>
    public sealed record Result(string Code, string Message, bool EnabledBefore, string? OptionBefore, bool InheritedBefore,
                                bool Winning, string? WinningFile)
    {
        public bool Ok => Code.Length == 0;
    }

    public const string CodeNeedsConfirm = "needs-confirm", CodeNoPenumbra = "penumbra-unavailable", CodeFailed = "failed";

    private static string L(string key, string fallback) => ProteusLoc.Localize(key, fallback);

    /// <summary>
    /// Switch <paramref name="option"/> on. A library that is off is switched on only with
    /// <paramref name="confirmEnable"/>; otherwise nothing is written and the code asks for the confirmation.
    /// </summary>
    public static Result Select(IMatcapPenumbra p, MatcapLibrary lib, string option, bool confirmEnable)
    {
        var st = Read(p, lib);
        if (!st.Known || p.PlayerCollection() is not { } c)
            return new Result(CodeNoPenumbra, L("Parts.Matcap.NoPenumbra", "Penumbra is not available."), false, null, false, false, null);
        if (!st.Enabled && !confirmEnable)
            return new Result(CodeNeedsConfirm, string.Format(L("Parts.Matcap.ConfirmEnable.Fmt",
                "{0} is off in {1}. Switching it on also turns on: {2}"), lib.Name, st.Collection, AlsoText(lib)),
                st.Enabled, st.CurrentOption, st.Inherited, false, null);

        if (!st.Enabled && !p.SetEnabled(c.Id, lib.Mod, true))
            return new Result(CodeFailed, L("Parts.Matcap.SelectFailed", "Penumbra did not accept the library's setting."), st.Enabled, st.CurrentOption, st.Inherited, false, null);
        if (!p.SetOption(c.Id, lib.Mod, lib.Group, option))
            return new Result(CodeFailed, L("Parts.Matcap.SelectFailed", "Penumbra did not accept the library's setting."), st.Enabled, st.CurrentOption, st.Inherited, false, null);

        // Who draws the slot now: the chosen option's file, or another mod's.
        bool winning = true;
        string? file = null;
        if (p.Resolve([lib.Texture]) is { } resolved && resolved.TryGetValue(lib.Texture, out var f) && f != null)
        {
            file = f;
            var want = lib.Options.FirstOrDefault(o => o.Name == option)?.File;
            winning = want != null && p.ModsRoot() is { } root
                      && PenumbraModMeta.SameFile(Path.GetFullPath(f), Path.GetFullPath(Path.Combine(root, lib.Mod, want.Replace('\\', Path.DirectorySeparatorChar))));
        }
        return new Result("", "", st.Enabled, st.CurrentOption, st.Inherited, winning, file);
    }

    /// <summary>The library's other default redirects, for the confirmation: five, then "+N more".</summary>
    public static string AlsoText(MatcapLibrary lib)
    {
        if (lib.AlsoRedirects.Count == 0) return L("Parts.Matcap.AlsoNothing", "nothing else");
        var shown = string.Join(", ", lib.AlsoRedirects.Take(5));
        return lib.AlsoRedirects.Count > 5
            ? shown + " " + string.Format(L("Parts.Matcap.AlsoMore.Fmt", "+{0} more"), lib.AlsoRedirects.Count - 5)
            : shown;
    }
}
