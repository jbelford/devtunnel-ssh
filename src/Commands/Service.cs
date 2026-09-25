using Dtssh.Auth;
using Dtssh.Infra;
using Dtssh.Service;

namespace Dtssh.Commands;

// `dtssh service <install|uninstall|start|stop|restart|status|logs>` — register
// `dtssh host` as a per-user, auto-restarting background service so the machine
// stays reachable over its dev tunnel without a human keeping a terminal open.
// The host runs in --persist mode (stable identity + one reused tunnel across
// restarts). Ported from the Go internal/commands Service.
internal static class ServiceCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); Console.Error.WriteLine("service: missing subcommand"); return 2; }
        var sub = args[0];
        var rest = args[1..];

        if (sub == "install") return await InstallAsync(rest).ConfigureAwait(false);

        var m = ServiceManager.New();
        switch (sub)
        {
            case "uninstall" or "remove":
                m.Uninstall();
                Console.Error.WriteLine("dtssh: service uninstalled.");
                if (Wsl.Wsl.IsWsl())
                {
                    try { Wsl.Wsl.UninstallBoot(); Console.Error.WriteLine("dtssh: removed the Windows Startup launcher that booted this WSL distro."); }
                    catch (Exception e) { Console.Error.WriteLine($"dtssh: WARNING: could not remove WSL boot launcher: {e.Message}"); }
                }
                return 0;
            case "start": m.Start(); return 0;
            case "stop": m.Stop(); return 0;
            case "restart": try { m.Stop(); } catch { } m.Start(); return 0;
            case "status": m.Status(); return 0;
            case "logs": m.Logs(); return 0;
            case "help" or "-h" or "--help": PrintUsage(); return 0;
            default: PrintUsage(); Console.Error.WriteLine($"service: unknown subcommand \"{sub}\""); return 2;
        }
    }

    private static async Task<int> InstallAsync(string[] args)
    {
        var f = Flags.Parse(args, "system-sshd", "no-wsl-boot", "permit-root-login");
        if (args.Any(a => a is "-h" or "--help" or "help")) { PrintUsage(); return 0; }
        var port = f.Int("port", 2222);
        var loginUser = f.Str("user");
        var alias = f.Str("alias");
        var tunnelId = f.Str("tunnel");
        var expiration = f.Str("expiration");
        var systemSshd = f.Bool("system-sshd", false);
        var noWslBoot = f.Bool("no-wsl-boot", false);
        var ct = CancellationToken.None;

        var permitRootLogin = HostCommand.RootLoginEnabled(f,
            string.IsNullOrEmpty(loginUser) ? Cli.CurrentUser() : loginUser, systemSshd);
        Paths.EnsureAll();
        _ = await DevtunnelCli.EnsureBinaryAsync(ct).ConfigureAwait(false);

        // Reconstruct the `dtssh host` args. --persist reuses one tunnel + a stable
        // identity across restarts.
        var hostArgs = new List<string> { "--persist", "--port", port.ToString() };
        if (!string.IsNullOrEmpty(loginUser)) { hostArgs.Add("--user"); hostArgs.Add(loginUser); }
        if (!string.IsNullOrEmpty(alias)) { hostArgs.Add("--alias"); hostArgs.Add(alias); }
        if (!string.IsNullOrEmpty(tunnelId)) { hostArgs.Add("--tunnel"); hostArgs.Add(tunnelId); }
        if (!string.IsNullOrEmpty(expiration)) { hostArgs.Add("--expiration"); hostArgs.Add(expiration); }
        if (systemSshd) hostArgs.Add("--system-sshd");
        if (permitRootLogin) hostArgs.Add("--permit-root-login");

        var m = ServiceManager.New();
        var cfg = new ServiceConfig(Cli.SelfPath(), hostArgs, ServiceManager.DefaultEnv());
        m.Install(cfg);
        Console.Error.WriteLine($"dtssh: service installed and started ({m.UnitPath()}).");
        Console.Error.WriteLine("dtssh: it will auto-start at login and auto-restart on failure.");
        Console.Error.WriteLine("dtssh: check it with `dtssh service status` / `dtssh service logs`.");
        Console.Error.WriteLine("dtssh: NOTE: you must be logged into devtunnel (`dtssh login`) for the host to come up.");

        // A systemd *user* service inside WSL never starts after a Windows reboot
        // until the distro is launched. Register a hidden Windows Startup launcher.
        if (Wsl.Wsl.IsWsl() && !noWslBoot)
        {
            var distro = Wsl.Wsl.Distro();
            try
            {
                var path = Wsl.Wsl.InstallBoot(distro);
                Console.Error.WriteLine($"dtssh: WSL detected — registered Windows Startup launcher ({path}) to boot \"{distro}\" at logon.");
                Console.Error.WriteLine("dtssh: after a Windows restart, this distro (and the host service) will come up automatically.");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"dtssh: WARNING: could not register WSL auto-boot: {e.Message}");
                Console.Error.WriteLine("dtssh: the service still runs, but the distro won't boot on its own after a Windows restart.");
            }
        }
        return 0;
    }

    private static void PrintUsage() => Console.Error.Write(
"""
dtssh service — run the host as an auto-restarting user service

USAGE:
    dtssh service <subcommand> [options]

SUBCOMMANDS:
    install    Register and start the host service (same flags as `dtssh host`:
               --port, --user, --alias, --tunnel, --expiration, --system-sshd,
               --permit-root-login (set PermitRootLogin yes for Linux root).
               Inside WSL it also registers a hidden Windows Startup launcher so
               the distro (and this service) auto-boot at logon; opt out with
               --no-wsl-boot.
    uninstall  Stop and remove the service.
    start      Start the installed service.
    stop       Stop the running service.
    restart    Restart the service.
    status     Show the service status.
    logs       Show recent service logs.

The service starts at login and auto-restarts on failure (auto-recovery). It
hosts in --persist mode, reusing one tunnel and a stable identity across
restarts. You must be logged into devtunnel (`dtssh login`).

EXAMPLES:
    dtssh service install --port 2222
    dtssh service status
    dtssh service uninstall

""");
}
