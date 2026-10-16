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

        public static string Of(Link link)
        {
            if (!link.Present) return link.AdapterState ?? Unplugged;
            if (!link.InternetUp) return link.GatewayReachable ? NoInternet : GatewayDown;
            return Ok;
        }

        public static bool IsDown(string health) { return health != null && health != Ok; }

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
                default: return "online";
            }
        }
    }
}
