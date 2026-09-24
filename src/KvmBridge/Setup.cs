using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace KvmBridge;

public static class Setup
{
    public const string ServiceName = "KvmBridge";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KvmBridge");
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KvmBridge");
    public const string Help = """
        KvmBridge 1.1 - TESmart HKS0802A1U LAN controller

        Windows x64. One executable; no separate .NET installation required.
        Connect Waveshare directly to the Windows host. RS232, NC, TX->RX, RX->TX, GND->GND.

        Run these commands in PowerShell opened as Administrator:
          .\KvmBridge.exe install --port COM5
          .\KvmBridge.exe client --output .\KvmBridge-Mac

        install copies itself to Program Files, creates an automatic Windows service,
        generates HTTPS credentials, and permits TCP 8443 from LocalSubnet on Private/Domain
        network profiles. It starts immediately and runs without a user login.
        Select your adapter explicitly with --port or --serial.
        Override if needed: install --port COM5
        Or use another adapter: install --serial YOURSERIAL
        Optional: --https-port 8443 --host 192.0.2.10

        Copy the generated KvmBridge-Mac folder to your Mac. It contains the CA certificate,
        an API-key configuration, and four select-N.command scripts plus status.command.
        Keep client.conf private. On the Mac run:
          chmod 700 /path/to/KvmBridge-Mac/*.command
          chmod 600 /path/to/KvmBridge-Mac/client.conf
        Run select-N.command for the desired computer, or build the silent Mac apps.

        Commands:
          install       Install/update and start service (Administrator)
          uninstall     Stop/delete service and its firewall rule; keep files/config
          status        Display Windows service status
          ports         List COM ports and detected adapter
          client        Export Mac scripts/certificate/API key (Administrator)
          run           Run in foreground using installed configuration
          source        Export complete source, README and project: --output DIRECTORY
          self-test     Run hardware-free controller tests
          help          Show this help

        Advanced development:
          init --data DIRECTORY --port PORT [--bind 127.0.0.1] [--https-port 8443]
          run --data DIRECTORY
          client --data DIRECTORY --output DIRECTORY [--host HOST_OR_IP]

        API (all routes require Authorization: Bearer API_KEY, over HTTPS):
          GET /health                  Service and serial health
          GET /api/kvm/status          Last observed channel and observation age
          GET /api/kvm/capabilities    Supported features and limitations
          PUT /api/kvm/selection       JSON: {"computer":1} (integers 1-4)

        Switch returns 200 only after a matching serial channel event; 504 on timeout,
        503 if disconnected, 429 if queue full. No automatic command retries.
        Selection starts unknown and becomes stale on disconnect/failed confirmation.
        No independent status query is known. An idle open serial port does not prove
        KVM power or reachability. Split-display routing and keyboard focus are not decoded.
        The service does not switch automatically at startup or on reconnection.

        Configuration and rotating serial logs: C:\ProgramData\KvmBridge
        Use a stable LAN IP/DHCP reservation. If the host address changes, export client
        scripts again for a name/IP already included in the certificate.
        Windows startup and COM access require validation on the target Windows machine.
        """;

