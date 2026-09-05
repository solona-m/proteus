using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Vector3 = System.Numerics.Vector3;

namespace Proteus.Interop;

/// <summary>
/// How much light is actually falling on the wearer, 0 (pitch dark) to 1 (full daylight).
///
/// This is what makes a glow light-sensitive: a row's emissive is scaled by <c>1 − response × level</c>, so
/// a dark-only tattoo reaches full brightness in an unlit cellar and disappears under a midday sky.
///
/// <para><b>Why it is computed and not read.</b> The per-pixel lighting a character actually receives exists
/// only inside the game's pixel shader. Getting it back would mean replacing the shader (which is what
/// Atramentum Luminis did, and the thing Proteus exists to not require) or reading back a render target
/// every frame — a GPU→CPU stall, in screen space rather than UV space, and wrong the moment the character
/// is off-screen or behind a wall. So we add up the lights instead.</para>
///
/// <para><b>Two terms.</b> The zone's PLACED lights — lamps, braziers, dungeon torches, housing lights, the
/// gpose rig — are real <see cref="LightLayoutInstance"/>s and are summed with their own colour, intensity,
/// range and falloff at the probe's position. That is what makes indoors work, where a clock-based guess
/// says nothing. On top sits a sky term from the time of day, which only applies where there IS a sky:
/// a layout with no outdoor data (a house interior, a dungeon) gets none, so its placed lights are the
/// whole answer.</para>
///
/// <para><b>Sampled per body part, not once per character.</b> A chest piece beside a lamp should read
/// brighter than one on the far side of the same body, so probes sit at a few heights up the character and
/// each shell layer asks for the one nearest its own surface.</para>
///
/// <para>Evaluated on a timer rather than per frame, and smoothed, so nothing pops when a zone loads or a
/// light streams in. Everything downstream quantises the result, so a still light level costs nothing at
/// all.</para>
/// </summary>
public sealed unsafe class SceneLightService : IDisposable
{
    /// <summary>Heights above the character's origin the probes sit at, in game units (roughly metres).
    /// Ankle, hip, chest — enough to tell a floor lamp from a ceiling one without pretending to a precision
    /// this estimate doesn't have.</summary>
    private static readonly float[] ProbeHeights = [0.15f, 0.9f, 1.45f];

    /// <summary>How often the lights are re-summed. A light level does not change fast, and the walk is the
    /// only part of this that is not free.</summary>
    private const double EvaluateIntervalSeconds = 0.25;

    /// <summary>Seconds for a change to travel ~63% of the way to its new value. Long enough that stepping
    /// through a doorway reads as a fade rather than a switch, short enough to keep up with a walk.</summary>
    private const float SmoothingSeconds = 0.6f;

    /// <summary>Lights further than this from a probe are skipped before any maths.</summary>
    private const float MaxLightDistance = 25f;

    /// <summary>How far a light that declares no range of its own is taken to reach — a lamp lighting the
    /// space around it, not a floodlight aimed at the character.</summary>
    private const float DefaultLightRange = 8f;

    /// <summary>
    /// Maps summed irradiance onto 0–1 as <c>1 − exp(−k·E)</c>. Saturating on purpose: a room with six
    /// lamps in it is not six times as bright as a room with one, and a linear map would let one bright
    /// light pin every dark-only tattoo off permanently.
    /// </summary>
    private const float IrradianceCurve = 1.6f;

    /// <summary>
    /// The most the zone's placed lights may ever contribute. **The sky is the only thing that can take a
    /// dark-only glow all the way out.**
    /// <para/>
    /// Not a taste call — a correctness one. A light's <c>Intensity</c> is in units nothing here can
    /// calibrate against: it is whatever the zone artist typed, and the curve above saturates by an
    /// irradiance of about 2, so a handful of street lamps within range summed straight to 0.98 on a night
    /// street that looked pitch black. Capping the term bounds that whole class of mistake — get the
    /// magnitude wrong now and a tattoo is dimmer than it should be near a lamp, instead of absent in the
    /// dark. For a cosmetic effect those two failures are not remotely equal.
    /// </summary>
    private const float LampCeiling = 0.35f;

    /// <summary>What a zone with no sky and no placed light still counts as. Not zero: even a black cave
    /// renders the character faintly, and a floor at exactly 0 would make the feature look broken (a glow
    /// snapping to full) rather than dark.</summary>
    private const float FloorAmbient = 0.02f;

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private readonly float[] _smoothed = new float[ProbeHeights.Length];
    private bool _seeded;
    private DateTime _lastEvaluate = DateTime.MinValue;

    // Diagnostics for the Settings readout — the raw pieces behind the one number, so "why is my tattoo
    // off in here" has an answer without a debugger.
    public int LightsCounted { get; private set; }
    public int LightsSeen { get; private set; }
    public float SkyTerm { get; private set; }
    public float PlacedTerm { get; private set; }
    public bool HasSky { get; private set; }

    /// <summary>The raw layout/environment signals behind <see cref="HasSky"/>. Surfaced in the readout
    /// because deciding "is there a sky over me" from them is the part of this estimate most likely to be
    /// wrong, and a screenshot of these three answers it in one step.</summary>
    public bool Outdoor { get; private set; }
    public bool Indoor { get; private set; }
    public bool InEnvSpace { get; private set; }

