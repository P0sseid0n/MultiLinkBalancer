using System;
using System.Diagnostics;
using System.IO;

namespace MultiLink.Platform
{
    public class ProcessOutput
    {
        public int ExitCode;
        public string StandardOutput;
        public string StandardError;
    }

    // Executa um programa de linha de comando sem janela e espera terminar.
    public static class ProcessRunner
    {
        // Programa do Windows pelo caminho completo em System32. Pelo nome simples, o Windows procura primeiro na pasta
        // do .exe, que é do usuário (app portátil): um netsh.exe colocado lá rodaria como administrador.
        public static string SystemExe(string name) { return Path.Combine(Environment.SystemDirectory, name); }

        public static string Explorer { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"); } }

        // fileName: caminho completo; um nome simples (netsh.exe) é procurado só em System32.
        public static ProcessOutput Run(string fileName, string arguments)
        {
            if (!Path.IsPathRooted(fileName)) fileName = SystemExe(fileName);
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(startInfo))
            {
                // As duas saídas ao mesmo tempo: lendo uma até o fim antes da outra, o programa que enche o buffer do pipe
                // da segunda (4 KB) para de escrever, e os dois ficam esperando um pelo outro para sempre. O PowerShell
                // com a saída redirecionada escreve o progresso de cada cmdlet no stderr (CLIXML, ~600 bytes por cmdlet):
                // um script com uns 7 cmdlets já travava assim.
                var error = process.StandardError.ReadToEndAsync();
                var output = new ProcessOutput { StandardOutput = process.StandardOutput.ReadToEnd() };
                output.StandardError = error.Result;
                process.WaitForExit();
                output.ExitCode = process.ExitCode;
                return output;
            }
        }
    }
}