    public static void Initialize(string data, Arguments cli)
    {
        Directory.CreateDirectory(data);
        if (File.Exists(Path.Combine(data, "config.json"))) throw new IOException("Configuration already exists; refusing to overwrite credentials.");
        var config = new Configuration
        {
            SerialPort = cli.Get("port"), UsbSerialNumber = cli.Get("serial") ?? "",
            BindAddress = cli.Get("bind") ?? "0.0.0.0",
            HttpsPort = int.Parse(cli.Get("https-port") ?? "8443"),
            ApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            CertificatePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            PreferredHost = ValidateHost(cli.Get("host") ?? BestHost())
        };
        config.Validate();
        CreateCertificates(data, config);
        File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(config, Configuration.Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(data, "config.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(Path.Combine(data, "server.pfx"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
    private static IEnumerable<IPAddress> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
    private static string BestHost()
    {
        var preferred = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        return preferred?.ToString() ?? Dns.GetHostName();
    }
    private static string ValidateHost(string host)
    {
        if (host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '/' or '\\'))
            throw new ArgumentException("--host must be a DNS hostname or IP address, without scheme or port.");
        return host;
    }
    private static void CreateCertificates(string data, Configuration config)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        using var rootKey = RSA.Create(3072);
        var rootRequest = new CertificateRequest("CN=KvmBridge Local CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var rootIdentifier = new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false);
        rootRequest.CertificateExtensions.Add(rootIdentifier);
        rootRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(rootIdentifier));
        using var root = rootRequest.CreateSelfSigned(start, start.AddYears(6));
        using var serverKey = RSA.Create(3072);
        var request = new CertificateRequest("CN=KvmBridge", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        var names = new SubjectAlternativeNameBuilder();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", Dns.GetHostName(), Environment.MachineName, Dns.GetHostName() + ".local", config.PreferredHost };
        foreach (var host in hosts)
        {
            if (IPAddress.TryParse(host, out var ip)) names.AddIpAddress(ip); else names.AddDnsName(host);
        }
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var ip in Addresses().Distinct()) names.AddIpAddress(ip);
        request.CertificateExtensions.Add(names.Build());
        var number = RandomNumberGenerator.GetBytes(16);
        number[0] &= 0x7f;
        using var publicServer = request.Create(root, start, start.AddYears(5), number);
        using var server = publicServer.CopyWithPrivateKey(serverKey);
        File.WriteAllBytes(Path.Combine(data, "server.pfx"), server.Export(X509ContentType.Pfx, config.CertificatePassword));
        File.WriteAllText(Path.Combine(data, "kvmbridge-ca.pem"), root.ExportCertificatePem() + "\n");
        // The CA private key is not persisted. Regenerating certificates requires redistributing the CA.
    }
    public static X509Certificate2 LoadCertificate(string data, Configuration config) =>
        X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(data, "server.pfx"), config.CertificatePassword,
            // Windows Schannel requires a backed key container, even when Kestrel
            // owns the certificate. MachineKeySet also avoids service-profile dependencies.
            // Omit PersistKeySet so disposing the certificate cleans up its imported key.
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.MachineKeySet :
            OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);

