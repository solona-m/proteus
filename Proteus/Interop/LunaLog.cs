using System;
using Dalamud.Plugin.Services;
using ImSharp;
using Microsoft.Extensions.Logging;

namespace Proteus.Interop;

/// <summary>
/// Points Luna's log at the plugin's. Penumbra.Api and Glamourer.Api take their IPC subscribers from Luna, which catches
/// an exception thrown by an event handler and logs it to <see cref="ImSharpConfiguration"/>'s logger, a null logger
/// until someone sets one: without this a handler that throws on a Glamourer or Penumbra event leaves no trace.
/// </summary>
public sealed class LunaLog : ILogger
{
    private readonly IPluginLog log;

    internal LunaLog(IPluginLog log) => this.log = log;

    /// <summary>Before the bridges subscribe, so a failure to subscribe is logged too.</summary>
    public static void Attach(IPluginLog log) => ImSharpConfiguration.SetLogger(new LunaLog(log));

    /// <summary>After the bridges are disposed: nothing of this instance is logged to once it unloads.</summary>
    public static void Detach() => ImSharpConfiguration.SetLogger(null);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter)
    {
        // The message is already rendered: passed as a value, never as the template, so braces in it stay literal.
        var message = formatter(state, exception);
        switch (logLevel)
        {
            case LogLevel.Trace:       log.Verbose(exception, "[Luna] {Message:l}", message); break;
            case LogLevel.Debug:       log.Debug(exception, "[Luna] {Message:l}", message); break;
            case LogLevel.Information: log.Information(exception, "[Luna] {Message:l}", message); break;
            case LogLevel.Warning:     log.Warning(exception, "[Luna] {Message:l}", message); break;
            case LogLevel.Error:
            case LogLevel.Critical:    log.Error(exception, "[Luna] {Message:l}", message); break;
        }
    }
}
