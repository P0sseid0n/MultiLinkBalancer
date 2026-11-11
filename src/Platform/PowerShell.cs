using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MultiLink.Platform
{
    // Resultado de um comando no Windows: deu certo? Output traz a saída ou a mensagem de erro.
    public class CommandResult
    {
        public bool Ok;
        public string Output;

        public static CommandResult Success(string output) { return new CommandResult { Ok = true, Output = output }; }
        public static CommandResult Failure(string error) { return new CommandResult { Ok = false, Output = error }; }
    }

    public static class PowerShell
    {
        // Texto entre aspas simples do PowerShell. Além do ' o PowerShell aceita como aspa simples as tipográficas
        // (‘ ’ ‚ ‛): todas são dobradas, senão um nome vindo do config.ini fecharia o texto e viraria código.
        public static string Quote(string text)
        {
            var quoted = new StringBuilder("'", text.Length + 2);
            foreach (var c in text)
            {
                if (c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u201A' || c == '\u201B') quoted.Append(c);
                quoted.Append(c);
            }
            return quoted.Append('\'').ToString();
        }

        // Módulos só das pastas do sistema: o caminho padrão começa pela pasta Documentos do usuário, e um módulo
        // gravado lá com o nome de um cmdlet (Get-NetIPInterface...) rodaria como administrador.
        const string SystemModulesOnly =
            "$env:PSModulePath = [Environment]::GetFolderPath('ProgramFiles') + '\\WindowsPowerShell\\Modules;' + $PSHOME + '\\Modules'; ";

        public static string Exe { get { return Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"); } }

        // Executa um script. Sem administrador, allowUac=true pede elevação (aviso do UAC);
        // nesse caso a saída do script não volta, só se deu certo.
        public static Task<CommandResult> Run(string script, bool allowUac)
        {
            return Task.Run(() =>
            {
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(SystemModulesOnly + "$ErrorActionPreference='Stop'; " + script));
                var arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
                if (Elevation.IsAdmin)
                {
                    var output = ProcessRunner.Run(Exe, arguments);
                    return output.ExitCode == 0 ? CommandResult.Success(output.StandardOutput.Trim())
                                                : CommandResult.Failure(output.StandardError.Trim());
                }
                if (!allowUac) return CommandResult.Failure("requer executar como administrador");
                return RunElevated(arguments);
            });
        }

        static CommandResult RunElevated(string arguments)
        {
            var startInfo = new ProcessStartInfo(Exe, arguments)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0 ? CommandResult.Success("") : CommandResult.Failure("falhou (código " + process.ExitCode + ")");
                }
            }
            catch (Win32Exception) { return CommandResult.Failure("cancelado no aviso do Windows (UAC)"); }
        }
    }
}
