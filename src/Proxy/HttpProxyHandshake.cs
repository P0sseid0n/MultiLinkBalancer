// Proxy HTTP: CONNECT (túnel, usado pelo HTTPS) e pedidos com URL absoluta (HTTP simples).
using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MultiLink.Common;

namespace MultiLink.Proxy
{
    static class HttpProxyHandshake
    {
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        // cabeçalhos do salto até o proxy: não seguem para o servidor
        static readonly string[] HopByHopHeaders = { "proxy-connection", "proxy-authorization", "connection", "keep-alive" };

        const string BadGatewayResponse =
            "HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\nMultiLink: nenhum link conseguiu conectar ao destino.\r\n";
        const string TunnelOpenResponse = "HTTP/1.1 200 Connection Established\r\n\r\n";
        // resposta a quem abre o endereço do proxy direto no navegador
        const string InfoPageResponse =
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n" +
            "<html><body style='font-family:Segoe UI;padding:2em'><h2>MultiLink Balancer está ativo</h2>" +
            "<p>Configure este endereço como proxy SOCKS5 ou HTTP nos seus aplicativos.</p></body></html>";

        // firstByte: o byte já lido para identificar o protocolo.
        public static async Task<ProxyRequest> Read(NetworkStream client, byte firstByte)
        {
            var buffer = BufferPool.Stream.Rent(); // o cabeçalho pode ter até BufferPool.Stream.Size bytes
            try { return await Read(client, firstByte, buffer); }
            finally { BufferPool.Stream.Return(buffer); }
        }

        static async Task<ProxyRequest> Read(NetworkStream client, byte firstByte, byte[] buffer)
        {
            buffer[0] = firstByte;
            int length = 1, headerEnd;
            while ((headerEnd = FindHeaderEnd(buffer, length)) < 0)
            {
                if (length == buffer.Length) return null;
                int read = await client.ReadAsync(buffer, length, buffer.Length - length);
                if (read <= 0) return null;
                length += read;
            }
            var lines = Latin1.GetString(buffer, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            // o que veio depois do cabeçalho (início do corpo ou do TLS) segue para o destino
            var afterHeader = new byte[length - headerEnd - 4];
            Buffer.BlockCopy(buffer, headerEnd + 4, afterHeader, 0, afterHeader.Length);

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 3) return null;
            string method = requestLine[0], target = requestLine[1], version = requestLine[2];

            var request = new ProxyRequest { ReplyFailure = () => client.WriteAllAsync(Encoding.ASCII.GetBytes(BadGatewayResponse)) };

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                if (!SplitHostPort(target, 443, out request.Host, out request.Port)) return null;
                request.InitialData = afterHeader;
                request.ReplySuccess = () => client.WriteAllAsync(Encoding.ASCII.GetBytes(TunnelOpenResponse));
                return request;
            }

            Uri uri;
            if (!Uri.TryCreate(target, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttp)
            {
                await client.WriteAllAsync(Encoding.UTF8.GetBytes(InfoPageResponse));
                return null;
            }
            request.Host = uri.DnsSafeHost;
            request.Port = uri.Port;
            request.InitialData = Concat(Latin1.GetBytes(RewriteForServer(method, uri, version, lines)), afterHeader);
            request.ReplySuccess = ProxyRequest.NoReply;
            return request;
        }

        // Pedido como o servidor espera: caminho relativo, sem os cabeçalhos do proxy, uma conexão por pedido.
        static string RewriteForServer(string method, Uri uri, string version, string[] lines)
        {
            var rewritten = new StringBuilder();
            rewritten.Append(method).Append(' ').Append(uri.PathAndQuery).Append(' ').Append(version).Append("\r\n");
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0 && Array.IndexOf(HopByHopHeaders, lines[i].Substring(0, colon).Trim().ToLowerInvariant()) >= 0) continue;
                rewritten.Append(lines[i]).Append("\r\n");
            }
            rewritten.Append("Connection: close\r\n\r\n");
            return rewritten.ToString();
        }

        static byte[] Concat(byte[] first, byte[] second)
        {
            var joined = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, joined, 0, first.Length);
            Buffer.BlockCopy(second, 0, joined, first.Length, second.Length);
            return joined;
        }

        // "host:porta", "[ipv6]:porta" ou só o host (usa defaultPort).
        static bool SplitHostPort(string text, int defaultPort, out string host, out int port)
        {
            host = text;
            port = defaultPort;
            if (text.StartsWith("["))
            {
                int close = text.IndexOf(']');
                if (close < 0) return false;
                host = text.Substring(1, close - 1);
                if (close + 2 < text.Length && text[close + 1] == ':') return int.TryParse(text.Substring(close + 2), out port);
                return true;
            }
            int colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon) // um ":" só: IPv6 sem colchetes não tem porta
            {
                host = text.Substring(0, colon);
                return int.TryParse(text.Substring(colon + 1), out port);
            }
            return host.Length > 0;
        }

        // Posição do "\r\n\r\n" que termina o cabeçalho, ou -1.
        static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i + 3 < length; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
            return -1;
        }
    }
}
