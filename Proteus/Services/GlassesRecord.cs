namespace Proteus.Services;

/// <summary>
/// What to do with the record of having equipped the invisible-glasses carrier
/// (<see cref="Configuration.InjectedGlasses"/>), given one reading of Glamourer's state and one of the models the
/// game is drawing.
/// <para/>
/// Pure, and separate from <see cref="CompositorService"/>, because the record is load-bearing in a way that is easy
/// to miss: the carrier is a real item a player can own, nothing ever re-adopts one, and the record is the only thing
/// that lets Proteus take its own pair back off. Clear it while the pair is still on the player's face and the carrier
/// shows its real frames for good — Proteus will neither hide it (the shell moves to another host) nor remove it
/// ("no record of equipping it"). The player's only way out is to clear the slot in Glamourer by hand.
/// <para/>
/// The reading that gets this wrong is "no glasses worn" taken while our carrier's model is still drawn.
/// <see cref="CompositorService.ReconcileInvisibleGlasses"/> runs off the redraw hook, so the state read and the model
/// walk are from different instants, and mid-redraw they disagree. That is a race, not a removal.
/// </summary>
internal static class GlassesRecord
{
    internal enum Decision
    {
        /// <summary>The reading agrees with the record, or there is no record. Nothing to do.</summary>
        Keep,

        /// <summary>
        /// The reading cannot be trusted — Glamourer's state was unreadable, or it disagrees with the drawn models
        /// the way a redraw in flight makes it disagree. Hold the record and decide on a settled reading.
        /// </summary>
        Hold,

        /// <summary>Our pair is genuinely gone. Drop the record.</summary>
        Forget,

        /// <summary>
        /// Another item now draws our carrier's model — the player put their own pair on. Drop the record, and move
        /// the shell off it, or it renders on an item that is not ours to rewrite.
        /// </summary>
        ForgetAndRelease,
    }

    /// <param name="recorded">We recorded equipping the carrier (<see cref="Configuration.InjectedGlasses"/>).</param>
    /// <param name="wornRow">The Glasses sheet row Glamourer reports (0 = none), or null if its state was unreadable.</param>
    /// <param name="ourItemId">The carrier's own sheet row.</param>
    /// <param name="ourModelDrawn">The carrier's model set is among the head "_met" models the game is drawing.</param>
    internal static Decision Evaluate(bool recorded, ulong? wornRow, ulong ourItemId, bool ourModelDrawn)
    {
        // Nothing to lose: without a record there is no ownership to drop.
        if (!recorded) return Decision.Keep;

        // An unreadable state is not evidence of anything.
        if (wornRow is not { } worn) return Decision.Hold;

        // Still ours.
        if (worn == ourItemId) return Decision.Keep;

        if (worn == 0)
            // "Nothing worn" while the model is still on the character is the redraw race described above. Once the
            // redraw settles the model leaves the walk too, and the next reading forgets it properly.
            return ourModelDrawn ? Decision.Hold : Decision.Forget;

        // A different pair. It only needs releasing when it draws OUR carrier's model; otherwise ours simply went.
        return ourModelDrawn ? Decision.ForgetAndRelease : Decision.Forget;
    }
}
