using System;
using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Proteus.Interop.Ipc;

namespace Proteus.TestHarness;

/// <summary>
/// Glamourer's design-apply gate (docs/ipc-contract.md §6, H1). Harness builds only (ADR 0002): never in the shipped
/// bridge and never in <see cref="IpcContract.Entries"/>.
/// </summary>
internal sealed class GlamourerApplyDesign(IDalamudPluginInterface pi)
    : IpcFunc<Guid, int, uint, ulong, int>(pi, Label)
{
    public const string Label = "Glamourer.ApplyDesign";

    public GlamourerApiEc Invoke(Guid designId, int objectIndex, uint key, ApplyFlag flags)
        => (GlamourerApiEc)Call(designId, objectIndex, key, (ulong)flags);
}
