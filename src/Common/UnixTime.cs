using System;

namespace MultiLink.Common
{
    // Datas em milissegundos desde 1970 (UTC), o formato do Date do JavaScript.
    public static class UnixTime
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowMs() { return ToMs(DateTime.UtcNow); }

        public static long ToMs(DateTime time) { return (long)(time.ToUniversalTime() - Epoch).TotalMilliseconds; }
    }
}
