// Diagnóstico de portas TCP efêmeras. Quando elas acabam, nenhum programa consegue abrir conexões novas
// (o Windows registra o evento Tcpip 4231/4227, sem dizer quem as consumiu). Este monitor registra quem
// está usando as portas: quando o uso passa da metade e quando o próprio Windows avisa que acabaram.
// O resumo vai para o registro de eventos; o detalhe vai para %LOCALAPPDATA%\MultiLinkBalancer\portas.log.
// Conexões em TIME_WAIT já não pertencem a nenhum processo: essas são agrupadas pelo destino.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Storage;

namespace MultiLink.Monitoring
{
    public class PortUsageMonitor
    {
        // faixa padrão do Windows (netsh int ipv4 show dynamicport tcp)
        const int FirstEphemeralPort = 49152, EphemeralPorts = 16384;
        const int AlertThreshold = EphemeralPorts / 2;
        static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
        static readonly TimeSpan MinReportInterval = TimeSpan.FromMinutes(5);
        const long MaxFileBytes = 1 << 20;

        readonly ActivityLog log;
        readonly string file = Path.Combine(AppPaths.Local, "portas.log");
        EventLogWatcher watcher;
        DateTime lastCheck = DateTime.UtcNow, lastReport = DateTime.MinValue;
        int busy;

        public PortUsageMonitor(ActivityLog log) { this.log = log; }

        public void Start()
        {
            try
            {
                var query = new EventLogQuery("System", PathType.LogName,
                    "*[System[Provider[@Name='Microsoft-Windows-Tcpip'] and (EventID=4231 or EventID=4227)]]");
                watcher = new EventLogWatcher(query);
                watcher.EventRecordWritten += (sender, e) => { if (e.EventRecord != null) Check("o Windows avisou que as portas TCP acabaram (evento " + e.EventRecord.Id + ")"); };
                watcher.Enabled = true;
            }
            catch (Exception ex) { log.Add("O aviso de portas TCP esgotadas não vai ser acompanhado: " + ex.Message, ActivityLog.Error); }
        }

        public void Stop()
        {
            if (watcher == null) return;
            try { watcher.Enabled = false; watcher.Dispose(); } catch { }
            watcher = null;
        }

        // Chamado a cada segundo; só olha a tabela de conexões a cada 5 s.
        public void Tick()
        {
            var now = DateTime.UtcNow;
            if (now - lastCheck < CheckInterval) return;
            lastCheck = now;
            Check(null);
        }

        // reason == null: verificação periódica, só registra acima do limite (e no máximo a cada 5 min).
        // O aviso do Windows sempre registra, mesmo com uma verificação periódica em andamento.
        void Check(string reason)
        {
            bool periodic = reason == null;
            if (periodic && Interlocked.Exchange(ref busy, 1) == 1) return;
            Task.Run(() =>
            {
                try
                {
                    var connections = TcpTable.Read();
                    var ephemeral = connections.Where(c => c.LocalPort >= FirstEphemeralPort).ToList();
                    if (periodic)
                    {
                        if (ephemeral.Count < AlertThreshold || DateTime.UtcNow - lastReport < MinReportInterval) return;
                        reason = "uso alto de portas TCP";
                    }
                    lastReport = DateTime.UtcNow;
                    Report(reason, ephemeral);
                }
                catch (Exception ex) { Debug.WriteLine("PortUsageMonitor: " + ex.Message); }
                finally { if (periodic) Interlocked.Exchange(ref busy, 0); }
            });
        }

