using System;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// What the game's own model structures expose, listed from the assembly rather than guessed at.
/// The renderer frees a model's file bytes after upload, so comparing the live model against the .mdl
/// on disk has to go through the PARSED structure — this says which fields are available to do that.
/// </summary>
public class ClientStructsProbe(ITestOutputHelper o)
{
    [Fact]
    public void ListModelResourceHandleMembers()
    {
        foreach (var typeName in new[]
                 {
                     "FFXIVClientStructs.FFXIV.Client.Graphics.Render.ModelResourceHandle",
                     "FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Model",
                 })
        {
            // Force the assembly to load: nothing in the test project has touched it yet.
            _ = typeof(Proteus.Interop.LiveModelDump);
            var t = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetType(typeName, false); } catch { return null; } })
                .FirstOrDefault(x => x != null)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
                    .FirstOrDefault(x => x.Name == typeName.Split('.').Last());
            if (t == null) { o.WriteLine($"{typeName}: not found"); continue; }

            o.WriteLine($"=== {t.FullName}");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)
                               .OrderBy(f => f.Name))
                o.WriteLine($"   field  {f.FieldType.Name,-40} {f.Name}");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                               .OrderBy(p => p.Name))
                o.WriteLine($"   prop   {p.PropertyType.Name,-40} {p.Name}");
        }
    }
}
