using System.Diagnostics;
using Dtssh.Auth;
using Dtssh.Commands;
using Dtssh.Infra;
using Dtssh.Ssh;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Reject(Action action, string message)
{
    try { action(); }
    catch (DtsshException) { return; }
    throw new Exception(message);
}

var defaultFlags = Flags.Parse([], "allow-wsl-root");
var enabledFlags = Flags.Parse(["--allow-wsl-root"], "allow-wsl-root");
Check(!HostCommand.ValidateRootFlag(defaultFlags, "root", false, true, true), "default root login must be disabled");
Check(HostCommand.ValidateRootFlag(enabledFlags, "root", false, true, true), "WSL root opt-in must be accepted");
Reject(() => HostCommand.ValidateRootFlag(enabledFlags, "root", false, false, true), "non-WSL accepted");
Reject(() => HostCommand.ValidateRootFlag(enabledFlags, "root", false, true, false), "non-root process accepted");
Reject(() => HostCommand.ValidateRootFlag(enabledFlags, "other", false, true, true), "non-root SSH user accepted");
Reject(() => HostCommand.ValidateRootFlag(enabledFlags, "root", true, true, true), "system sshd accepted");
Reject(() => HostCommand.ValidateRootFlag(Flags.Parse(["--allow-wsl-root=unexpected"], "allow-wsl-root"),
    "root", false, true, true), "invalid boolean accepted");
Reject(() => HostCommand.ValidateRootFlag(Flags.Parse(["--allow-wsl-root", "unexpected"], "allow-wsl-root"),
    "root", false, true, true), "stray root flag argument accepted");
Check(!HostCommand.ValidateRootFlag(Flags.Parse(["--allow-wsl-root=false"], "allow-wsl-root"),
    "root", false, false, false), "explicit false rejected");

var installedArgs = ServiceCommand.BuildHostArgs(Flags.Parse(
    ["--user", "root", "--port", "2345", "--allow-wsl-root"], "allow-wsl-root"), true);
Check(installedArgs.Contains("--allow-wsl-root") && installedArgs.Contains("root") &&
    installedArgs.Contains("2345"), "service did not forward host options");
Check(!ServiceCommand.BuildHostArgs(defaultFlags, false).Contains("--allow-wsl-root"),
    "service opted in by default");
Check(Flags.Parse(installedArgs.ToArray(), "allow-wsl-root").Bool("allow-wsl-root", false),
    "persisted host flags did not opt in");

var sshd = new Sshd
{
    Port = 2345,
    HostKey = "/tmp/hostkey",
    PidFile = "/tmp/sshd.pid",
    AuthKeys = "/tmp/authorized_keys",
    SftpServerPath = Environment.GetEnvironmentVariable("DTSSH_CHECK_SFTP") ?? "/usr/lib/openssh/sftp-server"
};
var normal = sshd.RenderConfig(false);
var optedIn = sshd.RenderConfig(true);
Check(normal.Contains("PermitRootLogin no\n"), "default config permits root");
Check(optedIn.Contains("PermitRootLogin prohibit-password\n"), "opt-in config denies root");
foreach (var config in new[] { normal, optedIn })
{
    Check(config.Contains("PubkeyAuthentication yes\n"), "missing public-key authentication");
    Check(config.Contains("PasswordAuthentication no\n"), "password authentication enabled");
    Check(config.Contains("KbdInteractiveAuthentication no\n"), "keyboard-interactive authentication enabled");
    Check(config.Contains("Subsystem sftp \"" + sshd.SftpServerPath + "\"\n"), "SFTP unavailable");
}

var sshdBinary = Environment.GetEnvironmentVariable("DTSSH_CHECK_SSHD");
if (!string.IsNullOrEmpty(sshdBinary))
{
    Check(File.Exists(sshd.SftpServerPath), "SFTP subsystem binary not found: " + sshd.SftpServerPath);
    var dir = Path.Combine(Path.GetTempPath(), "dtssh-root-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var key = Path.Combine(dir, "hostkey");
        var keygen = Process.Start(new ProcessStartInfo("ssh-keygen")
        {
            ArgumentList = { "-q", "-t", "ed25519", "-N", "", "-f", key },
            RedirectStandardError = true
        })!;
        keygen.WaitForExit();
        Check(keygen.ExitCode == 0, "ssh-keygen failed: " + keygen.StandardError.ReadToEnd());
        foreach (var (config, expected) in new[] { (normal, "no"), (optedIn, "without-password") })
        {
            var path = Path.Combine(dir, "sshd_config");
            File.WriteAllText(path, config.Replace("/tmp/hostkey", key, StringComparison.Ordinal));
            var p = Process.Start(new ProcessStartInfo(sshdBinary)
            {
                ArgumentList = { "-T", "-f", path, "-C", "user=root,host=localhost,addr=127.0.0.1" },
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Check(p.ExitCode == 0, "sshd -T failed: " + error);
            Check(output.Contains("permitrootlogin " + expected + "\n"),
                "effective root-login setting incorrect: " +
                output.Split('\n').FirstOrDefault(line => line.StartsWith("permitrootlogin ")));
            Check(output.Contains("passwordauthentication no\n"), "effective password auth enabled");
            Check(output.Contains("kbdinteractiveauthentication no\n"), "effective interactive auth enabled");
            Check(output.Contains("pubkeyauthentication yes\n"), "effective public-key auth disabled");
            Check(output.Contains("subsystem sftp "), "effective SFTP unavailable");
        }
    }
    finally { Directory.Delete(dir, recursive: true); }
}
Console.WriteLine("WSL root SSH regression checks passed" +
    (string.IsNullOrEmpty(sshdBinary) ? " (sshd -T not supplied)" : " (including sshd -T)"));
