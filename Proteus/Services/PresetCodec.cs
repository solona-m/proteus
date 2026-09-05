using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Proteus.Services;

/// <summary>The outcome of decoding a shared preset. A bad paste is an everyday event — someone copies
/// the wrong thing — so it is a value the UI can print, never an exception.</summary>
/// <param name="Preset">The decoded preset, or null when <paramref name="Error"/> says why not.</param>
public record PresetDecodeResult(ModPreset? Preset, string? Error)
{
    public static PresetDecodeResult Fail(string error) => new(null, error);
    public static PresetDecodeResult Ok(ModPreset preset) => new(preset, null);
}

/// <summary>
/// Reads and writes presets outside the plugin: as a <c>.ptp</c> file to keep or post, and as a
/// clipboard share code to paste into a chat window.
/// <para/>
/// The share code is a version byte, then deflate, then base64 — the same recipe Glamourer uses for
/// designs, reimplemented here in a few lines because that code lives in the Glamourer plugin proper
/// and Proteus references only its API assembly. The version byte is what makes a future format change
/// a clear message instead of a confusing parse failure.
/// </summary>
public static class PresetCodec
{
    /// <summary>Bumped only when a decoder written today could get an OLDER code wrong. Adding an
    /// optional field does not qualify; changing what an existing one means does.</summary>
    public const byte Version = 1;

    public const string FileExtension = ".ptp";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = ProteusJson.Encoder,
    };

    // ── File ────────────────────────────────────────────────────────────────────

    /// <summary>A filename that will not need renaming and will not surprise anyone: the preset's name,
    /// with anything the filesystem rejects replaced.</summary>
    public static string SuggestedFileName(ModPreset preset)
    {
        var name = string.IsNullOrWhiteSpace(preset.Name) ? "preset" : preset.Name;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name + FileExtension;
    }

    public static void ToFile(ModPreset preset, string path)
        => PenumbraModMeta.AtomicWrite(path, JsonSerializer.Serialize(Portable(preset), JsonOpts));

    public static PresetDecodeResult FromFile(string path)
    {
        try
        {
            return Validate(JsonSerializer.Deserialize<ModPreset>(File.ReadAllText(path), JsonOpts));
        }
        catch (Exception ex)
        {
            return PresetDecodeResult.Fail(ex.Message);
        }
    }

    // ── Share code ──────────────────────────────────────────────────────────────

    public static string ToShareCode(ModPreset preset)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(Portable(preset), JsonOpts);

        using var output = new MemoryStream();
        output.WriteByte(Version);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(json, 0, json.Length);

        return Convert.ToBase64String(output.ToArray());
    }

    public static PresetDecodeResult FromShareCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return PresetDecodeResult.Fail("Nothing to paste.");

        byte[] raw;
        // Chat clients wrap long strings, and a pasted code routinely arrives with newlines or stray
        // spaces in it. Strip whitespace rather than making the wearer clean it up by hand.
        try { raw = Convert.FromBase64String(Strip(code)); }
        catch (FormatException) { return PresetDecodeResult.Fail("That doesn't look like a Proteus preset code."); }

        if (raw.Length < 2) return PresetDecodeResult.Fail("That doesn't look like a Proteus preset code.");
        if (raw[0] != Version)
            return PresetDecodeResult.Fail(
                $"This code is version {raw[0]}; this version of Proteus understands {Version}. Update Proteus.");

        try
        {
            using var input   = new MemoryStream(raw, 1, raw.Length - 1);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var json    = new MemoryStream();
            deflate.CopyTo(json);
            return Validate(JsonSerializer.Deserialize<ModPreset>(json.ToArray(), JsonOpts));
        }
        catch (Exception)
        {
            return PresetDecodeResult.Fail("That doesn't look like a Proteus preset code.");
        }
    }

    private static string Strip(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var c in code)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    // ── Shared ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The copy that leaves this machine. The id is dropped because it is meaningless anywhere else —
    /// the receiving store mints a fresh one — and carrying it would let a shared preset collide with
    /// a local one that happens to share it.
    /// </summary>
    private static ModPreset Portable(ModPreset preset)
    {
        var copy = preset.Clone();
        copy.Id     = Guid.Empty;
        copy.Source = PresetSource.User;
        return copy;
    }

    /// <summary>
    /// Well-formed JSON is not yet a preset: an empty object deserializes happily into a nameless one
    /// with nothing in it, and applying that would silently do nothing at all.
    /// </summary>
    private static PresetDecodeResult Validate(ModPreset? preset)
    {
        if (preset == null) return PresetDecodeResult.Fail("That file doesn't contain a Proteus preset.");
        if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = "Imported preset";

        var hasSomething = preset.Options.Count > 0
                        || preset.StackOrder.Count > 0
                        || preset.Colors.Top != null || preset.Colors.Mask != null || preset.Colors.Options != null
                        || preset.Gear.Top != null || preset.Gear.Mask != null
                        || preset.Gear.Content != null || preset.Gear.Options != null;
        if (!hasSomething) return PresetDecodeResult.Fail("That preset is empty.");

        preset.Id     = Guid.NewGuid();
        preset.Source = PresetSource.User;
        return PresetDecodeResult.Ok(preset);
    }
}
