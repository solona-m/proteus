using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Proteus's own IPC call gates and enum copies (plan D24/D26) checked mechanically against the IPC API assemblies,
/// following <c>docs/ipc-contract.md</c> §5. The API DLLs are only read as metadata here; no API code is decompiled or
/// executed. A failure names only the contract row and the fact that differs.
/// </summary>
public class IpcContractTests
{
    private const string Plan10 = "AC-contrib-clean-room-10";

    private static readonly Lazy<string?> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    });

    private static string RepoFile(params string[] parts)
    {
        Assert.True(Root.Value != null, "could not find proteus.slnx");
        return Path.Combine([Root.Value!, .. parts]);
    }

    // ── The contract's rows (§1, §2, §3), parsed from the document ──────────────────────────────

    public sealed record Row(string Id, string Owner, string Label, string Kind);

    private static string Section(string doc, string heading)
    {
        int at = doc.IndexOf("\n" + heading, StringComparison.Ordinal);
        Assert.True(at >= 0, $"contract has no {heading}");
        int next = doc.IndexOf("\n## ", at + heading.Length, StringComparison.Ordinal);
        return next < 0 ? doc[at..] : doc[at..next];
    }

    private static List<Row> ContractRows(params string[] headings)
    {
        var doc = File.ReadAllText(RepoFile("docs", "ipc-contract.md")).Replace("\r\n", "\n");
        var rows = new List<Row>();
        foreach (var h in headings)
            foreach (Match m in Regex.Matches(Section(doc, h), @"^\|\s*([PGH]\d+)\s+(Penumbra|Glamourer)\s*\|\s*`([^`]+)`\s*\|\s*(Func|Action|Event)\s*\|", RegexOptions.Multiline))
                rows.Add(new Row(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value));
        return rows;
    }

    private static HashSet<string> ContractEnums()
    {
        var doc = File.ReadAllText(RepoFile("docs", "ipc-contract.md")).Replace("\r\n", "\n");
        return Regex.Matches(Section(doc, "## 3."), @"^\|\s*((?:Penumbra|Glamourer)\.Api\.Enums\.\w+)\s*\|\s*\w+\s*\|\s*\d+\s*\|", RegexOptions.Multiline)
                    .Select(m => m.Groups[1].Value).ToHashSet();
    }

    // ── Proteus's side: IpcContract.Entries (plan Contract) through reflection ──────────────────

    public sealed record Entry(string Owner, string Label, string Kind, Type[] Arguments, Type? Returns);

    private static List<Entry> ProteusEntries()
    {
        var contract = typeof(Plugin).Assembly.GetType("Proteus.Interop.Ipc.IpcContract");
        Assert.True(contract != null, "Proteus.Interop.Ipc.IpcContract is missing");
        var entries = (System.Collections.IEnumerable)contract!.GetProperty("Entries", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var list = new List<Entry>();
        foreach (var e in entries)
        {
            var t = e.GetType();
            list.Add(new Entry((string)t.GetProperty("Owner")!.GetValue(e)!, (string)t.GetProperty("Label")!.GetValue(e)!,
                               t.GetProperty("Kind")!.GetValue(e)!.ToString()!, (Type[])t.GetProperty("Arguments")!.GetValue(e)!,
                               (Type?)t.GetProperty("Returns")!.GetValue(e)));
        }
        return list;
    }

    // ── The API side, read with Cecil (§5 M1–M10) ───────────────────────────────────────────────

    private static readonly Dictionary<string, ModuleDefinition> Modules = new();

    private static ModuleDefinition Module(string owner)
    {
        lock (Modules)
        {
            if (Modules.TryGetValue(owner, out var m)) return m;
            var path = Path.Combine(AppContext.BaseDirectory, owner + ".Api.dll");
            Assert.True(File.Exists(path), $"{owner}.Api.dll is not in the test output");
            return Modules[owner] = ModuleDefinition.ReadModule(path);
        }
    }

    /// <summary>The API type whose Label constant is the label, in the exact IpcSubscribers namespace (M1, M2).</summary>
    private static TypeDefinition? ApiType(string owner, string label) =>
        Module(owner).Types.FirstOrDefault(t => t.Namespace == owner + ".Api.IpcSubscribers"
            && t.Fields.Any(f => f.Name == "Label" && f.HasConstant && f.IsStatic && (f.Constant as string) == label));

    /// <summary>The API's (kind, gate generic arguments without the trailing return/object) per M4–M7.</summary>
    private static (string Kind, List<string> Args, string? Returns) ApiShape(TypeDefinition t)
    {
        var subscriber = t.Methods.FirstOrDefault(m => m.Name == "Subscriber" && m.IsStatic && m.IsPublic);
        if (t.IsAbstract && t.IsSealed && subscriber != null)
        {
            var args = subscriber.ReturnType is GenericInstanceType g ? g.GenericArguments.Select(a => a.FullName).ToList() : [];
            return ("Event", args, null);
        }
        var invokes = t.Methods.Where(m => m.Name == "Invoke" && !m.IsStatic && m.IsPublic).ToList();
        var baseArgs = ((GenericInstanceType)t.BaseType).GenericArguments.Select(a => a.FullName).ToList();
        if (invokes.Count > 0 && invokes.All(m => m.ReturnType.FullName == "System.Void"))
            return ("Action", baseArgs, null);
        return ("Func", baseArgs.Take(baseArgs.Count - 1).ToList(), baseArgs[^1]);
    }

    /// <summary>A System.Type in Cecil's FullName spelling (M8).</summary>
    public static string CecilName(Type t)
    {
        if (t.IsArray) return CecilName(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var name = (def.Namespace is { Length: > 0 } ns ? ns + "." : "") + def.Name;
            return name + "<" + string.Join(",", t.GetGenericArguments().Select(CecilName)) + ">";
        }
        return t.FullName!.Replace('+', '/');
    }

    /// <summary>Compares one Proteus entry with its API type; messages name only the row and the fact (P-5).</summary>
    public static List<string> Compare(string rowId, string owner, Entry e, (string Kind, List<string> Args, string? Returns) api)
    {
        var errors = new List<string>();
        string where = $"{owner} {rowId}";
        if (e.Kind != api.Kind) errors.Add($"{where}: Kind differs");
        var mine = e.Arguments.Select(CecilName).ToList();
        if (!mine.SequenceEqual(api.Args)) errors.Add($"{where}: Arguments differ");
        string? myReturn = e.Returns == null ? null : CecilName(e.Returns);
        if (myReturn != api.Returns) errors.Add($"{where}: Returns differs");
        return errors;
    }

    [Fact]
    [Trait("Plan", Plan10)]
    [Trait("Plan", "EDGE-contrib-clean-room-16")]
    public void Every_call_gate_matches_the_API()   // [AC-contrib-clean-room-10] [EDGE-contrib-clean-room-16]
    {
        var rows = ContractRows("## 1.", "## 2.");
        Assert.NotEmpty(rows);
        var entries = ProteusEntries();
        var errors = new List<string>();

        // Exactly the contract's labels, no more and no fewer.
        var want = rows.Select(r => r.Owner + "|" + r.Label).OrderBy(x => x).ToList();
        var have = entries.Select(e => e.Owner + "|" + e.Label).OrderBy(x => x).ToList();
        foreach (var r in rows.Where(r => !have.Contains(r.Owner + "|" + r.Label))) errors.Add($"{r.Owner} {r.Id}: no IpcContract entry");
        foreach (var e in entries.Where(e => !want.Contains(e.Owner + "|" + e.Label))) errors.Add($"{e.Owner} entry {e.Label}: not in the contract");

        foreach (var r in rows)
        {
            var e = entries.FirstOrDefault(x => x.Owner == r.Owner && x.Label == r.Label);
            if (e == null) continue;
            if (e.Kind != r.Kind) errors.Add($"{r.Owner} {r.Id}: Kind differs from the contract");
            var api = ApiType(r.Owner, r.Label);
            if (api == null) { errors.Add($"{r.Owner} {r.Id}: no API type with this label"); continue; }
            errors.AddRange(Compare(r.Id, r.Owner, e, ApiShape(api)));
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    /// <summary>An enum constant's bit pattern, whatever its underlying type.</summary>
    private static ulong Bits(object v) => v switch
    {
        ulong u => u, long l => unchecked((ulong)l), uint u => u, int i => unchecked((ulong)(long)i),
        ushort u => u, short s => unchecked((ulong)(long)s), byte b => b, sbyte s => unchecked((ulong)(long)s),
        _ => throw new ArgumentException("not an integral constant"),
    };

    [Fact]
    [Trait("Plan", Plan10)]
    [Trait("Plan", "EDGE-contrib-clean-room-15")]
    public void Every_owned_type_matches_the_API()   // [AC-contrib-clean-room-10] [EDGE-contrib-clean-room-15]
    {
        var errors = new List<string>();
        var owned = typeof(Plugin).Assembly.GetTypes()
            .Where(t => t.IsEnum && t.Namespace is "Penumbra.Api.Enums" or "Glamourer.Api.Enums").ToDictionary(t => t.FullName!);
        foreach (var name in ContractEnums().Where(n => !owned.ContainsKey(n)))
            errors.Add($"§3 {name}: no owned type");
        foreach (var (name, t) in owned)
        {
            string owner = name.StartsWith("Penumbra.", StringComparison.Ordinal) ? "Penumbra" : "Glamourer";
            var api = Module(owner).Types.FirstOrDefault(x => x.FullName == name && x.IsEnum);
            if (api == null) { errors.Add($"§3 {name}: no API enum"); continue; }
            var apiUnderlying = api.Fields.First(f => f.Name == "value__").FieldType.FullName;
            if (Enum.GetUnderlyingType(t).FullName != apiUnderlying) errors.Add($"§3 {name}: Underlying differs");
            bool apiFlags = api.CustomAttributes.Any(a => a.AttributeType.FullName == "System.FlagsAttribute");
            if (t.IsDefined(typeof(FlagsAttribute), false) != apiFlags) errors.Add($"§3 {name}: Flags differs");
            var apiMembers = api.Fields.Where(f => f.IsStatic && f.HasConstant).ToDictionary(f => f.Name, f => Bits(f.Constant!));
            var mine = Enum.GetNames(t).ToDictionary(n => n, n => Bits(Convert.ChangeType(Enum.Parse(t, n), Enum.GetUnderlyingType(t))));
            foreach (var m in apiMembers.Keys.Except(mine.Keys)) errors.Add($"§3 {name}.{m}: missing");
            foreach (var m in mine.Keys.Except(apiMembers.Keys)) errors.Add($"§3 {name}.{m}: not in the API");
            foreach (var m in mine.Keys.Intersect(apiMembers.Keys).Where(m => mine[m] != apiMembers[m])) errors.Add($"§3 {name}.{m}: Value differs");
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    [Trait("Plan", Plan10)]
    [Trait("Plan", "EDGE-contrib-clean-room-18")]
    public void Harness_gates_match_the_API()   // [AC-contrib-clean-room-10] [EDGE-contrib-clean-room-18]
    {
        // §6: compiled only into local harness builds (ADR 0002); on a CI build there is nothing to check.
        var rows = ContractRows("## 6.");
        Assert.Equal(2, rows.Count);
        var errors = new List<string>();
        foreach (var r in rows)
        {
            var name = r.Label.Split('.')[1];   // Glamourer.ApplyDesign -> ApplyDesign
            var gate = typeof(Plugin).Assembly.GetType("Proteus.TestHarness.Glamourer" + name);
            if (gate == null) return;
            var d = Proteus.Interop.Ipc.IpcContract.Describe(r.Owner, gate);
            var e = new Entry(d.Owner, d.Label, d.Kind.ToString(), d.Arguments, d.Returns);
            if (e.Label != r.Label) errors.Add($"{r.Owner} {r.Id}: Label differs");
            var api = ApiType(r.Owner, r.Label);
            if (api == null) { errors.Add($"{r.Owner} {r.Id}: no API type with this label"); continue; }
            errors.AddRange(Compare(r.Id, r.Owner, e, ApiShape(api)));
            Assert.DoesNotContain(Proteus.Interop.Ipc.IpcContract.Entries, x => x.Label == r.Label);   // never a shipped gate
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    [Trait("Plan", Plan10)]
    public void ApplyFlagEx_constants_match_the_API()   // [AC-contrib-clean-room-10]
    {
        var api = Module("Glamourer").Types.First(t => t.FullName == "Glamourer.Api.Enums.ApplyFlagEx");
        var mine = typeof(Plugin).Assembly.GetType("Glamourer.Api.Enums.ApplyFlagEx");
        Assert.True(mine != null, "§3 Glamourer.Api.Enums.ApplyFlagEx: no owned type");
        var apiConsts = api.Fields.Where(f => f.IsStatic && f.HasConstant).ToDictionary(f => f.Name, f => Bits(f.Constant!));
        var myConsts = mine!.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral)
                            .ToDictionary(f => f.Name, f => Bits(Convert.ChangeType(f.GetRawConstantValue()!, typeof(ulong))));
        Assert.Equal(apiConsts.OrderBy(k => k.Key), myConsts.OrderBy(k => k.Key));
    }

    [Fact]
    [Trait("Plan", Plan10)]
    public void Shipped_code_reaches_Penumbra_and_Glamourer_only_through_IpcContract_gates()   // [AC-contrib-clean-room-10]
    {
        var root = RepoFile("Proteus");
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.Combine("Interop", "Ipc") + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "TestHarness" + Path.DirectorySeparatorChar))
            .Where(f =>
            {
                var s = File.ReadAllText(f);
                return s.Contains("GetIpcSubscriber", StringComparison.Ordinal) || s.Contains("IpcSubscribers", StringComparison.Ordinal);
            })
            .Select(f => Path.GetRelativePath(Root.Value!, f)).ToList();
        Assert.True(offenders.Count == 0, "outside Interop/Ipc: " + string.Join(", ", offenders));
    }

    [Fact]
    [Trait("Plan", Plan10)]
    [Trait("Plan", "EDGE-contrib-clean-room-54")]
    public void Failure_messages_name_only_the_row_and_fact()   // [AC-contrib-clean-room-10] [EDGE-contrib-clean-room-54]
    {
        var mismatched = new Entry("Penumbra", "Penumbra.Fake", "Func", [typeof(int)], typeof(string));
        var errors = Compare("P99", "Penumbra", mismatched, ("Action", ["System.String"], null));
        Assert.Equal(["Penumbra P99: Kind differs", "Penumbra P99: Arguments differ", "Penumbra P99: Returns differs"], errors);
    }

    [Fact]
    [Trait("Plan", Plan10)]
    public void Cecil_spelling_of_contract_types()   // [AC-contrib-clean-room-10]
    {
        Assert.Equal("System.ValueTuple`2<System.String,System.Int32>", CecilName(typeof((string, int))));
        Assert.Equal("System.Collections.Generic.Dictionary`2<System.String,System.String>", CecilName(typeof(Dictionary<string, string>)));
        Assert.Equal("System.Nullable`1<System.Guid>", CecilName(typeof(Guid?)));
        Assert.Equal("System.String[][]", CecilName(typeof(string[][])));
    }
}
