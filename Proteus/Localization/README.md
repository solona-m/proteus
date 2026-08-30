# Proteus localization

Proteus ships English plus seven translations. The language follows the player's **Dalamud**
UI language and switches live — there is no separate setting in Proteus.

| Code | Language | | Code | Language |
|------|----------|-|------|----------|
| `en` | English (source) | | `ko` | Korean |
| `ja` | Japanese | | `es` | Spanish |
| `de` | German | | `ru` | Russian |
| `fr` | French | | `zh` | Chinese (Simplified) |

> **The non-English files are machine-translated and have not been reviewed by native
> speakers.** They are correct in structure and safe to ship — the tests guarantee that — but the
> wording deserves a pass from someone who plays the game in that language. Corrections are
> welcome and are the easiest possible contribution: edit one `.json` file, nothing else.

## How it works

`LocSetup` constructs `Dalamud.Localization` (a wrapper over CheapLoc, both shipped inside
Dalamud — no NuGet package) against the embedded resource prefix `Proteus.Localization.`, then
follows `IDalamudPluginInterface.LanguageChanged`.

Every string is looked up **once per language**, not once per frame, and cached in the holder
classes in `Strings.cs`. `Loc.Localize` walks the stack and allocates an `AssemblyName` on every
call, so calling it from a draw loop would be thousands of stack walks a second. If you add a
string that is drawn every frame, put it in a holder; a string reached only by a click or a
background task may call `Loc.Localize` inline.

All eight languages fall back to the English text compiled into each call site, so a missing key
or a malformed file degrades to English rather than to blanks.

## Translating

1. Read `en.json`. Every entry has a `description` saying where the string appears and what its
   `{0}` placeholders hold, and flagging anything that must not be translated. Where space is
   tight — table headers, the header-band badges — it says so; those are clipped, not wrapped.
2. Copy the value of `message` into the matching key in your language's file. **Never add,
   remove or rename a key** — the tests fail on both missing and orphaned keys.

Rules the tests enforce:

- **`{0}`, `{1}` … may be reordered but never added, removed or renumbered.** Reordering is
  expected — German puts the verb last, Japanese is subject-object-verb.
- **`\n` is a deliberate line break.** Keep them; move them where your language needs them.
- **`message` may not be empty.** If a term genuinely should not be translated, repeat the
  English.
- **A key ending `.Fmt` has arguments; one that doesn't, doesn't.**

Never translate: **Proteus**, **Penumbra**, **Glamourer**, **Onion**, **Skindent** /
**Skindenting** (a coined Proteus feature name), **Discord**, file extensions, paths like
`Proteus/Effects/`, and technical tokens like `BC7`, `_o`, `bibo`, `gen3`.

Column headers in the Mods and Bindings tables are drawn in **fixed-width columns** — their
`description` gives the budget. Abbreviate rather than overflow.

## The project README is translated too

The same eight languages, at [`docs/README.<lang>.md`](../../docs) (English is the repo root's
`README.md`). They are not part of the plugin build — the asset mirror serves them, picking one from
the reader's `Accept-Language` for `/` and pinning it for `/ja/README.md` — but they are the same job
and the same terminology, so translate them from the strings above rather than inventing new wording
for a tab or a setting. `worker/README.md` describes the serving side.

`ReadmeTranslationTests` holds them to the English structure: same headings in the same order, same
tables with the same number of rows, the language switcher listing all eight, and every command, URL
and file extension left verbatim. Adding a section to the English README without the translations
following fails the build, which is the point.

## Adding a string in code

```csharp
// In Strings.cs, inside the holder for the screen it belongs to.
public readonly string Thing = Loc.Localize("Area.Thing.Label", "Thing");

// Carries ImGui state (checkbox, header, tab, table header)? Pin a stable ASCII id, so the
// widget stays the same widget when the language changes and two languages cannot collide:
public readonly string Thing = Loc.Localize("Area.Thing.Label", "Thing") + "###areaThing";

// Takes arguments? Name the key .Fmt and use string.Format at the call site.
public readonly string CountFmt = Loc.Localize("Area.Count.Fmt", "{0} of {1}");
```

Both arguments must be **compile-time literals**. `"a" + "b"` is folded by the compiler and is
fine; an interpolated `$"..."` is **not** — it is invisible to the exporter and can never be
translated. `LocalizationTests` fails the build on this.

Then run `dotnet test`. `CodeKeysMatchEnglishJson` prints the new entries as ready-to-paste JSON;
paste them into `en.json` and translate into the other seven.

That test is also why there is no in-game export command: CheapLoc's own `ExportLocalizable`
reads `Assembly.Location`, and Dalamud loads plugins from a stream, so at runtime that path is
empty and the call throws. The test does the same IL walk from disk, where the path is real, and
runs in CI on every push.
