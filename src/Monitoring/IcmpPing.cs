// Ping saindo por um link específico. O Ping do .NET não escolhe o endereço de origem;
// o IcmpSendEcho2Ex escolhe, então cada link é medido pelo seu próprio caminho.
using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiLink.Monitoring
{
    public static class IcmpPing
    {
        [DllImport("iphlpapi.dll", SetLastError = true)] static extern IntPtr IcmpCreateFile();
        [DllImport("iphlpapi.dll")] static extern bool IcmpCloseHandle(IntPtr handle);
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint IcmpSendEcho2Ex(IntPtr handle, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext, uint source, uint destination,
            byte[] requestData, ushort requestSize, IntPtr options, byte[] replyBuffer, uint replySize, uint timeout);

        static readonly byte[] Payload = Encoding.ASCII.GetBytes("MultiLinkBalancer-qualidade-1234");

        // Tempo de resposta em ms, ou -1 se não respondeu.
        public static int Echo(IPAddress source, IPAddress destination, int timeoutMs)
        {
            var handle = IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return -1;
            try
            {
                var reply = new byte[128 + Payload.Length];
                uint replies = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    BitConverter.ToUInt32(source.GetAddressBytes(), 0), BitConverter.ToUInt32(destination.GetAddressBytes(), 0),
                    Payload, (ushort)Payload.Length, IntPtr.Zero, reply, (uint)reply.Length, (uint)timeoutMs);
                // ICMP_ECHO_REPLY: Address (4 bytes), Status (4), RoundTripTime (4)...
                const int StatusOffset = 4, RoundTripOffset = 8;
                if (replies == 0 || BitConverter.ToUInt32(reply, StatusOffset) != 0) return -1;
                return (int)BitConverter.ToUInt32(reply, RoundTripOffset);
            }
            catch { return -1; }
            finally { IcmpCloseHandle(handle); }
        }
    }
}
