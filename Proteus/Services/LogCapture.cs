using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Proteus.Services;

/// <summary>
/// Wraps the plugin's log so a capture session can record every line at every level, Debug included: Dalamud's own
/// minimum level drops exactly the lines (triggers, START, cancellations) a bug report needs. Outside a session it
/// only forwards.
/// </summary>
public sealed class CapturingPluginLog : IPluginLog
{
    // A runaway logger must not eat memory during a capture.
    private const int MaxLines = 50_000;

    private readonly IPluginLog inner;
    private readonly Logger renderer;
    private readonly List<string> lines = [];
    private readonly object gate = new();
    private volatile bool capturing;
    private bool truncated;

    public CapturingPluginLog(IPluginLog inner)
    {
        this.inner = inner;
        // Serilog renders the templates (positional {0} included) and the exception, exactly as the real log would.
        renderer = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new Sink(this))
            .CreateLogger();
    }

    /// <summary>Starts a session, discarding whatever an earlier one left behind.</summary>
    public void BeginCapture()
    {
        lock (gate)
        {
            lines.Clear();
            truncated = false;
            capturing = true;
        }
    }

    /// <summary>Ends the session and returns what it recorded.</summary>
    public List<string> EndCapture()
    {
        lock (gate)
        {
            capturing = false;
            var result = new List<string>(lines);
            if (truncated)
                result.Add($"... capture stopped at {MaxLines} lines");
            lines.Clear();
            return result;
        }
    }

    private void Record(LogEventLevel level, Exception? exception, string messageTemplate, object[] values)
    {
        if (!capturing) return;
        try { renderer.Write(level, exception, messageTemplate, values); }
        catch { /* a bad template must never break the call it rides on */ }
    }

    private void Append(LogEvent e)
    {
        var text = $"{e.Timestamp.LocalDateTime:HH:mm:ss.fff} [{Abbrev(e.Level)}] {e.RenderMessage()}";
        if (e.Exception != null)
            text += Environment.NewLine + e.Exception;
        lock (gate)
        {
            if (!capturing) return;
            if (lines.Count >= MaxLines) { truncated = true; return; }
            lines.Add(text);
        }
    }

    private static string Abbrev(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose     => "VRB",
        LogEventLevel.Debug       => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning     => "WRN",
        LogEventLevel.Error       => "ERR",
        _                         => "FTL",
    };

    private sealed class Sink(CapturingPluginLog owner) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => owner.Append(logEvent);
    }

    // ── IPluginLog: forward, and record while a session is open ──────────────

    public ILogger Logger => inner.Logger;

    public LogEventLevel MinimumLogLevel
    {
        get => inner.MinimumLogLevel;
        set => inner.MinimumLogLevel = value;
    }

    public void Fatal(string messageTemplate, params object[] values)
    { inner.Fatal(messageTemplate, values); Record(LogEventLevel.Fatal, null, messageTemplate, values); }

    public void Fatal(Exception? exception, string messageTemplate, params object[] values)
    { inner.Fatal(exception, messageTemplate, values); Record(LogEventLevel.Fatal, exception, messageTemplate, values); }

    public void Error(string messageTemplate, params object[] values)
    { inner.Error(messageTemplate, values); Record(LogEventLevel.Error, null, messageTemplate, values); }

    public void Error(Exception? exception, string messageTemplate, params object[] values)
    { inner.Error(exception, messageTemplate, values); Record(LogEventLevel.Error, exception, messageTemplate, values); }

    public void Warning(string messageTemplate, params object[] values)
    { inner.Warning(messageTemplate, values); Record(LogEventLevel.Warning, null, messageTemplate, values); }

    public void Warning(Exception? exception, string messageTemplate, params object[] values)
    { inner.Warning(exception, messageTemplate, values); Record(LogEventLevel.Warning, exception, messageTemplate, values); }

    public void Information(string messageTemplate, params object[] values)
    { inner.Information(messageTemplate, values); Record(LogEventLevel.Information, null, messageTemplate, values); }

    public void Information(Exception? exception, string messageTemplate, params object[] values)
    { inner.Information(exception, messageTemplate, values); Record(LogEventLevel.Information, exception, messageTemplate, values); }

    public void Info(string messageTemplate, params object[] values)
    { inner.Info(messageTemplate, values); Record(LogEventLevel.Information, null, messageTemplate, values); }

    public void Info(Exception? exception, string messageTemplate, params object[] values)
    { inner.Info(exception, messageTemplate, values); Record(LogEventLevel.Information, exception, messageTemplate, values); }

    public void Debug(string messageTemplate, params object[] values)
    { inner.Debug(messageTemplate, values); Record(LogEventLevel.Debug, null, messageTemplate, values); }

    public void Debug(Exception? exception, string messageTemplate, params object[] values)
    { inner.Debug(exception, messageTemplate, values); Record(LogEventLevel.Debug, exception, messageTemplate, values); }

    public void Verbose(string messageTemplate, params object[] values)
    { inner.Verbose(messageTemplate, values); Record(LogEventLevel.Verbose, null, messageTemplate, values); }

    public void Verbose(Exception? exception, string messageTemplate, params object[] values)
    { inner.Verbose(exception, messageTemplate, values); Record(LogEventLevel.Verbose, exception, messageTemplate, values); }

    public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values)
    { inner.Write(level, exception, messageTemplate, values); Record(level, exception, messageTemplate, values); }
}
