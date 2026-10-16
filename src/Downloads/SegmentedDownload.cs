// Download acelerado: divide o arquivo em pedaços (HTTP Range) e baixa por todos os links ao mesmo
// tempo. Cada link recebe vários "workers" que pegam o próximo pedaço livre: o link mais rápido
// naturalmente baixa mais pedaços, então as bandas se somam.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Downloads
{
    // Resultado da consulta inicial ao servidor.
    public class DownloadProbe
    {
        public Uri FinalUri;          // após redirecionamentos
        public long Size;             // -1 se o servidor não informar
        public bool SupportsRanges;   // aceita download em partes
    }

    public class SegmentedDownload
    {
        const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MultiLinkBalancer/1.0";
        const int WorkersPerLink = 4;
        const long MinChunkBytes = 1L << 20, MaxChunkBytes = 8L << 20;
        const int ConnectTimeoutMs = 8000;
        const int StalledSeconds = 20;          // conexão sem receber nada por 20 s é fechada (o pedaço volta para a fila)
        const int FailuresBeforeRetire = 3, FailuresBeforeGivingUp = 8;
        const int MaxHeaderBytes = 32768;

        // o que todos os workers de um download compartilham
        class Job
        {
            public Uri Uri;
            public IPAddress Server;
            public ConcurrentQueue<long> PendingChunks;  // posição inicial de cada pedaço ainda não baixado
            public long ChunkBytes;
            public long Total;
            public string Path;
            public CancellationToken Token;
        }

        class Worker { public Socket Socket; public long LastActivity; }

        // o servidor respondeu 429/503: está limitando conexões por IP
        class ThrottledException : IOException
        {
            public readonly int RetryAfterSeconds;
            public ThrottledException(string statusLine, int retryAfterSeconds) : base(statusLine) { RetryAfterSeconds = retryAfterSeconds; }
        }

        readonly ActivityLog log;
        readonly ConcurrentBag<Worker> workers = new ConcurrentBag<Worker>();
        readonly Dictionary<Link, int> workersPerLink = new Dictionary<Link, int>();
        int chunksInFlight; // workers com um pedaço em mãos (ou procurando um)

        public long Total = -1;
        public long Done;
        public readonly ConcurrentDictionary<Link, long> BytesPerLink = new ConcurrentDictionary<Link, long>();

        public SegmentedDownload(ActivityLog log) { this.log = log; }

        // Pede só o primeiro byte para descobrir o tamanho, a URL final e se o servidor aceita Range.
        public static async Task<DownloadProbe> Probe(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.AllowAutoRedirect = true;
            request.Timeout = 20000;
            request.AddRange(0L, 0L);
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            {
                var probe = new DownloadProbe { FinalUri = response.ResponseUri, Size = -1 };
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    // Content-Range: bytes 0-0/<tamanho>
                    var contentRange = response.Headers["Content-Range"] ?? "";
                    int slash = contentRange.LastIndexOf('/');
                    long size = -1;
                    probe.SupportsRanges = slash > 0 && long.TryParse(contentRange.Substring(slash + 1), out size) && size > 0;
                    if (probe.SupportsRanges) probe.Size = size;
                }
                else probe.Size = response.ContentLength;
                return probe;
            }
        }

        public async Task Run(Uri uri, long total, string path, List<Link> links, CancellationToken token)
        {
            Total = total;
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) file.SetLength(total);
            var job = new Job
            {
                Uri = uri, Total = total, Path = path, Token = token,
                ChunkBytes = Math.Max(MinChunkBytes, Math.Min(MaxChunkBytes, total / (links.Count * WorkersPerLink * 4))),
                PendingChunks = new ConcurrentQueue<long>()
            };
            for (long offset = 0; offset < total; offset += job.ChunkBytes) job.PendingChunks.Enqueue(offset);

            job.Server = (await Dns.GetHostAddressesAsync(uri.DnsSafeHost)).FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
            if (job.Server == null) throw new IOException("o servidor não tem endereço IPv4");

            var running = new List<Task>();
            foreach (var link in links)
                for (int i = 0; i < WorkersPerLink; i++)
                    running.Add(RunWorker(link, job));

            var all = Task.WhenAll(running);
            while (await Task.WhenAny(all, Task.Delay(2000)) != all) CloseStalledConnections(token);
            token.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref Done) < total) throw new IOException("download incompleto: todos os links falharam");
        }

        // Fecha conexões paradas (o worker recoloca o pedaço na fila) e, ao cancelar, todas.
        void CloseStalledConnections(CancellationToken token)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var worker in workers)
            {
                var socket = worker.Socket;
                if (socket == null) continue;
                bool stalled = now - Interlocked.Read(ref worker.LastActivity) > Stopwatch.Frequency * StalledSeconds;
                if (stalled || token.IsCancellationRequested) socket.CloseQuietly();
            }
        }

        async Task RunWorker(Link link, Job job)
        {
            var worker = new Worker();
            workers.Add(worker);
            Stream connection = null;
            int failures = 0, waitMs = 0;
            bool retired = false;
            lock (workersPerLink) { int count; workersPerLink.TryGetValue(link, out count); workersPerLink[link] = count + 1; }
            var buffer = new byte[65536];
            using (var file = new FileStream(job.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                while (!job.Token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref chunksInFlight);
                    long offset;
                    if (!job.PendingChunks.TryDequeue(out offset))
                    {
                        // fila vazia: espera caso algum pedaço em andamento falhe e volte para a fila
                        if (Interlocked.Decrement(ref chunksInFlight) == 0 && job.PendingChunks.IsEmpty) break;
                        await Task.Delay(200);
                        continue;
                    }
                    try
                    {
                        if (connection == null) connection = await Connect(link, job, worker);
                        bool serverClosing = await DownloadChunk(connection, link, job, offset, file, buffer, worker);
                        if (serverClosing) { connection.Dispose(); connection = null; }
                        failures = 0;
                    }
                    catch (Exception ex)
                    {
                        job.PendingChunks.Enqueue(offset);
                        DisposeQuietly(connection);
                        connection = null;
                        var throttled = ex as ThrottledException;
                        if (throttled != null)
                        {
                            // servidor limitando conexões por IP: reduz as conexões deste link, mas mantém ao menos uma
                            if ((retired = TryRetireWorker(link)))
                            {
                                log.Add(string.Format("[{0}] servidor pediu menos conexões ({1}); usando {2} neste link", link.Name, throttled.Message, WorkerCount(link)));
                                Interlocked.Decrement(ref chunksInFlight);
                                break;
                            }
                            waitMs = Math.Max(1000, Math.Min(15000, throttled.RetryAfterSeconds * 1000));
                        }
                        else if (!job.Token.IsCancellationRequested && ++failures >= FailuresBeforeRetire &&
                                 ((retired = TryRetireWorker(link)) || failures >= FailuresBeforeGivingUp))
                        {
                            log.Add(string.Format("[{0}] download: conexão desistiu após 3 falhas ({1})", link.Name, ex.Message));
                            Interlocked.Decrement(ref chunksInFlight);
                            break;
                        }
                        else waitMs = 1000 * Math.Min(failures, 10);
                    }
                    Interlocked.Decrement(ref chunksInFlight);
                    if (waitMs > 0) { await Task.Delay(waitMs); waitMs = 0; }
                }
            }
            DisposeQuietly(connection);
            worker.Socket = null;
            lock (workersPerLink) { int count; if (workersPerLink.TryGetValue(link, out count) && count > 0 && !retired) workersPerLink[link] = count - 1; }
        }

        // Conexão keep-alive com o servidor, saindo pelo IP do link (TLS se for https).
        static async Task<Stream> Connect(Link link, Job job, Worker worker)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            worker.Socket = socket;
            Interlocked.Exchange(ref worker.LastActivity, Stopwatch.GetTimestamp());
            try
            {
                socket.Bind(new IPEndPoint(link.Address, 0));
                socket.ReceiveBufferSize = 1 << 20;
                await socket.ConnectWithTimeout(new IPEndPoint(job.Server, job.Uri.Port), ConnectTimeoutMs);
            }
            catch { socket.CloseQuietly(); throw; }
            Stream stream = new NetworkStream(socket, true);
            if (job.Uri.Scheme == Uri.UriSchemeHttps)
            {
                var tls = new SslStream(stream, false);
                await tls.AuthenticateAsClientAsync(job.Uri.DnsSafeHost, null, SslProtocols.Tls12, false);
                stream = tls;
            }
            return stream;
        }

        // Baixa um pedaço e grava na posição dele no arquivo. Devolve true se o servidor vai fechar a conexão.
        async Task<bool> DownloadChunk(Stream connection, Link link, Job job, long offset, FileStream file, byte[] buffer, Worker worker)
        {
            long last = Math.Min(offset + job.ChunkBytes, job.Total) - 1;
            await connection.WriteAllAsync(Encoding.ASCII.GetBytes(
                "GET " + job.Uri.PathAndQuery + " HTTP/1.1\r\nHost: " + job.Uri.Authority +
                "\r\nUser-Agent: " + UserAgent + "\r\nAccept: */*\r\nAccept-Encoding: identity\r\nRange: bytes=" + offset + "-" + last +
                "\r\nConnection: keep-alive\r\n\r\n"));

            var response = ChunkResponse.Parse(await connection.ReadHttpHeaderAsync(MaxHeaderBytes));
            if (response.StatusCode == "429" || response.StatusCode == "503") throw new ThrottledException(response.StatusLine, response.RetryAfterSeconds);
            if (response.StatusCode != "206") throw new IOException("resposta inesperada: " + response.StatusLine);
            if (response.ContentLength != last - offset + 1) throw new IOException("tamanho do pedaço inesperado");

            long received = 0;
            try
            {
                file.Seek(offset, SeekOrigin.Begin);
                while (received < response.ContentLength)
                {
                    int read = await connection.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, response.ContentLength - received));
                    if (read <= 0) throw new IOException("conexão encerrada");
                    await file.WriteAsync(buffer, 0, read);
                    received += read;
                    Interlocked.Add(ref Done, read);
                    Interlocked.Add(ref link.Traffic.BytesDown, read);
                    BytesPerLink.AddOrUpdate(link, read, (key, total) => total + read);
                    Interlocked.Exchange(ref worker.LastActivity, Stopwatch.GetTimestamp());
                }
            }
            catch { Interlocked.Add(ref Done, -received); throw; } // o pedaço volta inteiro para a fila
            return response.ConnectionClose;
        }

        class ChunkResponse
        {
            public string StatusLine, StatusCode;
            public long ContentLength = -1;
            public bool ConnectionClose;
            public int RetryAfterSeconds;

            public static ChunkResponse Parse(string header)
            {
                var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var statusParts = lines[0].Split(' ');
                var response = new ChunkResponse { StatusLine = lines[0], StatusCode = statusParts.Length >= 2 ? statusParts[1] : "" };
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = line.Substring(0, colon).Trim().ToLowerInvariant();
                    var value = line.Substring(colon + 1).Trim();
                    if (name == "content-length") long.TryParse(value, out response.ContentLength);
                    else if (name == "connection" && value.Equals("close", StringComparison.OrdinalIgnoreCase)) response.ConnectionClose = true;
                    else if (name == "retry-after") int.TryParse(value, out response.RetryAfterSeconds);
                }
                return response;
            }
        }

        int WorkerCount(Link link) { lock (workersPerLink) { int count; workersPerLink.TryGetValue(link, out count); return count; } }

        // Tira um worker do link, desde que sobre ao menos um.
        bool TryRetireWorker(Link link)
        {
            lock (workersPerLink)
            {
                int count;
                workersPerLink.TryGetValue(link, out count);
                if (count <= 1) return false;
                workersPerLink[link] = count - 1;
                return true;
            }
        }

        static void DisposeQuietly(Stream stream)
        {
            if (stream == null) return;
            try { stream.Dispose(); } catch { }
        }

        // Servidor sem suporte a Range: baixa normalmente por uma conexão só.
        public async Task RunSingle(Uri uri, string path, CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = UserAgent;
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var body = response.GetResponseStream())
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                Total = response.ContentLength;
                var buffer = new byte[65536];
                int read;
                while ((read = await body.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await file.WriteAsync(buffer, 0, read);
                    Interlocked.Add(ref Done, read);
                }
            }
        }
    }
}
