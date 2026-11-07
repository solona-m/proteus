namespace Proteus.Services;

/// <summary>
/// Glamourer's empty gear slot. It is not 0: Glamourer stores "Nothing" as <c>ItemManager.NothingId(slot)</c> =
/// uint.MaxValue - 128 - slot index, and serialises that as the slot's ItemId (a user's log shows 无 (4294967155)).
/// </summary>
internal static class GlamourerItemIds
{
    internal const ulong NothingIdHigh = uint.MaxValue - 128UL;
    internal const ulong NothingIdLow  = NothingIdHigh - 31UL;

    public static bool IsEmpty(ulong id) => id == 0 || id is >= NothingIdLow and <= NothingIdHigh;
}