        void Report(string reason, List<TcpTable.Connection> ephemeral)
        {
            var timeWait = ephemeral.Where(c => c.State == TcpTable.TimeWait).ToList();
            var byProcess = ephemeral.Where(c => c.State != TcpTable.TimeWait)
                .GroupBy(c => c.Pid).Select(g => new { Name = ProcessName(g.Key), Count = g.Count() })
                .GroupBy(p => p.Name).Select(g => new { Name = g.Key, Count = g.Sum(p => p.Count) })
                .OrderByDescending(p => p.Count).ToList();
            var byDestination = timeWait.GroupBy(c => c.Remote).Select(g => new { Remote = g.Key, Count = g.Count() })
                .OrderByDescending(d => d.Count).ToList();

            log.Add("Portas TCP: " + reason + ". Em uso: " + ephemeral.Count + " de " + EphemeralPorts
                + (byProcess.Count > 0 ? "; mais conexões: " + string.Join(", ", byProcess.Take(3).Select(p => p.Name + " (" + p.Count + ")")) : "")
                + "; em TIME_WAIT: " + timeWait.Count + (byDestination.Count > 0 ? " (mais para " + byDestination[0].Remote + ")" : "")
                + ". Detalhes em portas.log.", ActivityLog.Error);

            var text = new StringBuilder();
            text.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + reason);
            text.AppendLine("  em uso: " + ephemeral.Count + " de " + EphemeralPorts + " (TIME_WAIT: " + timeWait.Count + ")");
            text.AppendLine("  por processo (sem TIME_WAIT):");
            foreach (var p in byProcess.Take(15)) text.AppendLine("    " + p.Count.ToString().PadLeft(6) + "  " + p.Name);
            text.AppendLine("  TIME_WAIT por destino:");
            foreach (var d in byDestination.Take(15)) text.AppendLine("    " + d.Count.ToString().PadLeft(6) + "  " + d.Remote);
            text.AppendLine();
            try
            {
                Directory.CreateDirectory(AppPaths.Local);
                if (File.Exists(file) && new FileInfo(file).Length > MaxFileBytes) { File.Copy(file, file + ".old", true); File.Delete(file); }
                File.AppendAllText(file, text.ToString());
            }
            catch { }
        }

        static string ProcessName(int pid)
        {
            if (pid == 0) return "(sistema)";
            try { using (var process = Process.GetProcessById(pid)) return process.ProcessName; }
            catch { return "pid " + pid; }
        }

        // GetExtendedTcpTable (IPv4 e IPv6) com o processo dono de cada conexão.
        static class TcpTable
        {
            public const int TimeWait = 11; // MIB_TCP_STATE_TIME_WAIT
            const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5;

            public struct Connection
            {
                public int State, LocalPort, Pid;
                public string Remote;
            }

            [DllImport("iphlpapi.dll", SetLastError = true)]
            static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, int reserved);

            public static List<Connection> Read()
            {
                var result = new List<Connection>();
                ReadFamily(AF_INET, result);
                ReadFamily(AF_INET6, result);
                return result;
            }

            static void ReadFamily(int family, List<Connection> result)
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    size += 4096; // a tabela pode crescer entre as duas chamadas
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        int error = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
                        if (error == 122) continue; // ERROR_INSUFFICIENT_BUFFER
                        if (error != 0) throw new Win32Exception(error);
                        int count = Marshal.ReadInt32(buffer);
                        if (family == AF_INET) ParseV4(buffer, count, result);
                        else ParseV6(buffer, count, result);
                        return;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }

            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (24 bytes)
            static void ParseV4(IntPtr buffer, int count, List<Connection> result)
            {
                for (int i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * 24;
                    var remote = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 12)));
                    result.Add(new Connection
                    {
                        State = Marshal.ReadInt32(row, 0), LocalPort = Port(Marshal.ReadInt32(row, 8)),
                        Remote = remote + ":" + Port(Marshal.ReadInt32(row, 16)), Pid = Marshal.ReadInt32(row, 20)
                    });
                }
            }

            // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid (56 bytes)
            static void ParseV6(IntPtr buffer, int count, List<Connection> result)
            {
                for (int i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * 56;
                    var address = new byte[16];
                    Marshal.Copy(row + 24, address, 0, 16);
                    result.Add(new Connection
                    {
                        LocalPort = Port(Marshal.ReadInt32(row, 20)), Remote = "[" + new IPAddress(address) + "]:" + Port(Marshal.ReadInt32(row, 44)),
                        State = Marshal.ReadInt32(row, 48), Pid = Marshal.ReadInt32(row, 52)
                    });
                }
            }

            // a porta vem em ordem de rede nos 16 bits baixos
            static int Port(int value) { return ((value & 0xFF) << 8) | ((value >> 8) & 0xFF); }
        }
    }
}
