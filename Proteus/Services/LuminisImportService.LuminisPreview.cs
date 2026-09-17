using System;
using System.Collections.Generic;
using System.Linq;
using CheapLoc;
using Penumbra.Api.Enums;

namespace Proteus.Services;

public sealed partial class LuminisImportService
{
    private sealed class LuminisPreview
    {
        private readonly string ttmpPath;
        private readonly TexToolsPackage.Contents pack;
        private readonly string? wearerBody;
        private readonly Func<byte[], string, (int Width, int Height, float Glow)?> measure;
        private List<string> warnings = null!;
        private string? wearerSuffix;
        private string? wearerType;
        private Dictionary<long, List<TexToolsPackage.PackFile>> byOffset = null!;
        private List<long> order = null!;
        private Dictionary<long, long> slices = null!;
        private Dictionary<long, TexToolsPackage.Payload> payloads = null!;
        private HashSet<string> stems = null!;
        private List<TexturePlan> plans = null!;

        public LuminisPreview(string ttmpPath, TexToolsPackage.Contents pack, string? wearerBody, Func<byte[], string, (int Width, int Height, float Glow)?> measure)
        {
            this.ttmpPath = ttmpPath;
            this.pack = pack;
            this.wearerBody = wearerBody;
            this.measure = measure;
        }

        public ImportPreview Run()
        {
            IndexPayloads();
            SelectPayloads();
            PlanTextures();
            return Summarise();
        }

        private void IndexPayloads()
        {
            warnings = new List<string>();
            wearerSuffix = SuffixOf(wearerBody);
            wearerType = wearerBody == null ? null : UVRemapService.InferBodyType(wearerBody);

            // One entry per distinct payload, carrying every path that aliases it. Keyed on the offset because
            // that IS the identity of a file in this format — an AL pack points six paths at byte 0, and
            // importing six copies of one picture would write six shells over each other.
            byOffset = new Dictionary<long, List<TexToolsPackage.PackFile>>();
            order = new List<long>();
            foreach (var f in pack.Files)
            {
                if (!byOffset.TryGetValue(f.Offset, out var list))
                {
                    byOffset[f.Offset] = list = [];
                    order.Add(f.Offset);
                }
                list.Add(f);
            }

            slices = byOffset.ToDictionary(kv => kv.Key, kv => kv.Value.Max(f => f.Size));
        }

        private void SelectPayloads()
        {
            // Decode ONLY the payloads that survive the path check, and decide that from the manifest alone.
            //
            // Everything below needs pixels, but nothing about "is this even an Atramentum Luminis path" does,
            // and the difference is unbounded: the Import tab accepts any .ttmp2 a user picks, so an ordinary
            // TexTools mod arriving here would otherwise be inflated in full — every model, material and 4K
            // sheet in it, all resident at once, on the frame that picked the file — purely to be told none of
            // its paths are AL-shaped. A pack that is not one now costs a manifest read.
            var wanted = slices
                .Where(kv => IsCandidate(byOffset[kv.Key][0].GamePath))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            payloads = TexToolsPackage.ReadPayloads(ttmpPath, wanted);

            // Stems name the written files AND the Penumbra options, so two payloads may not share one. A pack
            // shipping chara/bibo/midlander_d.tex beside chara/gen3/midlander_d.tex would otherwise write both
            // over the same overlays/midlander_glow.png and offer two identically-named options.
            stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            plans = new List<TexturePlan>();
        }

