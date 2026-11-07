using Glamourer.Api.Enums;

namespace Proteus.Interop;

/// <summary>
/// Which Glamourer state changes mean "the player's equipment moved". Glamourer raises StateChanged synchronously inside
/// SetItem/SetBonusItem, so an Equip raised on our own thread while our carrier write is in progress is our echo: it must
/// not start a recomposite (a user's log showed every carrier ask followed by a skipped "glamourer-equip" composite).
/// </summary>
internal static class GlamourerSignals
{
    public static bool RaisesEquipmentChange(StateChangeType type, bool withinReapplyEcho, bool ownCarrierWriteOnThisThread)
        => type is StateChangeType.Equip or StateChangeType.BonusItem
        && !withinReapplyEcho
        && !ownCarrierWriteOnThisThread;
}
