using Dalamud.Plugin;
using Penumbra.Api.Enums;
using Proteus.Interop.Ipc;

namespace Proteus.TestHarness;

/// <summary>
/// Penumbra's mod-package installer (docs/ipc-contract.md §6, H3): Penumbra imports the .pmp itself, in the background, into
/// its own mod root. Harness builds only (ADR 0002): never in the shipped bridge and never in <see cref="IpcContract.Entries"/>.
/// </summary>
internal sealed class PenumbraInstallMod(IDalamudPluginInterface pi)
    : IpcFunc<string, int>(pi, Label)
{
    public const string Label = "Penumbra.InstallMod.V5";

    public PenumbraApiEc Invoke(string modFilePackagePath) => (PenumbraApiEc)Call(modFilePackagePath);
}