        private void PlanTextures()
        {
            foreach (var offset in order)
            {
                var paths = byOffset[offset].Select(f => f.GamePath).ToList();
                var stem = Unique(StemOf(paths[0]), TokenOf(paths[0]));
                var size = slices[offset];

                TexturePlan Skip(string why)
                    => new(offset, size, paths, stem, null, null, null, 0, 0, 0f, false, why);

                // ── is it AL-shaped? ──
                var first = paths[0];
                if (!first.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                {
                    plans.Add(Skip(Loc.Localize("Import.Luminis.Skip.NotATexture",
                        "it isn't a texture — Atramentum Luminis only carries those.")));
                    continue;
                }

                var token = TokenOf(first);
                if (token == null)
                {
                    plans.Add(Skip(string.Format(Loc.Localize("Import.Luminis.Skip.GamePath.Fmt",
                        "\"{0}\" is a real game path, so this is an ordinary TexTools mod rather than an "
                      + "Atramentum Luminis pack. Install it in Penumbra instead."), first)));
                    continue;
                }

                // ── which body, and in whose UV space? ──
                var (bodyType, suffix, fromWearer) = ResolveBody(token, wearerType, wearerSuffix);
                if (suffix == null)
                {
                    plans.Add(Skip(string.Format(Loc.Localize("Import.Luminis.Skip.UnknownBody.Fmt",
                        "Proteus doesn't know the body \"{0}\", and can't ask your character which one they "
                      + "are wearing until they're drawn. Load in and reopen this pack."), token)));
                    continue;
                }

                // ── does it actually carry a glow mask? ──
                if (!payloads.TryGetValue(offset, out var payload))
                {
                    plans.Add(Skip(Loc.Localize("Import.Luminis.Skip.Missing",
                        "the modpack's data blob doesn't contain it.")));
                    continue;
                }
                if (payload.Error != null)
                {
                    plans.Add(Skip(string.Format(Loc.Localize("Import.Luminis.Skip.Unreadable.Fmt",
                        "it couldn't be read: {0}"), payload.Error)));
                    continue;
                }
                if (!payload.IsTexture)
                {
                    plans.Add(Skip(string.Format(Loc.Localize("Import.Luminis.Skip.NotTextureData.Fmt",
                        "it is a model or material (SqPack type {0}), not a texture."), payload.Type)));
                    continue;
                }

                var measured = measure(payload.Tex!, first);
                if (measured is not { } m)
                {
                    plans.Add(Skip(Loc.Localize("Import.Luminis.Skip.Undecodable",
                        "its pixels couldn't be decoded.")));
                    continue;
                }

                if (m.Glow < MinGlowFraction)
                {
                    plans.Add(Skip(Loc.Localize("Import.Luminis.Skip.NoGlow",
                        "its alpha channel is flat, so it carries no glow. Atramentum Luminis puts the glow "
                      + "there, and a texture without one is an ordinary skin rather than a glowing tattoo.")));
                    continue;
                }

                if (m.Glow > SuspiciousGlowFraction)
                    warnings.Add(string.Format(Loc.Localize("Import.Luminis.Warn.MostlyGlow.Fmt",
                        "\"{0}\" glows across {1:P0} of the body. That is legal, but it usually means the "
                      + "texture has no real alpha channel — check the result before wearing it out."),
                        first, m.Glow));

                plans.Add(new TexturePlan(offset, size, paths, stem, token, bodyType, suffix,
                                          m.Width, m.Height, m.Glow, fromWearer, null));
            }

            if (plans.Count == 0 || plans.All(p => !p.Import))
                warnings.Add(Loc.Localize("Import.Luminis.Warn.NothingImportable",
                    "Nothing in this pack can be imported — see the reasons above."));
        }

        private ImportPreview Summarise()
        {
            // Said once, not per texture: the fallback is a guess about which body the art was painted for,
            // and a guess repeated eight times reads as eight problems.
            if (plans.Any(p => p.Import && p.FromWearer))
                warnings.Add(string.Format(Loc.Localize("Import.Luminis.Warn.FromWearer.Fmt",
                    "Proteus doesn't know this pack's body layout, so it will paint the art onto the body "
                  + "you're wearing ({0}) exactly as it is, with no resizing. If the pack was painted for a "
                  + "different body it will look wrong — change the body target below if you know better."),
                    wearerSuffix ?? ""));

            // Deliberately NOT warnings, and the two that are here say why by contrast. Proteus carries no race
            // or sex filter and never has — that is a standing property of every overlay it composites, not
            // something this pack did — and colouring it amber on every single import is the cried-wolf problem
            // ContentImportService.FaultyUnits exists to avoid. The panel states it in plain text instead. The
            // aliasing is the same: several paths over one picture is the NORMAL shape of these packs, and the
            // texture table already says "{n} paths" on the row it applies to.
            return new ImportPreview(
                ttmpPath,
                pack.Name.Trim(),
                pack.Author.Trim(),
                string.IsNullOrWhiteSpace(pack.Description) ? null : pack.Description!.Trim(),
                string.IsNullOrWhiteSpace(pack.Website) ? null : pack.Website!.Trim(),
                string.IsNullOrWhiteSpace(pack.Version) ? null : pack.Version!.Trim(),
                plans,
                warnings,
                wearerSuffix);
        }

        // Worth decoding: a texture on an Atramentum Luminis virtual path. The two cheap checks PlanTextures makes
        // before it touches a pixel, hoisted so the decode can be limited to what passes them.
        private static bool IsCandidate(string gamePath)
            => gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) && TokenOf(gamePath) != null;

        // Qualified by the body token first, since that is what actually differs between two payloads
        // spelled the same, and only then by a number.
        private string Unique(string stem, string? token)
        {
            if (stems.Add(stem)) return stem;
            if (token != null && stems.Add(stem + "_" + token)) return stem + "_" + token;
            for (int i = 2; ; i++)
                if (stems.Add(stem + "_" + i))
                    return stem + "_" + i;
        }
    }
}
