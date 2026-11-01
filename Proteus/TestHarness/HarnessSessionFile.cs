using System;
using System.IO;
using System.Text.Json;

namespace Proteus.TestHarness;

/// <summary>What QA reads to reach the harness: <c>&lt;ConfigDirectory&gt;\test-harness.json</c>.</summary>
internal sealed record HarnessSession(int Port, string Token, int Pid, int Build, string BuildStamp, string Session,
                                      DateTime StartedUtc);

/// <summary>The session file and the autostart marker beside it.</summary>
internal static class HarnessSessionFile
{
    internal const string FileName = "test-harness.json";
    internal const string MarkerName = "test-harness.autostart";

    internal static string PathIn(string configDir) => Path.Combine(configDir, FileName);
    internal static string MarkerIn(string configDir) => Path.Combine(configDir, MarkerName);

    /// <summary>Written atomically (temp then move), so QA never reads half a file.</summary>
    internal static void Write(string configDir, HarnessSession session)
    {
        Directory.CreateDirectory(configDir);
        string path = PathIn(configDir);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, HarnessJson.Bytes(session));
        File.Move(temp, path, overwrite: true);
    }

    internal static HarnessSession? Read(string configDir)
    {
        try { return JsonSerializer.Deserialize<HarnessSession>(File.ReadAllBytes(PathIn(configDir)), HarnessJson.Options); }
        catch { return null; }
    }

    /// <summary>Deletes the file only if it is this session's: a stopping instance never deletes a newer one's.</summary>
    internal static void DeleteIfOwn(string configDir, string session)
    {
        if (Read(configDir) is { } on && on.Session == session)
            try { File.Delete(PathIn(configDir)); } catch { /* best effort */ }
    }
}
