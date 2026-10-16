// Atalhos globais: Ctrl+Alt+M liga/desliga o MultiLink; Ctrl+Alt+N abre/fecha o painel.
// Funcionam com qualquer janela em foco (RegisterHotKey); a janela repassa o WM_HOTKEY para cá.
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace MultiLink.Ui
{
    class GlobalHotkeys
    {
        public const string ToggleShortcut = "Ctrl+Alt+M", PanelShortcut = "Ctrl+Alt+N";
        const int ToggleId = 1, PanelId = 2;

        readonly IntPtr window;
        bool registered;

        public event Action TogglePressed;
        public event Action PanelPressed;

        public GlobalHotkeys(IntPtr window) { this.window = window; }

        // Devolve os atalhos que outro programa já usa.
        public List<string> Set(bool enabled)
        {
            var busy = new List<string>();
            if (registered)
            {
                NativeMethods.UnregisterHotKey(window, ToggleId);
                NativeMethods.UnregisterHotKey(window, PanelId);
                registered = false;
            }
            if (!enabled) return busy;
            const uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
            if (!NativeMethods.RegisterHotKey(window, ToggleId, modifiers, (uint)Keys.M)) busy.Add(ToggleShortcut);
            if (!NativeMethods.RegisterHotKey(window, PanelId, modifiers, (uint)Keys.N)) busy.Add(PanelShortcut);
            registered = true;
            return busy;
        }

        public void OnHotkeyMessage(int id)
        {
            var handler = id == ToggleId ? TogglePressed : id == PanelId ? PanelPressed : null;
            if (handler != null) handler();
        }
    }
}
