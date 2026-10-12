using System;
using System.Collections.Generic;
using Proteus.Localization;

namespace Proteus.Gui;

/// <summary>
/// One mod-wide geometry tick in the Colors window's Geometry section: its ImGui id, the recomposite reason, its
/// label and tooltip, and how it reads and writes the sidecar.
/// </summary>
internal sealed record GeometryToggle(string Id, string Reason, Func<ColorsStrings, string> Label,
                                      Func<ColorsStrings, string> Tip, Func<ProteusMetadata, bool> IsOn,
                                      Action<ProteusMetadata, bool> Set);

/// <summary>
/// Every geometry tick, in the order they are drawn: the one list, so a new pass cannot be drawn but forgotten
/// elsewhere. Ticking writes "on, full", clearing any authored strength (which the compositor still honours); an
/// untick clears to null rather than false, so "absent = off" holds.
/// </summary>
internal static class GeometryToggles
{
    public static readonly IReadOnlyList<GeometryToggle> All =
    [
        new("bustbridge", "bust-bridge", c => c.BustBridge, c => c.BustBridgeTip, md => md.BustBridge == true,
            (md, on) => { md.BustBridge = on ? true : null; md.BustBridgeStrength = null; }),
        new("smoothnipples", "smooth-nipples", c => c.SmoothNipples, c => c.SmoothNipplesTip,
            md => md.SmoothNipples == true,
            (md, on) => { md.SmoothNipples = on ? true : null; md.SmoothNipplesStrength = null; }),
        new("cleftbridge", "cleft-bridge", c => c.CleftBridge, c => c.CleftBridgeTip, md => md.CleftBridge == true,
            (md, on) => { md.CleftBridge = on ? true : null; md.CleftBridgeStrength = null; }),
        new("smoothfold", "smooth-fold", c => c.SmoothFold, c => c.SmoothFoldTip, md => md.SmoothFold == true,
            (md, on) => { md.SmoothFold = on ? true : null; md.SmoothFoldStrength = null; }),
    ];
}
