using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.Refit;

/// <summary>
/// <c>--size-set</c>: the in-game "Every size for other bodies" on an unpacked mod, without the game
/// (<see cref="SizeSetBatch"/>). Writes the sizes into that folder — a work copy, converted from v3 first when it is one —
/// and prints the report on stdout; the log goes to stderr.
/// </summary>
internal static class SizeSetCommand
{
    /// <returns>0 when it ran, whatever was refused; 1 when nothing could run or it was stopped; 2 on a usage error.</returns>
    internal static int Run(Dictionary<string, string> opts, Func<Dictionary<string, string>, string, string> required,
                            TextWriter stdout, TextWriter stderr)
    {
        string modRoot = required(opts, "size-set");
        string modsRoot = required(opts, "mods-root");
        string game = required(opts, "game");
        string pluginDir = required(opts, "uvmaps");
        bool men = opts.ContainsKey("men"), ends = !opts.ContainsKey("no-ends");
        string? skeleton = opts.GetValueOrDefault("skeleton");
        var keys = SizeSetBatch.ParseKeys(required(opts, "bodies"), out var keyError)
                   ?? throw new Program.UsageException(keyError!);
        if (SizeSetBatch.Validate(modRoot, modsRoot, game, skeleton) is { } error) throw new Program.UsageException(error);
        // The report names this machine's folders: never inside the mod that ships, nor among the installed mods.
        if (opts.TryGetValue("report", out var reportAt)
            && (PenumbraModMeta.IsAtOrUnder(reportAt, modRoot) || PenumbraModMeta.IsAtOrUnder(reportAt, modsRoot)))
            throw new Program.UsageException($"--report {reportAt} is inside the mod or the mod folder: write it elsewhere");
        if (reportAt != null && Path.GetDirectoryName(Path.GetFullPath(reportAt)) is { } reportDir && !Directory.Exists(reportDir))
            throw new Program.UsageException($"--report {reportAt}: the folder {reportDir} does not exist");
        if (UvMapFiles.Missing(pluginDir, men) is { Count: > 0 } missing)
            throw new Program.UsageException($"--uvmaps {pluginDir}: missing or not a real map (a Git LFS pointer?): "
                                           + string.Join(", ", missing));

        var log = ConsoleLog.To(stderr);
        // Opened at the first read: a mod with nothing to size never needs it.
        var data = new Lazy<Lumina.GameData>(() => new Lumina.GameData(game));
        byte[]? ReadGame(string path)
        {
            var opened = data.Value;   // a game folder Lumina cannot open fails the run, said as itself
            try { return opened.GetFile(path)?.Data; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Most paths asked for are only maybe there; a file there that cannot be read is said.
                log.Warning(e, "[Proteus] size set: the game's {0} could not be read", path);
                return null;
            }
        }
        var uvRemap = new UVRemapService(log, pluginDir);
        using var cancel = new CancellationTokenSource();
        // Stopped: the sizes saved so far stand and are reported; the process waits for the size being made.
        ConsoleCancelEventHandler stop = (_, e) =>
        {
            e.Cancel = true;
            stderr.WriteLine("Stopping after the size being made…");
            cancel.Cancel();
        };
        Console.CancelKeyPress += stop;
        var runLog = new SizeSetRefit.RunLog();
        SizeSetBatch.Outcome outcome;
        try
        {
            outcome = SizeSetBatch.Run(new SizeSetBatch.Inputs(modRoot, modsRoot, keys, men, ends, ReadGame, uvRemap, log,
                                                               skeleton == null ? null : Path.GetFullPath(skeleton)),
                                       runLog, p => stderr.WriteLine(p), cancel.Token);
        }
        finally
        {
            Console.CancelKeyPress -= stop;
        }

        string report = SizeSetReport.ToText(SizeSetReport.Build(modRoot, Plugin.BuildNumber, outcome, runLog, men, ends));
        stdout.WriteLine(report);
        if (opts.TryGetValue("report", out var reportPath)) File.WriteAllText(reportPath, report + Environment.NewLine);
        return ExitCode(outcome);
    }

    /// <summary>0 ran; 1 refused before anything ran, or stopped.</summary>
    internal static int ExitCode(SizeSetBatch.Outcome outcome) => outcome.RefusalCode != null || outcome.Cancelled ? 1 : 0;
}

/// <summary>
/// A log written to a writer — the CLI's stderr: information, warnings and errors, each on a line; debug and verbose
/// dropped. Public and unsealed, as <see cref="DispatchProxy"/> requires.
/// </summary>
public class ConsoleLog : DispatchProxy
{
    private TextWriter writer = TextWriter.Null;

    internal static IPluginLog To(TextWriter writer)
    {
        var log = Create<IPluginLog, ConsoleLog>();
        ((ConsoleLog)(object)log).writer = writer;
        return log;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        string? level = method?.Name switch
        {
            "Information" or "Info" => "info",
            "Warning" => "warn",
            "Error" or "Fatal" => "error",
            _ => null,
        };
        if (level != null && args != null)
        {
            var exception = Array.Find(args, a => a is Exception) as Exception;
            int at = Array.FindIndex(args, a => a is string);
            if (at >= 0)
            {
                string template = (string)args[at]!;
                var values = at + 1 < args.Length && args[at + 1] is object?[] v ? v : [];
                string line;
                try { line = values.Length > 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, template, values) : template; }
                catch (FormatException) { line = template; }
                lock (writer) writer.WriteLine($"[{level}] {line}{(exception != null ? ": " + exception.Message : "")}");
            }
        }
        return method?.ReturnType is { IsValueType: true } t && t != typeof(void) ? Activator.CreateInstance(t) : null;
    }
}
