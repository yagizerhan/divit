// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Side button actions (IN-07): right click and keyboard shortcut via SendInput. No admin rights needed.
// Note: Windows (UIPI) does not let a normal process send input to windows running as administrator.

using System.Runtime.InteropServices;
using Divit.Core;

namespace Divit.Platform.Windows;

public sealed class InputSender : IButtonActions
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    // WinForms Keys layout: low 16 bits are the virtual key code, modifiers above.
    private const int ShiftFlag = 0x10000, CtrlFlag = 0x20000, AltFlag = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    public void RightClick()
    {
        var i = new INPUT[2];
        i[0].type = i[1].type = INPUT_MOUSE;
        i[0].u.mi.dwFlags = MOUSEEVENTF_RIGHTDOWN;   // no move flag: at the cursor (pen) position
        i[1].u.mi.dwFlags = MOUSEEVENTF_RIGHTUP;
        Send(i, "right click");
    }

    public void Shortcut(int keys)
    {
        ushort vk = (ushort)(keys & 0xFFFF);
        if (vk == 0) return;
        var mods = new List<ushort>();
        if ((keys & CtrlFlag) != 0) mods.Add(VK_CONTROL);
        if ((keys & ShiftFlag) != 0) mods.Add(VK_SHIFT);
        if ((keys & AltFlag) != 0) mods.Add(VK_MENU);

        var list = new List<INPUT>();
        foreach (var m in mods) list.Add(Key(m, up: false));
        list.Add(Key(vk, up: false));
        list.Add(Key(vk, up: true));
        for (int k = mods.Count - 1; k >= 0; k--) list.Add(Key(mods[k], up: true));
        Send(list.ToArray(), $"shortcut 0x{keys:X}");
    }

    private static INPUT Key(ushort vk, bool up)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki.wVk = vk;
        i.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        return i;
    }

    // Arrow keys and Insert/Delete/Home/End/PageUp/PageDown are extended keys.
    private static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E;

    private static void Send(INPUT[] inputs, string what)
    {
        uint n = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (n != inputs.Length) Log.Info($"{what} could not be sent (error {Marshal.GetLastWin32Error()}; Windows blocks it when a window running as administrator is in front)");
    }
}
