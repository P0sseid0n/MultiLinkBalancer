// O .exe pode rodar como administrador sem aviso do UAC (tarefa agendada com privilégio máximo)? Só se nenhum usuário
// comum consegue trocá-lo: nem o arquivo, nem a pasta (onde também poderia pôr uma DLL que o .exe carregaria), nem
// renomear uma pasta acima e criar outra no lugar. No Arquivos de Programas isso vale; na Área de Trabalho ou em
// Downloads, não: qualquer programa do usuário trocaria o .exe e ganharia administrador no próximo logon.
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MultiLink.Platform
{
    public static class ProtectedLocation
    {
        // quem pode alterar sem problema: Administradores, SYSTEM e TrustedInstaller
        static bool IsTrusted(SecurityIdentifier sid)
        {
            return sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                   sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // NT SERVICE\TrustedInstaller
        }

        const FileSystemRights Ownership = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        const FileSystemRights FileChange = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | Ownership;
        const FileSystemRights FolderChange = FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories |
                                              FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | Ownership;
        const FileSystemRights AncestorChange = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | Ownership;

        // null se só administradores alteram o .exe e o caminho até ele; senão, o motivo.
        public static string WhyNotProtected(string exePath)
        {
            try
            {
                var problem = Check(File.GetAccessControl(exePath), FileChange, exePath);
                var folder = new DirectoryInfo(Path.GetDirectoryName(exePath));
                if (problem == null) problem = Check(folder.GetAccessControl(), FolderChange, folder.FullName);
                return problem ?? CheckAncestors(folder);
            }
            catch (Exception ex) { return "não foi possível ler as permissões de " + exePath + ": " + ex.Message; }
        }

        // O mesmo para uma pasta de dados (ElevatedWebViewData): só administradores criam, trocam ou apagam algo nela, e
        // ninguém mais consegue renomear uma pasta acima. Junção no caminho também não vale: aponta para outro lugar.
        public static string WhyNotProtectedFolder(string folderPath)
        {
            try
            {
                var folder = new DirectoryInfo(folderPath);
                if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) return folder.FullName + " é uma junção ou um link";
                return Check(folder.GetAccessControl(), FolderChange, folder.FullName) ?? CheckAncestors(folder);
            }
            catch (Exception ex) { return "não foi possível ler as permissões de " + folderPath + ": " + ex.Message; }
        }

        static string CheckAncestors(DirectoryInfo folder)
        {
            for (var ancestor = folder.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                var problem = Check(ancestor.GetAccessControl(), AncestorChange, ancestor.FullName);
                if (problem != null) return problem;
            }
            return null;
        }

        static string Check(FileSystemSecurity security, FileSystemRights dangerous, string path)
        {
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner != null && !IsTrusted(owner)) return path + " pertence a " + Name(owner); // o dono sempre pode mudar as permissões
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                var sid = rule.IdentityReference as SecurityIdentifier;
                if (sid == null || IsTrusted(sid) || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
                if ((rule.FileSystemRights & dangerous) != 0) return Name(sid) + " pode alterar " + path;
            }
            return null;
        }

        static string Name(SecurityIdentifier sid)
        {
            try { return sid.Translate(typeof(NTAccount)).Value; } catch { return sid.Value; }
        }
    }
}
