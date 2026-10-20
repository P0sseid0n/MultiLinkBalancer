// Etapas da partida, do processo aberto até a configuração começar, com o tempo de cada uma. Junto com o Windows
// a partida chegou a levar 20 s (com tudo abrindo ao mesmo tempo); o resumo vai para o registro de eventos.
// Só a thread da interface marca, antes de o ciclo de 1 s começar.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MultiLink.Common
{
    public static class StartupTimeline
    {
        static readonly List<string> steps = new List<string>();
        static DateTime last = Process.GetCurrentProcess().StartTime;

        // step: o que terminou desde a marca anterior
        public static void Mark(string step)
        {
            var now = DateTime.Now;
            steps.Add(step + " " + (now - last).TotalSeconds.ToString("0.0") + " s");
            last = now;
        }

        public static string Summary() { return string.Join(", ", steps); }
    }
}
