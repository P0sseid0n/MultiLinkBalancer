// Proxy do Windows (Opções da Internet do usuário). Antes de apontar para o MultiLink, guarda a
// configuração anterior no config.ini ("prev.*") para devolvê-la ao desligar, ao sair ou na
// próxima abertura, se o app tiver fechado sem restaurar.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MultiLink.Storage;

namespace MultiLink.Platform
{
    public static class SystemProxy
    {
        const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        const string Missing = "\u0000"; // marca de "valor não existia" no config.ini
        // o próprio PC (inclusive 0.0.0.0, que os servidores de desenvolvimento mostram), as redes privadas inteiras (a
        // 172.16.0.0/12 vai até 172.31: o WSL e o Docker usam 172.17 a 172.31) e o link-local (169.254)
        static readonly string Bypass = "<local>;localhost;*.localhost;127.*;0.0.0.0;[::1];10.*;" +
            string.Concat(Enumerable.Range(16, 16).Select(second => "172." + second + ".*;")) + "192.168.*;169.254.*;*.multilink";
        const int INTERNET_OPTION_SETTINGS_CHANGED = 39, INTERNET_OPTION_REFRESH = 37;

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);

        public static void Apply(AppSettings settings, int port)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey))
            {
                if (!settings.SystemProxyApplied)
                {
                    var enabled = key.GetValue("ProxyEnable");
                    settings.Set("prev.ProxyEnable", enabled == null ? "" : enabled.ToString());
                    settings.Set("prev.ProxyServer", (key.GetValue("ProxyServer") ?? Missing).ToString());
                    settings.Set("prev.ProxyOverride", (key.GetValue("ProxyOverride") ?? Missing).ToString());
                }
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", "127.0.0.1:" + port);
                key.SetValue("ProxyOverride", Bypass);
            }
            settings.SystemProxyApplied = true;
            settings.Save();
            NotifyWindows();
        }

        public static void Restore(AppSettings settings)
        {
            if (!settings.SystemProxyApplied) return;
            using (var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey))
            {
                int enabled;
                if (!int.TryParse(settings.Get("prev.ProxyEnable", ""), out enabled)) enabled = 0;
                key.SetValue("ProxyEnable", enabled, RegistryValueKind.DWord);
                RestoreString(key, "ProxyServer", settings.Get("prev.ProxyServer", Missing));
                RestoreString(key, "ProxyOverride", settings.Get("prev.ProxyOverride", Missing));
            }
            settings.SystemProxyApplied = false;
            settings.Save();
            NotifyWindows();
        }

        static void RestoreString(RegistryKey key, string name, string value)
        {
            if (value == Missing) key.DeleteValue(name, false);
            else key.SetValue(name, value);
        }

        // Avisa os programas abertos que a configuração de proxy mudou.
        static void NotifyWindows()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }
}
