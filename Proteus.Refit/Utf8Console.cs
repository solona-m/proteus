using System;
using System.IO;
using System.Text;

namespace Proteus.Refit;

/// <summary>
/// The CLI's output as UTF-8 without a BOM, whatever the console's code page.
/// <para/>
/// Its JSON is read by scripts redirecting stdout to a file or a pipe — <c>json.load(open(f, encoding='utf-8'))</c> —
/// and in the console's own code page (cp850, cp1252) an em dash in a piece's label is not valid UTF-8 there. Nor may
/// it start with a BOM, which <c>json.load</c> refuses.
/// </summary>
internal static class Utf8Console
{
    internal static readonly Encoding NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static TextWriter Writer(Stream s) => new StreamWriter(s, NoBom) { AutoFlush = true };

    /// <summary>
    /// Set the console's output encoding to UTF-8 without a BOM — process-wide, so stdout, stderr and every direct
    /// <c>Console.WriteLine</c> — and hand back what puts the original back, so a terminal the CLI ran in is not left
    /// at 65001. Call it before anything reads <c>Console.Out</c>, which takes the encoding of the moment.
    /// <para/>
    /// With no console attached at all (a process started detached), the console's encoding cannot be set: stdout and
    /// stderr become UTF-8 writers instead, and the handle restores nothing. Only that failure falls back.
    /// </summary>
    internal static IDisposable Use(Func<Encoding> getConsoleEncoding, Action<Encoding> setConsoleEncoding,
                                    Action<TextWriter> setOut, Action<TextWriter> setError,
                                    Func<Stream> openOut, Func<Stream> openError)
    {
        Encoding original = getConsoleEncoding();
        try
        {
            setConsoleEncoding(NoBom);
        }
        catch (IOException)
        {
            setOut(Writer(openOut()));
            setError(Writer(openError()));
            return new Restore(null);
        }
        return new Restore(() => setConsoleEncoding(original));
    }

    private sealed class Restore(Action? undo) : IDisposable
    {
        private Action? undo = undo;

        public void Dispose()
        {
            if (undo is not { } u) return;
            undo = null;
            try
            {
                Console.Out.Flush();
                Console.Error.Flush();
                u();
            }
            catch (IOException)
            {
                // The process is exiting; a console that will not take its code page back is not worth a crash.
            }
        }
    }
}
