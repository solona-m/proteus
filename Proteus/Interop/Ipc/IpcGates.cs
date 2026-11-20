using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace Proteus.Interop.Ipc;

/// <summary>
/// Where the call gates report their own failures: a gate Dalamud would not hand out, a subscription that could not
/// be made, or an event handler that threw. Attached before the bridges are built and detached after they are disposed.
/// </summary>
internal static class IpcLog
{
    private static volatile IPluginLog? log;

    public static void Attach(IPluginLog pluginLog) => log = pluginLog;

    public static void Detach() => log = null;

    internal static void Error(Exception ex, string message, string label) => log?.Error(ex, message, label);
}

/// <summary>What every gate kind shares: its label, and asking Dalamud for the gate without ever throwing.</summary>
public abstract class IpcGate
{
    protected IpcGate(string label) => GateLabel = label;

    /// <summary>The label the gate was requested under.</summary>
    public string GateLabel { get; }

    /// <summary>The gate, or null (logged) when Dalamud refused it. Dalamud hands one out whether or not the owner is loaded.</summary>
    protected static TGate? TryCreate<TGate>(string label, Func<TGate> create) where TGate : class
    {
        try { return create(); }
        catch (Exception ex)
        {
            IpcLog.Error(ex, "[Proteus] could not create the IPC gate {0}", label);
            return null;
        }
    }
}

/// <summary>A function gate. Invoking it with no gate, or with no provider registered, throws <see cref="IpcNotReadyError"/>.</summary>
public abstract class IpcFuncGate(string label) : IpcGate(label)
{
    protected IpcNotReadyError NotReady() => new(GateLabel);
}

/// <summary>An action gate. Invoking it with no gate does nothing; with no provider registered Dalamud throws.</summary>
public abstract class IpcActionGate(string label) : IpcGate(label);

/// <summary>
/// An event gate, subscribed once on construction with one handler. The handler runs on whichever thread the owner
/// raises the event on, synchronously; nothing is marshalled. A handler that throws is logged and never reaches the
/// owner. Dispose unsubscribes; a second Dispose does nothing.
/// </summary>
public abstract class IpcEventGate(string label) : IpcGate(label), IDisposable
{
    private int disposed;

    protected void ReportHandlerError(Exception ex)
        => IpcLog.Error(ex, "[Proteus] a handler of the IPC event {0} threw", GateLabel);

    protected void ReportSubscribeError(Exception ex)
        => IpcLog.Error(ex, "[Proteus] could not subscribe to the IPC event {0}", GateLabel);

    protected abstract void Unsubscribe();

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0) return;
        Unsubscribe();
        GC.SuppressFinalize(this);
    }
}

// ── Function gates, by argument count ──────────────────────────────────────────────────────────

public abstract class IpcFunc<TRet> : IpcFuncGate
{
    private readonly ICallGateSubscriber<TRet>? gate;

    protected IpcFunc(IDalamudPluginInterface pi, string label) : base(label)
        => gate = TryCreate(label, () => pi.GetIpcSubscriber<TRet>(label));

    protected TRet Call() => (gate ?? throw NotReady()).InvokeFunc();
}

public abstract class IpcFunc<T1, TRet> : IpcFuncGate
{
    private readonly ICallGateSubscriber<T1, TRet>? gate;

    protected IpcFunc(IDalamudPluginInterface pi, string label) : base(label)
        => gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, TRet>(label));

    protected TRet Call(T1 a1) => (gate ?? throw NotReady()).InvokeFunc(a1);
}

public abstract class IpcFunc<T1, T2, TRet> : IpcFuncGate
{
    private readonly ICallGateSubscriber<T1, T2, TRet>? gate;

    protected IpcFunc(IDalamudPluginInterface pi, string label) : base(label)
        => gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, T2, TRet>(label));

    protected TRet Call(T1 a1, T2 a2) => (gate ?? throw NotReady()).InvokeFunc(a1, a2);
}

public abstract class IpcFunc<T1, T2, T3, TRet> : IpcFuncGate
{
    private readonly ICallGateSubscriber<T1, T2, T3, TRet>? gate;

    protected IpcFunc(IDalamudPluginInterface pi, string label) : base(label)
        => gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, T2, T3, TRet>(label));

    protected TRet Call(T1 a1, T2 a2, T3 a3) => (gate ?? throw NotReady()).InvokeFunc(a1, a2, a3);
}

public abstract class IpcFunc<T1, T2, T3, T4, TRet> : IpcFuncGate
{
    private readonly ICallGateSubscriber<T1, T2, T3, T4, TRet>? gate;

    protected IpcFunc(IDalamudPluginInterface pi, string label) : base(label)
        => gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, T2, T3, T4, TRet>(label));

    protected TRet Call(T1 a1, T2 a2, T3 a3, T4 a4) => (gate ?? throw NotReady()).InvokeFunc(a1, a2, a3, a4);
}

