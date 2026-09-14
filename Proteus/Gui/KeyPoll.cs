using System;
using System.Runtime.InteropServices;

namespace Proteus.Gui;

/// <summary>
/// Keyboard shortcuts read straight from Windows, because ImGui never hears them.
/// <para/>
/// Dalamud's input handler hands ImGui a key other than a modifier ONLY while a text box has the keyboard
/// (<c>Win32InputHandler</c>: <c>if (key != ImGuiKey.None &amp;&amp; io.WantTextInput)</c>); everything else goes
/// to the game. So <c>ImGui.IsKeyPressed(ImGuiKey.Z)</c> is never true outside a text box, and a shortcut built on
/// it silently does nothing. Modifiers do reach ImGui, so <c>io.KeyCtrl</c> and <c>io.KeyShift</c> stay usable.
/// </summary>
internal static class KeyPoll
{
    public const int VkZ = 0x5A;
    public const int VkLeftBracket = 0xDB;    // VK_OEM_4
    public const int VkRightBracket = 0xDD;   // VK_OEM_6

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>Whether the key is held right now.</summary>
    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// The foreground window belongs to the game's process — so a key pressed while typing in a browser does not
    /// reach a shortcut here. GetAsyncKeyState reads the keyboard whichever window has it.
    /// </summary>
    public static bool GameHasFocus()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}

/// <summary>
/// One key, polled once a frame: true on the frame it goes down and, when <see cref="repeat"/>, again at the
/// usual key-repeat pace while it is held. Poll it EVERY frame, even when the answer is not wanted, so a release
/// is never missed and a key held through a gated frame does not read as a fresh press afterwards.
/// </summary>
internal sealed class HeldKey(int vk, bool repeat)
{
    private const long RepeatDelayMs = 400, RepeatEveryMs = 60;
    private long downAt = -1, lastFired;

    public bool Poll()
    {
        long now = Environment.TickCount64;
        if (!KeyPoll.IsDown(vk)) { downAt = -1; return false; }
        if (downAt < 0) { downAt = lastFired = now; return true; }
        if (!repeat || now - downAt < RepeatDelayMs || now - lastFired < RepeatEveryMs) return false;
        lastFired = now;
        return true;
    }
}
