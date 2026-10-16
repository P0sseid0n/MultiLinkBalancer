using System;
using System.IO;

namespace MultiLink.Storage
{
    public static class AppPaths
    {
        // %APPDATA%\MultiLinkBalancer: ajustes e histórico, que acompanham o usuário
        public static readonly string Roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiLinkBalancer");

        // %LOCALAPPDATA%\MultiLinkBalancer: binários extraídos do .exe e dados do WebView2
        public static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiLinkBalancer");
    }
}
