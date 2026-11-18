using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Proteus.Interop.Ipc;

namespace Proteus.TestHarness;

/// <summary>
/// Glamourer's state-apply gate (docs/ipc-contract.md §6, H2). The state crosses as object: Glamourer takes a JObject
/// (what the harness sends) or a base64 string. Harness builds only (ADR 0002): never in the shipped bridge and never
/// in <see cref="IpcContract.Entries"/>.
/// </summary>
internal sealed class GlamourerApplyState(IDalamudPluginInterface pi)
    : IpcFunc<object, int, uint, ulong, int>(pi, Label)
{
    public const string Label = "Glamourer.ApplyState";

    public GlamourerApiEc Invoke(JObject state, int objectIndex, uint key, ApplyFlag flags)
        => (GlamourerApiEc)Call(state, objectIndex, key, (ulong)flags);
}
