// Comandos vindos da interface (e do menu da bandeja). A lista de comandos é o contrato com
// ui/src: qualquer comando novo também precisa existir no simulador (ui/src/api/mock.js).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using MultiLink.Links;
using MultiLink.Platform;
using MultiLink.Storage;

namespace MultiLink.App
{
    public partial class AppController
    {
        public async Task<object> Handle(string command, Dictionary<string, object> args)
        {
            switch (command)
            {
                case "proxy.start":
                    TurnOn();
                    if (!Proxy.Running) throw new Exception("Não foi possível abrir a porta " + Settings.Port + ". Outro programa pode estar usando.");
                    return null;
                case "proxy.stop": TurnOff(); return null;
                case "settings.set": return await SetSetting(Text(args, "key"), args.ContainsKey("value") ? args["value"] : null);

                case "link.set":
                    Links.SetPreferences(Text(args, "id"),
                        args.ContainsKey("enabled") ? Convert.ToBoolean(args["enabled"]) : (bool?)null,
                        args.ContainsKey("weight") ? Convert.ToInt32(args["weight"]) : (int?)null);
                    return null;
                case "link.toggleAdapter": return new Dictionary<string, object> { { "enabled", await Links.ToggleAdapter(Text(args, "id")) } };
                case "links.test": Links.CheckAllNow(); return null;
                case "adapters.refresh": Links.Refresh(); return null;
                case "measure": await Links.MeasureAndSetWeights(); return null;

                case "download.start": await Downloads.Start(Text(args, "url"), Links.Usable(), host.PickSavePath, NotifyIfEnabled); return null;
                case "download.cancel": Downloads.Cancel(); return null;
                case "download.reveal": Downloads.Reveal(); return null;
                case "download.clear": Downloads.Clear(); return null;

                case "history.get":
                    int minutes = args.ContainsKey("minutes") ? Convert.ToInt32(args["minutes"]) : 60;
                    return History.Get(Math.Max(10, Math.Min(1440, minutes)), Links.All());

                case "update.check": await CheckUpdate(); return null;
                case "update.install":
                    if (DevBuild.Available) throw new Exception("No modo de desenvolvimento a atualização trocaria o build local pelo da release. Use Recompilar e reiniciar.");
                    await Updater.Install(Log, host.Exit);
                    return null;

                case "dev.rebuild": DevBuild.Start(); return null;
                case "dev.openFolder": Process.Start("explorer.exe", "\"" + (Text(args, "folder") == "settings" ? AppPaths.Roaming : AppPaths.Local) + "\""); return null;

                case "app.restartElevated": Elevation.StartElevatedInstance(); host.Exit(); return null;
                case "app.openNetworkConnections": Process.Start("ncpa.cpl"); return null;
                case "app.exit": host.Exit(); return null;
                default: throw new Exception("Comando desconhecido: " + command);
            }
        }

        static string Text(Dictionary<string, object> args, string key)
        {
            object value;
            return args != null && args.TryGetValue(key, out value) && value != null ? value.ToString() : null;
        }

        // Devolve { needsAdmin } nos ajustes que só funcionam como administrador.
        async Task<object> SetSetting(string key, object value)
        {
            switch (key)
            {
                case "port":
                    if (Proxy.Running) throw new Exception("Pare o proxy antes de mudar a porta.");
                    int port = Convert.ToInt32(value);
                    if (port < 1 || port > 65535) throw new Exception("A porta deve estar entre 1 e 65535.");
                    Settings.Port = port;
                    break;
                case "mode":
                    Balancer.Mode = (BalanceMode)Math.Max(0, Math.Min(3, Convert.ToInt32(value)));
                    Settings.BalanceMode = (int)Balancer.Mode;
                    break;
                case "affinity": Settings.Affinity = Balancer.Affinity = Convert.ToBoolean(value); break;
                case "quality": Settings.QualityAware = Balancer.QualityAware = Convert.ToBoolean(value); break;
                case "lan":
                    Settings.AllowLan = Convert.ToBoolean(value);
                    if (Proxy.Running) { Proxy.Stop(); StartProxy(); } // reabre a porta na interface certa
                    break;
                case "system":
                    // preferência: com o MultiLink ligado, o Windows passa pelo proxy
                    Settings.UseSystemProxy = Convert.ToBoolean(value);
                    if (Proxy.Running) SetSystemProxy(Settings.UseSystemProxy);
                    break;
                case "startup": await SetStartup(Convert.ToBoolean(value)); break;
                case "notify": Settings.Notify = Convert.ToBoolean(value); break;
                case "notifySound": Settings.NotifySound = Convert.ToBoolean(value); break;
                case "autoUpdate": Settings.AutoUpdate = Convert.ToBoolean(value); break;
                case "hotkeys":
                    Settings.Hotkeys = Convert.ToBoolean(value);
                    host.SetHotkeys(Settings.Hotkeys);
                    break;
                case "capture": return SetCapture(Convert.ToBoolean(value));
                case "autoRoute": return SetAutoRoute(Convert.ToBoolean(value));
                default: throw new Exception("Ajuste desconhecido: " + key);
            }
            Settings.Save();
            return null;
        }

        object SetCapture(bool enabled)
        {
            Settings.CaptureAllPrograms = enabled;
            Settings.Save();
            if (!enabled) Capture.Stop();
            else if (IsAdmin && Proxy.Running) StartCapture();
            return NeedsAdmin(enabled && !IsAdmin);
        }

        object SetAutoRoute(bool enabled)
        {
            Settings.AutoRoute = enabled;
            RouteFailover.Enabled = enabled;
            if (!enabled && IsAdmin) RouteFailover.RestoreAll(Links.All());
            Settings.Save();
            return NeedsAdmin(enabled && !IsAdmin);
        }

        static object NeedsAdmin(bool needsAdmin) { return new Dictionary<string, object> { { "needsAdmin", needsAdmin } }; }
    }
}
