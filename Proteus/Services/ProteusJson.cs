using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proteus.Services;

/// <summary>
/// Shared serializer settings for every JSON file Proteus writes to disk.
/// </summary>
/// <remarks>
/// The point of this type is <see cref="Encoder"/>. System.Text.Json's default encoder escapes every
/// non-ASCII character, so a mod named 彩绘比基尼 with an option named 正常 lands on disk as
/// <c>"彩绘比基尼"</c> / <c>"正常"</c> — legal JSON that parses back
/// identically, but unreadable to the author it belongs to. Penumbra, which serializes with Newtonsoft,
/// writes the real characters into its own meta.json; since Proteus rewrites those same files, matching
/// its escaping is what stops a group rewrite from mangling names Penumbra had written cleanly.
/// </remarks>
public static class ProteusJson
{
    /// <summary>
    /// Writes non-ASCII text as itself. Do NOT "fix" this back to the default: "Unsafe" here means only
    /// that <c>&lt; &gt; &amp; '</c> are left unescaped, which matters when JSON is pasted into an HTML
    /// or script document. Ours goes into mod folders and is read by Penumbra and by us — never into a
    /// page — and everything JSON actually requires escaped (quote, backslash, control characters) still
    /// is.
    /// </summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// Options for <c>Proteus/metadata.json</c>. Nulls are skipped because the gear-layer fields are all
    /// optional, and writing them out as null would bloat every mod's metadata.json the first time it is
    /// saved. Shared and static: the editor saves from the ImGui draw path, so allocating an identical
    /// options object per save was pure garbage on a frame thread.
    /// </summary>
    public static readonly JsonSerializerOptions MetadataWrite = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder                = Encoder,
    };

    /// <summary>
    /// The read counterpart to <see cref="MetadataWrite"/>. Case-insensitive because metadata.json is
    /// hand-edited often enough that a lowercased key shouldn't silently drop a field. Shared and static
    /// for the same reason as the write side, and more so: discovery parses one of these per mod, and a
    /// fresh options object is a COLD one — System.Text.Json rebuilds the whole converter and property
    /// cache for <c>ProteusMetadata</c> behind each one instead of reusing it.
    /// </summary>
    public static readonly JsonSerializerOptions MetadataRead = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// For the hand-rolled <see cref="Utf8JsonWriter"/> paths. A writer's own encoder governs everything
    /// written through it — including <c>JsonElement.WriteTo</c> and
    /// <c>JsonSerializer.Serialize(writer, …)</c>, whose options encoder is ignored once a writer is
    /// supplied — so this is the only place the setting can be applied there.
    /// </summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder  = Encoder,
    };
}
