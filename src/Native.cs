using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BlueClipboard
{
    internal static class Native
    {
        internal const int OpenMessage = 0x8001;
        [DllImport("user32.dll")] internal static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int cmd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls, string title);
        [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public KeyInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        internal static Input Key(ushort key, bool up) { return new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyInput { Key = key, Flags = up ? 2u : 0u } } }; }
        internal static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        internal static bool ModifiersDown() { return Down(0x5B) || Down(0x5C) || Down(0x10) || Down(0x11) || Down(0x12) || Down(0x0D) || Down(0x56); }
        internal static bool Paste()
        {
            var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
            return SendInput(4, inputs, Marshal.SizeOf(typeof(Input))) == 4;
        }
        internal static string SourceName()
        {
            try { uint pid; GetWindowThreadProcessId(GetClipboardOwner(), out pid); return pid == 0 ? "Windows" : Process.GetProcessById((int)pid).ProcessName; }
            catch { return "Windows"; }
        }
    }

}
