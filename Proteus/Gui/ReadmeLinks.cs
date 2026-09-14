using System;
using System.Collections.Generic;

namespace Proteus.Gui;

/// <summary>The README sections the header band's capability row links to.</summary>
internal enum ReadmeSection
{
    ColorEditor,
    Import,
    Studio,
    Bindings,
}

/// <summary>
/// Links into the README, in the reader's own language, at a named section.
/// <para/>
/// The dl.solona.info mirror, not GitHub: GitHub rate-limits anonymous readers hard, and the mirror is the
/// host the plugin already sends people to. It serves each translation at a PINNED path (/ja/README.md), so
/// the link shows that language whatever the browser's Accept-Language says.
/// <para/>
/// The mirror gives headings the ids GitHub would (githubSlug in worker/src/render.js), which derive from the
/// heading's TEXT — why every translation needs its own anchor below: the same section is "#studio" in
/// English and "#スタジオ" in Japanese. ReadmeLinksTests reads every README and fails if an anchor names a
/// heading that no longer exists, so renaming a heading without updating this table cannot ship.
/// <para/>
/// The mirror proxies the READMEs from main, so a section that exists only on a branch opens at the top of
/// the page until that branch is merged.
/// </summary>
internal static class ReadmeLinks
{
    private const string Mirror = "https://dl.solona.info/";

    /// <summary>
    /// Per language, the GitHub heading anchor of each section, in <see cref="ReadmeSection"/> order.
    /// Exposed for the test; nothing else should read it.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> Anchors = new Dictionary<string, string[]>
    {
        ["en"] = ["color-editor",        "import",   "studio",   "bindings"],
        ["ja"] = ["カラーエディター",        "取り込み",   "スタジオ",   "紐付け"],
        ["de"] = ["farbeditor",          "import",   "studio",   "bindungen"],
        ["fr"] = ["éditeur-de-couleurs", "importer", "studio",   "liaisons"],
        ["zh"] = ["颜色编辑器",             "导入",      "工作室",    "绑定"],
        ["ko"] = ["색상-편집기",            "가져오기",   "스튜디오",   "연결"],
        ["es"] = ["editor-de-colores",   "importar", "estudio",  "vínculos"],
        ["ru"] = ["редактор-цветов",     "импорт",   "студия",   "привязки"],
    };

    /// <summary>Where each translation lives in the repo: English is the root README, the rest sit in docs/.</summary>
    internal static string DocPathFor(string lang) => lang == "en" ? "README.md" : $"docs/README.{lang}.md";

    /// <summary>
    /// Where the mirror serves each translation. English too gets its pinned /en/ path rather than the front
    /// door, which negotiates on Accept-Language. Matches mirrorPathFor in worker/src/render.js.
    /// </summary>
    internal static string MirrorPathFor(string lang) => $"{lang}/README.md";

    /// <summary>
    /// The URL of <paramref name="section"/> in the README for <paramref name="lang"/>. A language Proteus does
    /// not ship (Dalamud's UI can be Italian or Norwegian) gets the English README, the same fallback the UI
    /// strings take.
    /// </summary>
    public static string Url(string? lang, ReadmeSection section)
    {
        if (lang == null || !Anchors.TryGetValue(lang, out var anchors))
        {
            lang = "en";
            anchors = Anchors["en"];
        }

        // Escaped because most anchors are not ASCII; the browser decodes the fragment before matching an id.
        return Mirror + MirrorPathFor(lang) + "#" + Uri.EscapeDataString(anchors[(int)section]);
    }
}
