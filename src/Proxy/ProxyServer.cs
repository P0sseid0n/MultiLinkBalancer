// Proxy local SOCKS4/SOCKS5/HTTP numa única porta. O primeiro byte de cada conexão identifica o
// protocolo; depois do pedido, o destino é aberto pelo link que o Balancer escolher e os bytes
// são copiados nos dois sentidos.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Proxy
{
    public class ProxyServer
    {
        const int Backlog = 1024;
        const int HalfCloseGraceMs = 30000; // depois que um lado fecha, espera o outro terminar por até 30 s

        readonly Balancer balancer;
        readonly BalancedConnector connector;
        readonly UdpPorts udpPorts = new UdpPorts();
        readonly UdpBurstLog udpBursts;
        readonly ConcurrentDictionary<Socket, byte> openSockets = new ConcurrentDictionary<Socket, byte>();
        TcpListener listener;
        CancellationTokenSource stopping;

        public ProxyServer(Balancer balancer, ActivityLog log)
        {
            this.balancer = balancer;
            connector = new BalancedConnector(balancer, log);
            udpBursts = new UdpBurstLog(log);
        }

        public bool Running { get { return listener != null; } }

        public void Start(int port, bool allowLan)
        {
            var started = new TcpListener(allowLan ? IPAddress.Any : IPAddress.Loopback, port);
            started.Start(Backlog);
            listener = started;
            stopping = new CancellationTokenSource();
            var token = stopping.Token;
            Task.Run(() => AcceptLoop(started, token));
        }

        public void Stop()
        {
            if (listener == null) return;
            stopping.Cancel();
            try { listener.Stop(); } catch { }
            listener = null;
            foreach (var socket in openSockets.Keys.ToList()) Close(socket);
        }

        async Task AcceptLoop(TcpListener acceptor, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await acceptor.AcceptTcpClientAsync(); }
                catch { if (token.IsCancellationRequested) break; continue; }
                var ignored = HandleClient(client.Client);
            }
        }

        async Task HandleClient(Socket clientSocket)
        {
            Socket remoteSocket = null;
            openSockets[clientSocket] = 0;
            try
            {
                clientSocket.NoDelay = true;
                var client = new NetworkStream(clientSocket, false);
                var request = await ReadRequest(client);
                if (request == null) return;
                if (request.IsUdpAssociate) { await new UdpAssociation(balancer, udpPorts, udpBursts, client, clientSocket).Run(); return; }

                var outbound = await connector.Connect(request.Host, request.Port);
                if (outbound == null) { await request.ReplyFailure(); return; }
                remoteSocket = outbound.Socket;
                openSockets[remoteSocket] = 0;
                await Relay(client, clientSocket, request, outbound);
            }
            catch { }
            finally
            {
                Close(remoteSocket);
                Close(clientSocket);
            }
        }

        static async Task<ProxyRequest> ReadRequest(NetworkStream client)
        {
            var first = new byte[1];
            if (!await client.ReadExactAsync(first, 1)) return null;
            if (first[0] == 5) return await Socks5Handshake.Read(client);
            if (first[0] == 4) return await Socks4Handshake.Read(client);
            return await HttpProxyHandshake.Read(client, first[0]);
        }

        static async Task Relay(NetworkStream client, Socket clientSocket, ProxyRequest request, OutboundConnection outbound)
        {
            var link = outbound.Link;
            var remote = new NetworkStream(outbound.Socket, false);
            await request.ReplySuccess();
            if (request.InitialData != null && request.InitialData.Length > 0)
            {
                await remote.WriteAllAsync(request.InitialData);
                Interlocked.Add(ref link.Traffic.BytesUp, request.InitialData.Length);
            }

            Interlocked.Increment(ref link.Traffic.ActiveConnections);
            try
            {
                var upload = Copy(client, remote, outbound.Socket, bytes => Interlocked.Add(ref link.Traffic.BytesUp, bytes));
                var download = Copy(remote, client, clientSocket, bytes => Interlocked.Add(ref link.Traffic.BytesDown, bytes));
                await Task.WhenAny(upload, download);
                await Task.WhenAny(Task.WhenAll(upload, download), Task.Delay(HalfCloseGraceMs));
            }
            finally { Interlocked.Decrement(ref link.Traffic.ActiveConnections); }
        }

        // Copia até a origem fechar e então fecha o envio do destino (meio-fechamento do TCP).
        static async Task Copy(Stream source, Stream destination, Socket destinationSocket, Action<int> countBytes)
        {
            var buffer = BufferPool.Stream.Rent();
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, read);
                    countBytes(read);
                }
            }
            catch { }
            BufferPool.Stream.Return(buffer);
            try { destinationSocket.Shutdown(SocketShutdown.Send); } catch { }
        }

        void Close(Socket socket)
        {
            if (socket == null) return;
            byte ignored;
            openSockets.TryRemove(socket, out ignored);
            socket.CloseQuietly();
        }
    }
}
