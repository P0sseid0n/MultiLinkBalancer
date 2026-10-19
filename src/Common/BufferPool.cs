// Buffers de 64 KB reaproveitados entre conexões. Alocar um novo por conexão gera um fluxo constante
// de lixo que sobrevive o bastante para chegar à geração 2 (conexões de segundos, associações UDP de
// 60 s); o GC em segundo plano não compacta essa geração e o processo fica com centenas de MB parados.
using System.Collections.Concurrent;

namespace MultiLink.Common
{
    public static class BufferPool
    {
        public const int Size = 65536; // cabe o maior datagrama UDP
        const int MaxPooled = 256;     // até 16 MB guardados; acima disso o excedente volta para o GC

        static readonly ConcurrentBag<byte[]> free = new ConcurrentBag<byte[]>();

        public static byte[] Rent()
        {
            byte[] buffer;
            return free.TryTake(out buffer) ? buffer : new byte[Size];
        }

        // Só devolva quando nenhuma leitura pendente puder mais escrever no buffer.
        public static void Return(byte[] buffer)
        {
            if (buffer != null && free.Count < MaxPooled) free.Add(buffer);
        }
    }
}
