// Tempo que o PC passou dormindo (suspensão ou hibernação): o relógio de interrupções "sem viés" do Windows para
// enquanto o PC dorme, e o GetTickCount64 não. A diferença entre os dois só cresce com o PC dormindo; um travamento
// do programa não conta, ao contrário de um intervalo grande entre duas leituras do relógio comum.
using System.Runtime.InteropServices;

namespace MultiLink.Platform
{
    public static class SleepClock
    {
        [DllImport("kernel32.dll")] static extern bool QueryUnbiasedInterruptTime(out ulong time);
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

        // Total dormido desde que o Windows ligou, em ms.
        public static long SleptMs()
        {
            ulong unbiased; // em unidades de 100 ns
            QueryUnbiasedInterruptTime(out unbiased);
            return (long)GetTickCount64() - (long)(unbiased / 10000);
        }
    }
}
