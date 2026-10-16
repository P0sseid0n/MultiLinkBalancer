// Funções do Windows usadas pela janela (user32 e dwmapi).
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiLink.Ui
{
    static class NativeMethods
    {
        public const int WM_WINDOWPOSCHANGING = 0x46, WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_HOTKEY = 0x312;
        public const int HTCLIENT = 1;
        public const int SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWCP_ROUND = 2;
        public const int GA_ROOT = 2;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy, flags; }

        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxLength);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
