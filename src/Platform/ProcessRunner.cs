using System.Diagnostics;

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
        public static ProcessOutput Run(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(startInfo))
            {
                var output = new ProcessOutput
                {
                    StandardOutput = process.StandardOutput.ReadToEnd(),
                    StandardError = process.StandardError.ReadToEnd()
                };
                process.WaitForExit();
                output.ExitCode = process.ExitCode;
                return output;
            }
        }
    }
}