    public static void Install(Arguments cli)
    {
        RequireAdministrator();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(DataDirectory);
        LockDirectory(InstallDirectory, false);
        LockDirectory(DataDirectory, false);
        var logs = Path.Combine(DataDirectory, "logs");
        var runtime = Path.Combine(DataDirectory, "runtime");
        Directory.CreateDirectory(logs); Directory.CreateDirectory(runtime);
        LockDirectory(logs, true); LockDirectory(runtime, true);
        var configFile = Path.Combine(DataDirectory, "config.json");
        if (!File.Exists(configFile)) Initialize(DataDirectory, cli);
        var config = Configuration.Load(DataDirectory);
        if (cli.Get("host") is { } host && host != config.PreferredHost)
            throw new ArgumentException("For an existing installation, use 'client --host NAME_OR_IP'. The address must be covered by the existing certificate.");
        if (cli.Get("port") is { } port) config.SerialPort = port;
        if (cli.Get("serial") is { } serial) { config.UsbSerialNumber = serial; if (cli.Get("port") == null) config.SerialPort = null; }
        if (cli.Get("https-port") is { } httpsPort) config.HttpsPort = int.Parse(httpsPort);
        if (cli.Get("bind") is { } bind) config.BindAddress = bind;
        config.Validate();
        using (var certificate = LoadCertificate(DataDirectory, config))
            if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) throw new InvalidDataException("HTTPS certificate expired. See exported README for renewal.");
        var existed = ServiceExists();
        if (existed) StopService();
        File.WriteAllText(configFile, JsonSerializer.Serialize(config, Configuration.Json));
        var destination = Path.Combine(InstallDirectory, "KvmBridge.exe");
        var current = Environment.ProcessPath ?? throw new IOException("Cannot determine executable location.");
        if (!string.Equals(Path.GetFullPath(current), destination, StringComparison.OrdinalIgnoreCase))
        {
            // SCM may report Stopped slightly before the process releases its image.
            var copyDeadline = Environment.TickCount64 + 10_000;
            while (true)
            {
                try { File.Copy(current, destination, true); break; }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && Environment.TickCount64 < copyDeadline)
                { Thread.Sleep(250); }
            }
        }
        var binaryPath = "\"" + destination + "\" service --data \"" + DataDirectory + "\"";
        Run("sc.exe", [existed ? "config" : "create", ServiceName, "binPath=", binaryPath, "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "DisplayName=", "KVM Bridge"]);
        Run("sc.exe", ["description", ServiceName, "TESmart HKS0802A1U serial control and authenticated LAN HTTPS API"]);
        Run("sc.exe", ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"]);
        Run("sc.exe", ["failureflag", ServiceName, "1"]);
        using (var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName, true)
            ?? throw new IOException("Service registry key missing."))
            serviceKey.SetValue("Environment", new[] { "DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + runtime }, RegistryValueKind.MultiString);
        var firewall = "$ErrorActionPreference='Stop'; Get-NetFirewallRule -Name 'KvmBridge-HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
            "New-NetFirewallRule -Name 'KvmBridge-HTTPS' -DisplayName 'KVM Bridge HTTPS (local subnet)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort " + config.HttpsPort +
            " -RemoteAddress LocalSubnet -Profile Private,Domain -Program " + PsQuote(destination) + " | Out-Null";
        Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", firewall]);
        Run("sc.exe", ["start", ServiceName]);
        using (var service = new ServiceController(ServiceName)) service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        VerifyStarted(config);
        Console.WriteLine($"Installed and started. Automatic startup is enabled.\nURL: https://{UrlHost(config.PreferredHost)}:{config.HttpsPort}\nAdapter: {config.SerialPort ?? "auto-detect " + config.UsbSerialNumber}\nExport your Mac scripts next:\n  .\\KvmBridge.exe client --output .\\KvmBridge-Mac\nService logs: {logs}");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyStarted(Configuration config)
    {
        using var cert = LoadCertificate(DataDirectory, config);
        var expected = cert.GetCertHashString(HashAlgorithmName.SHA256);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.GetCertHashString(HashAlgorithmName.SHA256) == expected
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", config.ApiKey);
        var address = config.BindAddress == "0.0.0.0" ? "127.0.0.1" : config.BindAddress == "::" ? "::1" : config.BindAddress;
        Exception? last = null;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                using var result = client.GetAsync($"https://{UrlHost(address)}:{config.HttpsPort}/health").GetAwaiter().GetResult();
                result.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception ex) { last = ex; Thread.Sleep(500); }
        }
        throw new IOException("Service was installed but HTTPS did not become healthy. Check Windows Event Viewer and C:\\ProgramData\\KvmBridge\\logs. " + last?.Message);
    }
    public static void ExportClient(Arguments cli)
    {
        var data = Path.GetFullPath(cli.Get("data") ?? DataDirectory);
        var config = Configuration.Load(data);
        var output = Path.GetFullPath(cli.Get("output") ?? "KvmBridge-Mac");
        var host = ValidateHost(cli.Get("host") ?? config.PreferredHost);
        using var certificate = LoadCertificate(data, config);
        if (!certificate.MatchesHostname(host, allowWildcards: false, allowCommonName: false))
            throw new ArgumentException("The certificate does not cover this host/IP. Use a hostname or IP recorded at installation; see README for certificate renewal.");
        Directory.CreateDirectory(output);
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Cannot determine current user.");
            Run("icacls.exe", [output, "/inheritance:r", "/grant:r", "*" + sid + ":(OI)(CI)F", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"]);
        }
        else File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Copy(Path.Combine(data, "kvmbridge-ca.pem"), Path.Combine(output, "kvmbridge-ca.pem"), true);
        File.WriteAllText(Path.Combine(output, "client.conf"), "header = \"Authorization: Bearer " + config.ApiKey + "\"\n");
        var prefix = "#!/bin/sh\nset -eu\nSCRIPT_DIR=$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\n" +
            "exec /usr/bin/curl --silent --show-error --fail --connect-timeout 3 --max-time 10 \\\n  --cacert \"$SCRIPT_DIR/kvmbridge-ca.pem\" --config \"$SCRIPT_DIR/client.conf\" \\\n  ";
        var url = $"https://{UrlHost(host)}:{config.HttpsPort}";
        File.WriteAllText(Path.Combine(output, "server-url.txt"), url + "\n");
        for (var i = 1; i <= 4; i++)
            File.WriteAllText(Path.Combine(output, $"select-{i}.command"), prefix + "--request PUT --header 'Content-Type: application/json' \\\n  --data '{\"computer\":" + i + "}' '" + url + "/api/kvm/selection'\n");
        File.WriteAllText(Path.Combine(output, "status.command"), prefix + "'" + url + "/api/kvm/status'\n");
        File.WriteAllText(Path.Combine(output, "README.txt"), $"""
            Copy this entire folder to a private location on your Mac.
            URL: {url}

            In Terminal (replace /path/to with the actual directory):
              chmod 700 /path/to/KvmBridge-Mac/*.command
              chmod 600 /path/to/KvmBridge-Mac/client.conf
              /path/to/KvmBridge-Mac/select-4.command

            Assign select-1.command through select-4.command to Stream Deck buttons.
            A button using System > Open can launch a .command file through Terminal.
            An action capable of running shell commands can execute the same file directly.
            Computer numbers correspond to the KVM input labels.
            status.command prints the last observed channel, not an on-demand hardware query.
            Keep client.conf private: it contains the API key. The PEM file is public.
            Do not use curl -k: the included CA verifies the service certificate.
            HTTP 200 with confirmed=true indicates a matching channel event was received.
            HTTP 504 means the command may have executed but was not confirmed; no auto-retry.
            Windows must remain awake. Its network must use a Private/Domain profile for
            the installed firewall rule. Make a DHCP reservation for a stable address.
            """);
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.GetFiles(output)) File.SetUnixFileMode(file,
                file.EndsWith(".command") ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine($"Client files exported to {output}\nURL: {url}\nCopy this folder privately to your Mac; client.conf contains the API key.");
    }
    public static void Uninstall()
    {
        RequireAdministrator();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (ServiceExists()) { StopService(); Run("sc.exe", ["delete", ServiceName]); }
        Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; Get-NetFirewallRule -Name 'KvmBridge-HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule"]);
        Console.WriteLine("Service and firewall rule removed. Executable, configuration, certificates and logs have been retained.");
    }
    public static void ServiceStatus()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service status is available on Windows.");
        if (!ServiceExists()) { Console.WriteLine("Not installed"); return; }
        using var service = new ServiceController(ServiceName);
        Console.WriteLine("Windows service: " + service.Status);
    }
    private static void RequireAdministrator()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This command must run on Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Open PowerShell as Administrator and run this command again.");
    }
    [SupportedOSPlatform("windows")]
    private static bool ServiceExists()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        return key != null;
    }
    [SupportedOSPlatform("windows")]
    private static void StopService()
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
    }
    private static void LockDirectory(string directory, bool modify) => Run("icacls.exe", [directory, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-19:(OI)(CI)" + (modify ? "M" : "RX")]);
    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
    private static string UrlHost(string host) => host.Contains(':') ? "[" + host + "]" : host;
    private static void Run(string file, string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start " + file);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(45000)) { process.Kill(true); throw new System.TimeoutException(file + " timed out."); }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException(file + " failed: " + stdout.Result + stderr.Result);
    }
}
