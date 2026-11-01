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
    /// <param name="onCancel">Registers what to run when the user stops the run (Ctrl+C, Ctrl+Break), which ends the
    /// process without unwinding to the caller's <c>using</c>: the code page is put back there too.</param>
    internal static IDisposable Use(Func<Encoding> getConsoleEncoding, Action<Encoding> setConsoleEncoding,
                                    Action<TextWriter> setOut, Action<TextWriter> setError,
                                    Func<Stream> openOut, Func<Stream> openError, Action<Action>? onCancel = null)
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
        var restore = new Restore(() => setConsoleEncoding(original));
        onCancel?.Invoke(restore.Dispose);
        return restore;
    }

    private sealed class Restore(Action? undo) : IDisposable
    {
        private Action? undo = undo;

        /// <summary>Once, whichever comes first: the end of the run, or the user stopping it (another thread).</summary>
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref undo, null) is not { } u) return;
            // Apart: a reader that closed the pipe early fails the flush, and the terminal is still to be put back.
            try
            {
                Console.Out.Flush();
                Console.Error.Flush();
            }
            catch (IOException)
            {
            }
            try
            {
                u();
            }
            catch (IOException)
            {
                // The process is exiting; a console that will not take its code page back is not worth a crash.
            }
        }
    }
}