public abstract class IpcFunc<T1, T2, T3, T4, T5, TRet> : IpcFuncGate
{
    private readonly ICallGateSubscriber<T1, T2, T3, T4, T5, TRet>? gate;

    protected IpcFunc(IDalamudPluginInterface pi, string label) : base(label)
        => gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, T2, T3, T4, T5, TRet>(label));

    protected TRet Call(T1 a1, T2 a2, T3 a3, T4 a4, T5 a5) => (gate ?? throw NotReady()).InvokeFunc(a1, a2, a3, a4, a5);
}

public abstract class IpcFunc<T1, T2, T3, T4, T5, T6, TRet> : IpcFuncGate
{
    private readonly ICallGateSubscriber<T1, T2, T3, T4, T5, T6, TRet>? gate;

    protected IpcFunc(IDalamudPluginInterface pi, string label) : base(label)
        => gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, T2, T3, T4, T5, T6, TRet>(label));

    protected TRet Call(T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6)
        => (gate ?? throw NotReady()).InvokeFunc(a1, a2, a3, a4, a5, a6);
}

// ── Action gates ───────────────────────────────────────────────────────────────────────────────

public abstract class IpcAction<T1, T2> : IpcActionGate
{
    private readonly ICallGateSubscriber<T1, T2, object?>? gate;

    protected IpcAction(IDalamudPluginInterface pi, string label) : base(label)
        => gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, T2, object?>(label));

    protected void Call(T1 a1, T2 a2) => gate?.InvokeAction(a1, a2);
}

// ── Event gates, by argument count ─────────────────────────────────────────────────────────────

public abstract class IpcEvent : IpcEventGate
{
    private readonly ICallGateSubscriber<object?>? gate;
    private Action? relay;

    protected IpcEvent(IDalamudPluginInterface pi, string label, Action handler) : base(label)
    {
        gate = TryCreate(label, () => pi.GetIpcSubscriber<object?>(label));
        if (gate == null) return;
        Action wrapped = () =>
        {
            try { handler(); }
            catch (Exception ex) { ReportHandlerError(ex); }
        };
        try { gate.Subscribe(wrapped); relay = wrapped; }
        catch (Exception ex) { ReportSubscribeError(ex); }
    }

    protected override void Unsubscribe()
    {
        if (gate != null && relay != null) gate.Unsubscribe(relay);
        relay = null;
    }
}

public abstract class IpcEvent<T1> : IpcEventGate
{
    private readonly ICallGateSubscriber<T1, object?>? gate;
    private Action<T1>? relay;

    protected IpcEvent(IDalamudPluginInterface pi, string label, Action<T1> handler) : base(label)
    {
        gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, object?>(label));
        if (gate == null) return;
        Action<T1> wrapped = a1 =>
        {
            try { handler(a1); }
            catch (Exception ex) { ReportHandlerError(ex); }
        };
        try { gate.Subscribe(wrapped); relay = wrapped; }
        catch (Exception ex) { ReportSubscribeError(ex); }
    }

    protected override void Unsubscribe()
    {
        if (gate != null && relay != null) gate.Unsubscribe(relay);
        relay = null;
    }
}

public abstract class IpcEvent<T1, T2> : IpcEventGate
{
    private readonly ICallGateSubscriber<T1, T2, object?>? gate;
    private Action<T1, T2>? relay;

    protected IpcEvent(IDalamudPluginInterface pi, string label, Action<T1, T2> handler) : base(label)
    {
        gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, T2, object?>(label));
        if (gate == null) return;
        Action<T1, T2> wrapped = (a1, a2) =>
        {
            try { handler(a1, a2); }
            catch (Exception ex) { ReportHandlerError(ex); }
        };
        try { gate.Subscribe(wrapped); relay = wrapped; }
        catch (Exception ex) { ReportSubscribeError(ex); }
    }

    protected override void Unsubscribe()
    {
        if (gate != null && relay != null) gate.Unsubscribe(relay);
        relay = null;
    }
}

public abstract class IpcEvent<T1, T2, T3, T4> : IpcEventGate
{
    private readonly ICallGateSubscriber<T1, T2, T3, T4, object?>? gate;
    private Action<T1, T2, T3, T4>? relay;

    protected IpcEvent(IDalamudPluginInterface pi, string label, Action<T1, T2, T3, T4> handler) : base(label)
    {
        gate = TryCreate(label, () => pi.GetIpcSubscriber<T1, T2, T3, T4, object?>(label));
        if (gate == null) return;
        Action<T1, T2, T3, T4> wrapped = (a1, a2, a3, a4) =>
        {
            try { handler(a1, a2, a3, a4); }
            catch (Exception ex) { ReportHandlerError(ex); }
        };
        try { gate.Subscribe(wrapped); relay = wrapped; }
        catch (Exception ex) { ReportSubscribeError(ex); }
    }

    protected override void Unsubscribe()
    {
        if (gate != null && relay != null) gate.Unsubscribe(relay);
        relay = null;
    }
}