    public SceneLightService(IFramework framework, IObjectTable objects, Configuration config, IPluginLog log)
    {
        this.framework = framework;
        this.objects   = objects;
        this.config    = config;
        this.log       = log;
        framework.Update += OnFramework;
    }

    /// <summary>The level at chest height — the answer for anything that hasn't said where it sits.</summary>
    public float Level => Sample(ProbeHeights[^1]);

    /// <summary>
    /// The light level at <paramref name="height"/> above the character's origin, smoothed. Snaps to the
    /// nearest probe rather than interpolating: the probes are already an approximation of a field that
    /// varies smoothly, and interpolating between two approximations buys nothing.
    /// </summary>
    public float Sample(float height)
    {
        if (config.LightResponseManual)
            return Math.Clamp(config.LightResponseManualLevel, 0f, 1f);

        int best = 0;
        float bestGap = float.MaxValue;
        for (int i = 0; i < ProbeHeights.Length; i++)
        {
            float gap = MathF.Abs(ProbeHeights[i] - height);
            if (gap < bestGap) { bestGap = gap; best = i; }
        }
        return _smoothed[best];
    }

    private void OnFramework(IFramework fw)
    {
        // Deliberately still runs while the level is PINNED by hand. The pin is applied in Sample, at the
        // point of use, so nothing here can drift what a pinned character renders at — and the diagnostics
        // this keeps up to date are the whole reason anyone pins the level in the first place. Freezing them
        // instead made the panel show a live-looking sky term beside a reading that no longer came from it,
        // which is exactly the wrong lie to tell someone who has come here to find out why.
        if (!config.LightResponseEnabled) return;

        var now = DateTime.UtcNow;
        if ((now - _lastEvaluate).TotalSeconds < EvaluateIntervalSeconds) return;
        float dt = _seeded ? (float)Math.Min((now - _lastEvaluate).TotalSeconds, 1.0) : 0f;
        _lastEvaluate = now;

        var addr = objects.LocalPlayer?.Address ?? 0;
        if (addr == 0) return;

        Vector3 origin;
        try
        {
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)addr;
            origin = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
        }
        catch (Exception ex) { log.Error(ex, "[ProteusLight] could not read the player's position"); return; }

        float sky = SkyLevel();
        SkyTerm = sky;

        Span<float> raw = stackalloc float[ProbeHeights.Length];
        for (int i = 0; i < raw.Length; i++) raw[i] = 0f;
        LightsCounted = AccumulatePlacedLights(origin, raw);

