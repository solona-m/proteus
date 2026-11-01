extern alias RefitCli;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Utf8Console = RefitCli::Proteus.Refit.Utf8Console;

namespace Proteus.Tests;

/// <summary>
/// Proteus.Refit writes UTF-8 without a BOM, whatever the console's code page (#33): a report redirected to a file is
/// read by Python's <c>json.load(open(f, encoding='utf-8'))</c>, which a cp850 em dash or a leading BOM both break.
/// </summary>
public class RefitConsoleEncodingTests
{
    private sealed class Recorder
    {
        public readonly List<Encoding> Set = [];
        public TextWriter? Out, Error;
        public Encoding Original = Encoding.GetEncoding(850);
        public Func<Encoding, Exception?> Throws = _ => null;

        public IDisposable Use(MemoryStream? outStream = null, MemoryStream? errStream = null)
            => Utf8Console.Use(() => Original,
                               e => { if (Throws(e) is { } ex) throw ex; Set.Add(e); },
                               w => Out = w, w => Error = w,
                               () => outStream ?? new MemoryStream(), () => errStream ?? new MemoryStream());
    }

    static RefitConsoleEncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-2")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-5")]
    public void Console_encoding_is_set_to_utf8_without_bom()   // [AC-gh33-36-37-body-size-2] [EDGE-gh33-36-37-body-size-5]
    {
        var r = new Recorder();
        using (r.Use())
        {
            var set = Assert.Single(r.Set);
            Assert.Equal(65001, set.CodePage);
            Assert.Empty(set.GetPreamble());
            Assert.Null(r.Out);
            Assert.Null(r.Error);
        }
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-3")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-4")]
    public void No_console_falls_back_to_utf8_writers()   // [AC-gh33-36-37-body-size-3] [EDGE-gh33-36-37-body-size-4]
    {
        var r = new Recorder { Throws = _ => new IOException("The handle is invalid.") };
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        using (r.Use(stdout, stderr))
        {
            Assert.NotNull(r.Out);
            Assert.NotNull(r.Error);
            r.Out!.Write("Body — e0485");
            r.Error!.Write("Body — e0485");
        }
        byte[] want = Encoding.UTF8.GetBytes("Body — e0485");   // GetBytes never writes the preamble
        Assert.Equal(want, stdout.ToArray());
        Assert.Equal(want, stderr.ToArray());

        // Only "no console" is a fallback; anything else is a bug and is not hidden.
        var other = new Recorder { Throws = _ => new InvalidOperationException() };
        Assert.Throws<InvalidOperationException>(() => other.Use());
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-9")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-23")]
    public void Disposing_restores_the_original_encoding()   // [AC-gh33-36-37-body-size-9] [EDGE-gh33-36-37-body-size-23]
    {
        var r = new Recorder();
        var handle = r.Use();
        handle.Dispose();
        Assert.Equal([65001, 850], r.Set.Select(e => e.CodePage));
        handle.Dispose();   // a second dispose does nothing
        Assert.Equal(2, r.Set.Count);

        // In fallback mode the console's encoding was never changed, so it is not touched at exit.
        var fallback = new Recorder { Throws = _ => new IOException() };
        var fh = fallback.Use();
        fallback.Throws = _ => null;   // a restore call would now be recorded, not swallowed
        fh.Dispose();
        Assert.Empty(fallback.Set);

        // The process is exiting: a restore that fails is not worth an exception.
        var failing = new Recorder();
        var h = failing.Use();
        failing.Throws = e => e.CodePage == 850 ? new IOException() : null;
        h.Dispose();
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-9")]
    public void Main_holds_the_handle_for_the_whole_run()   // [AC-gh33-36-37-body-size-9]
    {
        string program = File.ReadAllText(SourceFile("Proteus.Refit/Program.cs")).Replace("\r\n", "\n");
        int main = program.IndexOf("private static int Main(string[] args)", StringComparison.Ordinal);
        Assert.True(main >= 0);
        string body = program[main..];
        int use = body.IndexOf("using var console = Utf8Console.Use(", StringComparison.Ordinal);
        int loc = body.IndexOf("Loc.SetupWithFallbacks", StringComparison.Ordinal);
        Assert.True(use >= 0 && loc > use, "Main must set the console up first, before anything prints");
        // Nothing but the opening brace and comments before it.
        string before = body[body.IndexOf('{')..use];
        Assert.DoesNotContain(";", string.Join("\n", before.Split('\n').Where(l => !l.TrimStart().StartsWith("//"))));
    }

    [Fact]
    [Trait("Plan", "AC-gh33-36-37-body-size-2")]
    [Trait("Plan", "EDGE-gh33-36-37-body-size-5")]
    public void Redirected_stdout_is_utf8_without_bom()   // [AC-gh33-36-37-body-size-2] [EDGE-gh33-36-37-body-size-5]
    {
        // The exe the RefitCli ProjectReference copies beside this assembly — never a build elsewhere on the machine.
        // On a machine whose OEM code page is already 65001 this passes without the fix; it was seen red on cp850.
        string exe = Path.Combine(AppContext.BaseDirectory, "Proteus.Refit.exe");
        Assert.True(File.Exists(exe), $"{exe} is missing: build Proteus.Tests, which builds and copies the CLI");

        string root = Path.Combine(Path.GetTempPath(), "proteus-utf8-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "files"));
            File.WriteAllBytes(Path.Combine(root, "files", "top_l.mdl"), [0]);
            File.WriteAllBytes(Path.Combine(root, "files", "top_s.mdl"), [0]);
            const string top = "chara/equipment/e0000/model/c0201e0000_top.mdl";
            File.WriteAllText(Path.Combine(root, "meta.json"),
                "{\"FileVersion\":4,\"Name\":\"Body\",\"Author\":\"x\",\"DefaultData\":{\"Files\":{},\"Manipulations\":[]},"
                + "\"Groups\":[{\"Name\":\"Chest\",\"Type\":\"Single\",\"Priority\":0,\"DefaultSettings\":0,\"Options\":["
                + $"{{\"Name\":\"Gen — Large\",\"Files\":{{\"{top}\":\"files/top_l.mdl\"}},\"Manipulations\":[]}},"
                + $"{{\"Name\":\"Small\",\"Files\":{{\"{top}\":\"files/top_s.mdl\"}},\"Manipulations\":[]}}]}}]}}",
                new UTF8Encoding(false));

            // A hidden console at the OEM code page, as a packer script's child gets: the code-page path itself.
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("--list");
            start.ArgumentList.Add("--body-root");
            start.ArgumentList.Add(root);
            using var p = Process.Start(start)!;
            var bytes = new MemoryStream();
            // To the end first (a full pipe would hold the child up), on a task, so a child that hangs fails the test.
            var read = p.StandardOutput.BaseStream.CopyToAsync(bytes);
            if (!read.Wait(60_000))
            {
                p.Kill(entireProcessTree: true);
                Assert.Fail("Proteus.Refit --list did not finish");
            }
            Assert.True(p.WaitForExit(60_000), "Proteus.Refit --list did not exit");
            Assert.Equal(0, p.ExitCode);

            byte[] raw = bytes.ToArray();
            Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "stdout starts with a BOM");
            string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
            Assert.Contains("Gen — Large", text);
            Assert.True(raw.AsSpan().IndexOf(new byte[] { 0xE2, 0x80, 0x94 }) >= 0);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static string SourceFile(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(file);
    }
}
