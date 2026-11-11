// Pasta de dados do WebView2 da instância elevada: %ProgramData%\MultiLinkBalancer, que só administradores alteram.
// Na pasta do usuário (%LOCALAPPDATA%), qualquer programa dele pode gravar um service worker, um cache ou as preferências
// do navegador para https://app.multilink, e a página plantada falaria com a ponte da instância elevada; por isso lá o
// perfil é limpo a cada abertura, o que atrasa o painel. Aqui não há o que limpar.
// O %ProgramData% deixa qualquer usuário criar pastas: uma MultiLinkBalancer criada antes por outro programa (dona dele,
// ou com permissões abertas, ou uma junção) é apagada e recriada só para Administradores e SYSTEM.
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MultiLink.Platform
{
    public static class ElevatedWebViewData
    {
        public static string Root
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MultiLinkBalancer"); }
        }

        // Pasta pronta para o WebView2 deste usuário, ou null com o motivo (o app volta à pasta do usuário, com limpeza).
        public static string Prepare(out string problem)
        {
            problem = null;
            try
            {
                var root = Root;
                if (Exists(root) && ProtectedLocation.WhyNotProtectedFolder(root) != null) Remove(root);
                if (!Exists(root)) Directory.CreateDirectory(root, AdminsOnly());
                problem = ProtectedLocation.WhyNotProtectedFolder(root); // recriada por outro programa no meio do caminho
                if (problem != null) return null;
                // uma pasta por usuário: o WebView2 não divide a pasta entre navegadores de usuários diferentes
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var folder = Path.Combine(root, "WebView2", identity.User.Value);
                    Directory.CreateDirectory(folder); // herda as permissões da raiz
                    return folder;
                }
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        // Desinstalação: apaga a pasta inteira (os dados do WebView2 de todos os usuários que abriram como administrador).
        public static void Delete()
        {
            if (Exists(Root)) Remove(Root);
        }

        static bool Exists(string path)
        {
            return Directory.Exists(path) || File.Exists(path) || IsLink(path);
        }

        static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; } // não existe, ou é uma junção quebrada que o GetAttributes não alcança
        }

        // Junção ou link: apaga só o link, sem entrar no destino (o Redirection Guard também impede seguir).
        static void Remove(string path)
        {
            if (IsLink(path)) Directory.Delete(path);
            else if (File.Exists(path)) File.Delete(path);
            else Directory.Delete(path, true);
        }

        static DirectorySecurity AdminsOnly()
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var security = new DirectorySecurity();
            security.SetOwner(admins);
            security.SetAccessRuleProtection(true, false); // sem herdar do %ProgramData%, que deixa usuários criarem
            foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            return security;
        }
    }
}
