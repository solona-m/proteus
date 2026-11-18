using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The containment rules against every mod actually installed: real records and real manifests must lose nothing. Read
/// only — every reader here writes nothing.
/// </summary>
[Trait("Speed", "Slow")]
public class ModPathLocalTests
{
    // [AC-mod-path-containment-2][AC-mod-path-containment-3][EDGE-mod-path-containment-8]
    [LocalDataFact(LocalData.Mods)]
    public void InstalledModsDropNothingAndRejectNoManifestFile()
    {
        var modsRoot = LocalData.Path(LocalData.Mods);
        var rejected = new List<string>();
        foreach (var modRoot in Directory.EnumerateDirectories(modsRoot))
        {
            HatCompatService.ReadRecord(modRoot);
            MatcapRecord.Read(modRoot);
            MeshToggleService.ReadRecord(modRoot);
            MeshVolumeService.ReadRecord(modRoot);
            ShaderUpgradeWriter.ReadRecord(modRoot);
            HairPortRecord.Read(modRoot);
            foreach (var r in PenumbraModMeta.ReadAllRedirects(modRoot))
                if (!ModPath.IsSafeRel(r.File.Replace('\\', '/')))
                    rejected.Add(Path.GetFileName(modRoot) + ": manifest");
        }

        // The mod folder and the feature only: a path could carry a user name.
        Assert.Empty(ModPath.DropsUnder(modsRoot).Keys.Select(k => k.Split('|')[0] + " in " + Path.GetFileName(k.Split('|')[1])));
        Assert.Empty(rejected.Distinct());
    }
}