        float maxPlaced = 0f;
        for (int i = 0; i < raw.Length; i++)
        {
            // Capped, so however wrong the summed magnitude is, lamplight can only ever dim a dark-only
            // glow — never switch it off. See LampCeiling.
            float lit = MathF.Min(LampCeiling, 1f - MathF.Exp(-IrradianceCurve * raw[i]));
            float level = Math.Clamp(MathF.Max(FloorAmbient, MathF.Max(sky, lit)), 0f, 1f);
            maxPlaced = MathF.Max(maxPlaced, lit);

            _smoothed[i] = _seeded ? Approach(_smoothed[i], level, dt) : level;
        }
        PlacedTerm = maxPlaced;
        _seeded = true;
    }

    /// <summary>Exponential approach, framerate-independent: the same wall-clock time gets the same
    /// distance travelled whether we ticked twice or twenty times.</summary>
    private static float Approach(float current, float target, float dt)
        => current + (target - current) * (1f - MathF.Exp(-dt / SmoothingSeconds));

    /// <summary>
    /// The daylight term, 0–1, or 0 where there is no sky to let it in.
    /// <para/>
    /// Analytic rather than read: the game's own ambient and sun colours live in EnvState, whose layout is
    /// not mapped in the ClientStructs we build against (only <c>Rain</c> is), so there is nothing to read
    /// yet. This is the one piece of the estimate that is a model, and it is deliberately the piece that
    /// only matters OUTDOORS — where a clock is a decent proxy — while indoors, which a clock cannot
    /// describe at all, is carried entirely by the placed lights.
    /// </summary>
    private float SkyLevel()
    {
        var env = EnvManager.Instance();
        if (env == null) { HasSky = false; return 0f; }

        var world = LayoutWorld.Instance();
        var layout = world == null ? null : world->ActiveLayout;

        // Three signals, recorded whether or not they are acted on, because "is there a sky over me" turned
        // out not to be one field. Outdoor data alone said YES inside a building, and a noon sky leaking
        // into an interior takes every dark-only tattoo in it out — the exact failure this feature must not
        // have.
        Outdoor  = layout != null && layout->OutdoorAreaData != null;
        Indoor   = layout != null && layout->IndoorAreaData != null;
        InEnvSpace = env->EnvSpace != null;

        // Indoor data VETOES outdoor data. A house interior carries both — it is part of a ward that has an
        // outdoor layout — so the two are not alternatives and the more specific one has to win.
        //
        // EnvSpace is recorded but deliberately NOT acted on yet: the game uses env-space volumes for
        // interiors and caves, which would make it the better discriminator, but they also appear outdoors
        // for weather and area overrides, and acting on that guess would take the sky away in the open.
        HasSky = Outdoor && !Indoor;
        if (!HasSky) return 0f;

        return SkyFromTime(env->DayTimeSeconds / 86400f, env->EnvState.Rain);
    }

    /// <summary>
    /// The daylight term from the clock and the rain, both 0–1 in and 0–1 out. Split out from the game
    /// reads so it can be checked without a running client: sun elevation as a sine peaking at noon,
    /// clamped at zero through the night rather than going negative, then dimmed by rain.
    /// <para/>
    /// Rain is the one weather value FFXIVClientStructs maps for us, and it stands in for cloud well enough
    /// — a downpour is dim even at midday. Halved at most, because an overcast noon is still nowhere near
    /// dark and a tattoo vanishing in the rain would read as a bug.
    /// </summary>
    internal static float SkyFromTime(float dayFraction, float rain)
    {
        float t = dayFraction - MathF.Floor(dayFraction);
        float day = Math.Clamp(MathF.Sin((t - 0.25f) * 2f * MathF.PI), 0f, 1f);
        return Math.Clamp(day * (1f - 0.5f * Math.Clamp(rain, 0f, 1f)), 0f, 1f);
    }

    /// <summary>
    /// Sum the zone's placed lights into <paramref name="into"/>, one entry per probe height. Returns how
    /// many lights CONTRIBUTED, and records how many were live at all in <see cref="LightsSeen"/> — the two
    /// numbers together say whether a room reading dark has no lights or merely none that reached.
    /// </summary>
    private int AccumulatePlacedLights(Vector3 origin, Span<float> into)
    {
        LightsSeen = 0;

        var world = LayoutWorld.Instance();
        var layout = world == null ? null : world->ActiveLayout;
        if (layout == null) return 0;

        var key = InstanceType.Light;
        if (!layout->InstancesByType.TryGetValuePointer(in key, out var bucketPtr)
            || bucketPtr == null || bucketPtr->Value == null)
            return 0;

        int counted = 0, seen = 0;
        foreach (var entry in *bucketPtr->Value)
        {
            var instance = (LightLayoutInstance*)entry.Item2.Value;
            if (instance == null || !instance->IsActive) continue;

            var scene = instance->GraphicsObject;
            if (scene == null || !scene->IsVisible) continue;

            var render = scene->RenderLight;
            if (render == null) continue;

            seen++;
            var pos = new Vector3(scene->Position.X, scene->Position.Y, scene->Position.Z);

            // A light that declares no range gets a SMALL one, not the cull distance. Treating Range 0 as
            // "reaches 60 units" made every such light a floodlight on the character — a large part of how a
            // dark street summed to nearly full daylight — but dropping those lights entirely goes too far
            // the other way and can leave a lit room reading as pitch black.
            float range = render->Range > 0f ? render->Range : DefaultLightRange;

            // Colour × intensity as a single luminance. A blue lamp and a white one of the same wattage do
            // not light a room equally, and Rec.709 is the same weighting the eye applies.
            var c = render->Color;
            float lum = (0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z) * MathF.Max(render->Intensity, 0f);
            if (lum <= 0f) continue;

            bool contributed = false;
            for (int i = 0; i < into.Length; i++)
            {
                var probe = origin with { Y = origin.Y + ProbeHeights[i] };
                float d = Vector3.Distance(pos, probe);
                if (d > MathF.Min(range, MaxLightDistance)) continue;

                float atten = Attenuate(d, range, render->FalloffType, render->FalloffFactor,
                                        render->LightShape);
                if (atten <= 0f) continue;

                into[i] += lum * atten;
                contributed = true;
            }
            if (contributed) counted++;
        }
        LightsSeen = seen;
        return counted;
    }

    /// <summary>
    /// How much of a light survives the trip to the probe. A WorldLight is directional — the zone's own
    /// sun/moon rig when one is placed — so it does not fall off with distance at all; everything else
    /// fades to nothing at its Range along the curve the light itself declares.
    /// </summary>
    internal static float Attenuate(float distance, float range, LightFalloffType falloff, float factor,
                                    LightShape shape)
    {
        // A WorldLight is the zone's own directional rig, not a lamp in a room: it has no position to be far
        // from. It is also NOT counted toward the placed term for that reason — see the caller.
        if (shape == LightShape.WorldLight) return 1f;
        if (range <= 0f) return 0f;

        float x = Math.Clamp(1f - distance / range, 0f, 1f);
        float curved = falloff switch
        {
            LightFalloffType.Linear    => x,
            LightFalloffType.Cubic     => x * x * x,
            _                          => x * x,   // Quadratic, and the safe default for anything new
        };
        // FalloffFactor sharpens or softens that curve; a zero or absurd value means "leave it alone"
        // rather than "no light", which is how an unset field reads in the wild.
        return factor is > 0f and < 8f ? MathF.Pow(curved, factor) : curved;
    }

    public void Dispose() => framework.Update -= OnFramework;
}
