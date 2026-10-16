using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MultiLink.Common
{
    public static class StreamExtensions
    {
        // Lê exatamente count bytes. Devolve false se a conexão fechar antes.
        public static async Task<bool> ReadExactAsync(this Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        public static Task WriteAllAsync(this Stream stream, byte[] data)
        {
            return stream.WriteAsync(data, 0, data.Length);
        }

        // Texto terminado em byte 0 (SOCKS4). null se a conexão fechar ou passar de maxLength.
        public static async Task<string> ReadNullTerminatedAsync(this Stream stream, int maxLength)
        {
            var text = new StringBuilder();
            var current = new byte[1];
            for (int i = 0; i < maxLength; i++)
            {
                if (!await stream.ReadExactAsync(current, 1)) return null;
                if (current[0] == 0) return text.ToString();
                text.Append((char)current[0]);
            }
            return null;
        }

        // Cabeçalho HTTP até a linha em branco (sem ela). Lê byte a byte para não consumir o corpo.
        public static async Task<string> ReadHttpHeaderAsync(this Stream stream, int maxLength)
        {
            var header = new StringBuilder();
            var current = new byte[1];
            while (header.Length < maxLength)
            {
                if (await stream.ReadAsync(current, 0, 1) <= 0) throw new IOException("conexão encerrada");
                header.Append((char)current[0]);
                int length = header.Length;
                if (length >= 4 && header[length - 1] == '\n' && header[length - 2] == '\r' && header[length - 3] == '\n' && header[length - 4] == '\r')
                    return header.ToString(0, length - 4);
            }
            throw new IOException("cabeçalho muito grande");
        }
    }
}
