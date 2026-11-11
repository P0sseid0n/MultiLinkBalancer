using System;

namespace MultiLink.Links
{
    // Estado de um link, do ponto de vista do usuário. Os códigos vão para a interface e para o histórico.
    public static class LinkHealth
    {
        public const string Ok = "ok";
        public const string NoInternet = "noinet";     // o modem responde, a internet não
        public const string GatewayDown = "nogw";      // nem o modem responde
        public const string Unplugged = "unplugged";
        public const string Disabled = "disabled";     // adaptador desativado no Windows
        public const string NoConfig = "nocfg";        // adaptador no ar, mas sem IP ou gateway
        public const string Connecting = "connecting"; // adaptador acabou de ser ativado: ainda pegando IP e internet

        public static string Of(Link link)
        {
            var health = !link.Present ? link.AdapterState ?? Unplugged
                       : !link.InternetUp ? (link.GatewayReachable ? NoInternet : GatewayDown)
                       : Ok;
            if (health != Ok && health != Disabled && DateTime.UtcNow < link.ConnectingUntil) return Connecting;
            return health;
        }

        // Conectando não é queda: só vira queda se o prazo acabar sem internet (ver LinkMonitor).
        public static bool IsDown(string health) { return health != null && health != Ok && health != Connecting; }

        public static string ShortText(string health)
        {
            switch (health)
            {
                case Ok: return "Online";
                case NoInternet: return "Sem internet (operadora)";
                case GatewayDown: return "Modem não responde";
                case Unplugged: return "Cabo desconectado";
                case Disabled: return "Desativado no Windows";
                case NoConfig: return "Sem IP/gateway";
                case Connecting: return "Conectando";
                default: return health;
            }
        }

        public static string Describe(string health)
        {
            switch (health)
            {
                case NoInternet: return "sem internet. O modem responde, então o problema é na operadora";
                case GatewayDown: return "o modem/roteador não responde (desligado, travado ou cabo com defeito)";
                case Unplugged: return "cabo desconectado";
                case Disabled: return "adaptador desativado no Windows";
                case NoConfig: return "sem IP ou gateway (o modem não entregou endereço)";
                case Connecting: return "conectando: o adaptador acabou de ser ativado";
                default: return "online";
            }
        }
    }
}
