using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Vector3 = System.Numerics.Vector3;

namespace Proteus.Interop;

/// <summary>
/// How much light is falling on the wearer, 0 (dark) to 1 (daylight), estimated rather than read: the zone's placed
/// <see cref="LightLayoutInstance"/>s plus a sky term where there is a sky. Sampled at a few probe heights, on a
/// timer, and smoothed.
/// </summary>
public sealed unsafe class SceneLightService : IDisposable
{
    /// <summary>Probe heights above the character's origin, in game units (roughly metres): ankle, hip, chest.</summary>
    private static readonly float[] ProbeHeights = [0.15f, 0.9f, 1.45f];

    /// <summary>How often the lights are re-summed, in seconds.</summary>
    private const double EvaluateIntervalSeconds = 0.25;

    /// <summary>Seconds for a change to travel ~63% of the way to its new value.</summary>
    private const float SmoothingSeconds = 0.6f;

    /// <summary>Lights further than this from a probe are skipped before any maths.</summary>
    private const float MaxLightDistance = 25f;

    /// <summary>How far a light that declares no range of its own is taken to reach, in game units.</summary>
    private const float DefaultLightRange = 8f;

    /// <summary>k in <c>1 − exp(−k·E)</c>, which maps summed irradiance onto 0–1, saturating on purpose.</summary>
    private const float IrradianceCurve = 1.6f;

    /// <summary>
    /// The most the zone's placed lights may ever contribute: only the sky can take a dark-only glow all the way out,
    /// since a light's <c>Intensity</c> units cannot be calibrated.
    /// </summary>
    private const float LampCeiling = 0.35f;

    /// <summary>The minimum level, for a zone with no sky and no placed light; not zero, as even a black cave renders faintly.</summary>
    private const float FloorAmbient = 0.02f;

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private readonly float[] _smoothed = new float[ProbeHeights.Length];
    private bool _seeded;
    private DateTime _lastEvaluate = DateTime.MinValue;

    // Diagnostics for the Settings readout: the raw pieces behind the level.
    public int LightsCounted { get; private set; }
    public int LightsSeen { get; private set; }
    public float SkyTerm { get; private set; }
    public float PlacedTerm { get; private set; }
    public bool HasSky { get; private set; }

    /// <summary>The raw layout/environment signals behind <see cref="HasSky"/>, for the readout.</summary>
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

    /// <summary>The smoothed light level at <paramref name="height"/> above the character's origin, from the nearest probe.</summary>
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
        // Still runs while the level is pinned by hand (the pin applies in Sample), so the diagnostics stay live.
        if (!config.PluginEnabled || !config.LightResponseEnabled) return;

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
            // Capped at LampCeiling: lamplight can only dim a dark-only glow, never switch it off.
            float lit = MathF.Min(LampCeiling, 1f - MathF.Exp(-IrradianceCurve * raw[i]));
            float level = Math.Clamp(MathF.Max(FloorAmbient, MathF.Max(sky, lit)), 0f, 1f);
            maxPlaced = MathF.Max(maxPlaced, lit);

            _smoothed[i] = _seeded ? Approach(_smoothed[i], level, dt) : level;
        }
        PlacedTerm = maxPlaced;
        _seeded = true;
    }

    /// <summary>Exponential approach, framerate-independent.</summary>
    private static float Approach(float current, float target, float dt)
        => current + (target - current) * (1f - MathF.Exp(-dt / SmoothingSeconds));

    /// <summary>
    /// The daylight term, 0–1, or 0 where there is no sky. Modelled from the clock: EnvState's ambient and sun colours
    /// are not mapped in ClientStructs (only <c>Rain</c> is).
    /// </summary>
    private float SkyLevel()
    {
        var env = EnvManager.Instance();
        if (env == null) { HasSky = false; return 0f; }

        var world = LayoutWorld.Instance();
        var layout = world == null ? null : world->ActiveLayout;

        Outdoor  = layout != null && layout->OutdoorAreaData != null;
        Indoor   = layout != null && layout->IndoorAreaData != null;
        InEnvSpace = env->EnvSpace != null;

        // Indoor data VETOES outdoor data: a house interior carries both. EnvSpace is recorded but not acted on,
        // since env-space volumes also appear outdoors.
        HasSky = Outdoor && !Indoor;
        if (!HasSky) return 0f;

        return SkyFromTime(env->DayTimeSeconds / 86400f, env->EnvState.Rain);
    }

    /// <summary>
    /// The daylight term from the day fraction and rain (all 0–1): a sine peaking at noon, zero at night, halved at
    /// most by rain.
    /// </summary>
    internal static float SkyFromTime(float dayFraction, float rain)
    {
        float t = dayFraction - MathF.Floor(dayFraction);
        float day = Math.Clamp(MathF.Sin((t - 0.25f) * 2f * MathF.PI), 0f, 1f);
        return Math.Clamp(day * (1f - 0.5f * Math.Clamp(rain, 0f, 1f)), 0f, 1f);
    }

    /// <summary>
    /// Sum the zone's placed lights into <paramref name="into"/>, one entry per probe height. Returns how many
    /// contributed; <see cref="LightsSeen"/> records how many were live.
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

            // A light that declares no range gets a small one, not the cull distance.
            float range = render->Range > 0f ? render->Range : DefaultLightRange;

            // Colour × intensity as a single Rec.709 luminance.
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
    /// How much of a light survives the trip to the probe. A directional WorldLight does not fall off; everything else
    /// fades to nothing at its Range along its declared curve.
    /// </summary>
    internal static float Attenuate(float distance, float range, LightFalloffType falloff, float factor,
                                    LightShape shape)
    {
        if (shape == LightShape.WorldLight) return 1f;
        if (range <= 0f) return 0f;

        float x = Math.Clamp(1f - distance / range, 0f, 1f);
        float curved = falloff switch
        {
            LightFalloffType.Linear    => x,
            LightFalloffType.Cubic     => x * x * x,
            _                          => x * x,   // Quadratic, and the safe default for anything new
        };
        // FalloffFactor sharpens or softens the curve; a zero or absurd value leaves it alone.
        return factor is > 0f and < 8f ? MathF.Pow(curved, factor) : curved;
    }

    public void Dispose() => framework.Update -= OnFramework;
}
