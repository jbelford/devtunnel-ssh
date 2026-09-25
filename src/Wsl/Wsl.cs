using System.Text;

namespace Dtssh.Wsl;

// Detects the Windows Subsystem for Linux. Inside WSL, the hostname resolves to
// the Windows computer name, so a WSL host and its Windows host would derive the
// same dtssh alias/tunnel name; AliasSuffix() disambiguates them with
// "wsl-<distro>". (Windows-Startup boot integration is handled by the service
// command and is not needed for hosting itself.)
internal static class Wsl
{
    public static bool IsWsl()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP")))
            return true;
        foreach (var p in new[] { "/proc/sys/kernel/osrelease", "/proc/version" })
        {
            try
            {
                var s = File.ReadAllText(p).ToLowerInvariant();
                if (s.Contains("microsoft") || s.Contains("wsl")) return true;
            }
            catch { /* not present */ }
        }
        return false;
    }

    // The WSL distro name (e.g. "Ubuntu"), falling back to /etc/os-release ID.
    public static string Distro()
    {
        var env = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        if (!string.IsNullOrEmpty(env)) return env;
        try
        {
            foreach (var line in File.ReadAllLines("/etc/os-release"))
                if (line.StartsWith("ID=", StringComparison.Ordinal))
                    return line[3..].Trim('"');
        }
        catch { /* not present */ }
        return "wsl";
    }

    // The alias/tunnel-name qualifier distinguishing this WSL distro from its
    // Windows host, e.g. "wsl-ubuntu". Empty when not running under WSL.
    public static string AliasSuffix() => IsWsl() ? "wsl-" + Sanitize(Distro()) : "";

    // The file dropped in the Windows Startup folder to auto-boot the distro.
    public const string BootScriptName = "dtssh-wsl-boot.vbs";

    // Resolves the current user's Windows Startup folder as a WSL path via
    // cmd.exe/wslpath interop.
    public static string StartupDir()
    {
        var appdata = WinEnv("APPDATA");
        var unix = WslPath(appdata);
        return Path.Combine(unix, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
    }

    // Writes a hidden launcher into the Windows Startup folder so the given distro
    // boots at Windows logon (which, with systemd + lingering, starts the dtssh
    // service). Returns the path written.
    public static string InstallBoot(string distro)
    {
        var dir = StartupDir();
        if (!Directory.Exists(dir))
            throw new Auth.DtsshException($"windows Startup folder not found ({dir})");
        // WScript.Shell.Run with window style 0 launches wsl.exe hidden and does
        // not wait, so logon is not blocked.
        var script = $"CreateObject(\"WScript.Shell\").Run \"wsl.exe -d {VbsEscape(distro)} -e /bin/true\", 0, False\r\n";
        var path = Path.Combine(dir, BootScriptName);
        File.WriteAllText(path, script);
        return path;
    }

    // Removes the Startup-folder launcher if present.
    public static void UninstallBoot()
    {
        var path = Path.Combine(StartupDir(), BootScriptName);
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception e) { throw new Auth.DtsshException(e.Message); }
    }

    // Reports whether the Startup launcher exists.
    public static bool BootInstalled()
    {
        try { return File.Exists(Path.Combine(StartupDir(), BootScriptName)); }
        catch { return false; }
    }

    private static string WinEnv(string name)
    {
        var r = Infra.Proc.RunAsync("cmd.exe", new[] { "/c", "echo %" + name + "%" }).GetAwaiter().GetResult();
        var v = r.Stdout.TrimEnd('\r', '\n', ' ');
        if (v.Length == 0 || v == "%" + name + "%")
            throw new Auth.DtsshException($"windows env %{name}% is empty");
        return v;
    }

    private static string WslPath(string winPath)
    {
        var r = Infra.Proc.RunAsync("wslpath", new[] { "-u", winPath }).GetAwaiter().GetResult();
        if (!r.Ok) throw new Auth.DtsshException($"wslpath -u {winPath}: {r.StderrTrim}");
        return r.Stdout.TrimEnd('\r', '\n');
    }

    private static string VbsEscape(string s) => s.Replace("\"", "\"\"");

    private static string Sanitize(string s)
    {
        s = s.ToLowerInvariant();
        var b = new StringBuilder();
        foreach (var r in s)
        {
            if ((r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '-') b.Append(r);
            else if (r is '.' or '_' or ' ') b.Append('-');
        }
        var outp = b.ToString().Trim('-');
        return outp.Length == 0 ? "wsl" : outp;
    }
}
