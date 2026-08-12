using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// What the game's own model structures expose, listed from the assembly rather than guessed at.
/// <para/>
/// The question this exists to answer: is there any per-character, already-race-deformed copy of a
/// model's vertices in memory? If there is, the second-skin shell can be cut from it and lands in the
/// character's own space for free. If there isn't, the deform has to be applied to the geometry by hand
/// (chara/xls/boneDeformer/human.pbd) before the shell is written.
/// </summary>
public class ClientStructsProbe(ITestOutputHelper o)
{
    private static IEnumerable<Type> AllClientStructTypes()
    {
        // Straight off a known type — scanning AppDomain assemblies missed it, since the reference is
        // only resolved when a type is actually touched.
        var asm = typeof(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase).Assembly;
        try { return asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    [Fact]
    public void ListModelMembers()
    {
        var types = AllClientStructTypes().ToList();
        o.WriteLine($"ClientStructs types loaded: {types.Count}");

        foreach (var typeName in new[]
                 {
                     "FFXIVClientStructs.FFXIV.Client.Graphics.Render.ModelResourceHandle",
                     "FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Model",
                     "FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase",
                 })
        {
            var t = types.FirstOrDefault(x => x.FullName == typeName)
                 ?? types.FirstOrDefault(x => x.Name == typeName.Split('.').Last());
            if (t == null) { o.WriteLine($"{typeName}: not found"); continue; }

            o.WriteLine($"=== {t.FullName}");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name))
                o.WriteLine($"   field  {f.FieldType.Name,-44} {f.Name}");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
                o.WriteLine($"   prop   {p.PropertyType.Name,-44} {p.Name}");
        }
    }

    /// <summary>
    /// Anything named for deformation or vertex storage, anywhere in the assembly — so a per-instance
    /// deformed buffer can't hide behind a name this probe didn't think to look up.
    /// </summary>
    [Fact]
    public void ListDeformAndVertexMembers()
    {
        foreach (var t in AllClientStructTypes()
                     .Where(t => t.Namespace?.Contains("Graphics", StringComparison.Ordinal) == true
                              || t.Namespace?.Contains("Character", StringComparison.Ordinal) == true))
        {
            var hits = t.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.Name.Contains("Deform", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("Pbd", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("VertexBuffer", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("VertexData", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 0) continue;
            o.WriteLine($"=== {t.FullName}");
            foreach (var f in hits)
                o.WriteLine($"   field  {f.FieldType.Name,-44} {f.Name}");
        }
    }
}
