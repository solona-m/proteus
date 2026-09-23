using System;

namespace Proteus.Services;

/// <summary>
/// The one door every write to the player's OWN gear goes through — the carrier items Proteus equips to host a shell.
/// <para/>
/// Its whole job is an ordering: the "am I still here?" question is asked inside the delegate that does the write, so
/// that both happen in the same framework tick as each other and as teardown, which runs there too. Asked before the
/// delegate instead there is a window between deciding and writing, and it is seconds wide — deciding to equip a
/// carrier means a Glamourer read first.
/// <para/>
/// That window is not theoretical. A composite outlives the instance that started it: it is a Task and nothing waits
/// for it. Measured on a plugin reload, one ran 9.5 seconds and equipped the facewear carrier 2.2 seconds after its
/// instance had been disposed — so the item went onto the player while the record that owns it went down with the
/// dead instance. The live one had no record of equipping it, would not take it off (it will not remove a pair it
/// did not put on), and the player was left wearing an eyepatch nothing claimed.
/// </summary>
internal static class CarrierWrites
{
    /// <summary>
    /// Do <paramref name="write"/> on the framework thread, unless this instance is gone by the time it runs.
    /// </summary>
    /// <param name="onFramework">Runs a delegate on the framework thread and returns what it returned.</param>
    /// <param name="goneAway">
    /// Whether this instance has been torn down. Called INSIDE <paramref name="onFramework"/> — that is the point of
    /// this method, and hoisting it out is the bug it exists to prevent.
    /// </param>
    /// <param name="write">The game write itself. Never called once <paramref name="goneAway"/> says so.</param>
    /// <returns>What the write returned, or false when it was refused — which every caller already reads as
    /// "did not happen", so no record of equipping is kept either.</returns>
    internal static bool Guarded(Func<Func<bool>, bool> onFramework, Func<bool> goneAway, Func<bool> write)
        => onFramework(() => !goneAway() && write());

    /// <summary>
    /// Give up this instance's hold on the player's gear: take the carriers off, and only then stop allowing writes.
    /// <para/>
    /// The order is the whole of it. Marked gone first, the removals below would refuse themselves through
    /// <see cref="Guarded"/> and teardown would leave on the player exactly the items it exists to take off — a
    /// failure that looks identical to the one the guard was added to fix, from the other direction.
    /// </summary>
    /// <param name="removeCarriers">Take off what this instance put on.</param>
    /// <param name="markGone">Refuse every later write — see <see cref="Guarded"/>.</param>
    internal static void Teardown(Action removeCarriers, Action markGone)
    {
        // Finally, not just after: a removal can throw — Glamourer is disposed around the same time as us — and an
        // instance that gave up its carriers but stayed writable is the worst of both, since the composite still
        // running behind it would put them back on with nobody left to remember.
        try { removeCarriers(); }
        finally { markGone(); }
    }
}
