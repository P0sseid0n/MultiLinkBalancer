// Buffers reaproveitados entre conexões. Alocar um novo por conexão gera um fluxo constante de lixo
// que sobrevive o bastante para chegar à geração 2 (conexões de segundos, associações UDP de 60 s);
// o GC em segundo plano não compacta essa geração e o processo fica com centenas de MB parados.
using System.Collections.Concurrent;

namespace MultiLink.Common
{
    public class BufferPool
    {
        // cópia dos túneis TCP e cabeçalho do proxy HTTP: até 16 MB guardados
        public static readonly BufferPool Stream = new BufferPool(65536, 256);
        // datagramas UDP: um programa P2P ao ligar a captura abre milhares de associações ao mesmo tempo, cada uma
        // com leituras pendentes; 64 KB por leitura somavam ~1 GB. Na internet o datagrama passa pouco de 1.500 bytes
        // (resposta grande de DNS: até 4 KB); um maior que isto é descartado.
        public static readonly BufferPool Datagram = new BufferPool(8192, 1024);

        public readonly int Size;
        readonly int maxPooled; // acima disso o excedente volta para o GC
        readonly ConcurrentBag<byte[]> free = new ConcurrentBag<byte[]>();

        BufferPool(int size, int maxPooled)
        {
            Size = size;
            this.maxPooled = maxPooled;
        }

        public byte[] Rent()
        {
            byte[] buffer;
            return free.TryTake(out buffer) ? buffer : new byte[Size];
        }

        // Só devolva quando nenhuma leitura pendente puder mais escrever no buffer.
        public void Return(byte[] buffer)
        {
            if (buffer != null && buffer.Length == Size && free.Count < maxPooled) free.Add(buffer);
        }
    }
}
