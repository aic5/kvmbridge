using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KvmBridge;

try
{
    var cli = new Arguments(args);
    switch (cli.Command)
    {
        case "help":
            Console.WriteLine(Setup.Help);
            return 0;
        case "source":
            var target = Path.GetFullPath(cli.Get("output") ?? "KvmBridge-source");
            Directory.CreateDirectory(target);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                using var input = asm.GetManifestResourceStream(name)!;
                using var output = File.Create(Path.Combine(target, name["KvmBridge.".Length..]));
                input.CopyTo(output);
            }
            Console.WriteLine($"Complete source exported to {target}");
            return 0;
        case "install": Setup.Install(cli); return 0;
        case "uninstall": Setup.Uninstall(); return 0;
        case "client": Setup.ExportClient(cli); return 0;
        case "status": Setup.ServiceStatus(); return 0;
        case "ports":
            foreach (var port in System.IO.Ports.SerialPort.GetPortNames().Order()) Console.WriteLine(port);
            if (cli.Get("serial") is { } serial) Console.WriteLine("Adapter match: " + (PortResolver.Find(serial) ?? "not found"));
            return 0;
        case "init":
            var folder = cli.Get("data") ?? throw new ArgumentException("init requires --data DIRECTORY");
            Setup.Initialize(Path.GetFullPath(folder), cli);
            Console.WriteLine("Configuration and certificate created. Use 'client --data DIRECTORY --output DIRECTORY' to export credentials.");
            return 0;
        case "self-test": return await SelfTests.Run();
        case "run": case "service": break;
        default: throw new ArgumentException("Unknown command. Run KvmBridge.exe help.");
    }

    var data = Path.GetFullPath(cli.Get("data") ?? Setup.DataDirectory);
    var config = Configuration.Load(data);
    if (cli.Get("port") is { } explicitPort) config.SerialPort = explicitPort;
    if (cli.Get("bind") is { } bind) config.BindAddress = bind;
    config.Validate();
    using var log = new AuditLog(Path.Combine(data, "logs"));
    using var certificate = Setup.LoadCertificate(data, config);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = data });
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    if (OperatingSystem.IsWindows()) builder.Services.AddWindowsService(o => o.ServiceName = Setup.ServiceName);
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(8));
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.AddServerHeader = false;
        o.Limits.MaxRequestBodySize = 1024;
        o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        o.Limits.MaxConcurrentConnections = 64;
        o.Listen(IPAddress.Parse(config.BindAddress), config.HttpsPort, p => p.UseHttps(certificate));
    });
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        o.SerializerOptions.PropertyNameCaseInsensitive = false;
    });
    var controller = new KvmController(config, log, () => new SerialConnection(config));
    builder.Services.AddSingleton(controller);
    builder.Services.AddHostedService(p => p.GetRequiredService<KvmController>());
    var app = builder.Build();
    var expected = Encoding.UTF8.GetBytes("Bearer " + config.ApiKey);
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        var supplied = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }
        try { await next(ctx); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        catch (BadHttpRequestException ex)
        {
            ctx.Response.StatusCode = ex.StatusCode;
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid_request" });
        }
        catch (Exception ex)
        {
            log.Write("HTTP error: " + ex.Message);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = "internal_error" });
            }
        }
    });
    app.MapGet("/health", () => Results.Ok(new
    {
        service = "running", version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3), serialConnected = controller.Snapshot().SerialConnected,
        note = "An open USB serial port does not prove that the KVM is powered on or responding."
    }));
    app.MapGet("/api/kvm/status", () => Results.Ok(controller.Snapshot()));
    app.MapGet("/api/kvm/capabilities", () => Results.Ok(new
    {
        model = "TESmart HKS0802A1U", computers = new[] { 1, 2, 3, 4 },
        selection = "both displays together", readback = "unsolicited channel events",
        independentStatusQuery = false, separateDisplayRouting = false,
        note = "Manual split-display routing and keyboard focus are not decoded. Channel observations are not video-signal verification."
    }));
    app.MapPut("/api/kvm/selection", async (SelectionInput input, HttpContext ctx) =>
    {
        if (input.Computer is < 1 or > 4)
            return Results.Json(new { error = "computer_must_be_1_to_4" }, statusCode: 400);
        var result = await controller.Select(input.Computer, ctx.RequestAborted);
        return Results.Json(result, statusCode: result.HttpStatus);
    });
    log.Write($"Starting HTTPS on {config.BindAddress}:{config.HttpsPort}; serial {(config.SerialPort ?? "auto:" + config.UsbSerialNumber)}");
    Console.WriteLine($"KvmBridge HTTPS port {config.HttpsPort}; log directory {Path.Combine(data, "logs")}");
    await app.RunAsync();
    log.Write("Service stopped");
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine("KvmBridge: " + ex.Message);
    return 1;
}

namespace KvmBridge
{
    public record SelectionInput(int Computer);

    public sealed class Arguments
    {
        public string Command { get; }
        private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        public Arguments(string[] args)
        {
            Command = args.Length == 0 || args[0] is "--help" or "-h" ? "help" : args[0].ToLowerInvariant();
            for (var i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException("Options require --name VALUE.");
                if (!values.TryAdd(args[i][2..], args[++i])) throw new ArgumentException("Duplicate option.");
            }
            string[] allowed = ["output", "data", "port", "serial", "bind", "https-port", "host"];
            if (values.Keys.Any(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("Unknown option. Run help.");
        }
        public string? Get(string key) => values.GetValueOrDefault(key);
    }

    public sealed class Configuration
    {
        public string? SerialPort { get; set; }
        public string UsbSerialNumber { get; set; } = "";
        public string BindAddress { get; set; } = "0.0.0.0";
        public int HttpsPort { get; set; } = 8443;
        public string ApiKey { get; set; } = "";
        public string CertificatePassword { get; set; } = "";
        public string PreferredHost { get; set; } = "localhost";
        public int ResponseTimeoutMs { get; set; } = 4000;
        public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        public static Configuration Load(string data) => JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Path.Combine(data, "config.json")), Json)
            ?? throw new InvalidDataException("Invalid configuration.");
        public void Validate()
        {
            if (!IPAddress.TryParse(BindAddress, out _)) throw new ArgumentException("BindAddress must be an IP address.");
            if (HttpsPort is < 1024 or > 65535) throw new ArgumentException("HTTPS port must be 1024–65535.");
            if (ApiKey.Length < 32 || CertificatePassword.Length < 16) throw new ArgumentException("Missing or invalid credentials. Run install or init.");
            if (ResponseTimeoutMs is < 500 or > 6000) throw new ArgumentException("ResponseTimeoutMs must be 500–6000.");
            if (SerialPort == null && (UsbSerialNumber.Length == 0 || !UsbSerialNumber.All(char.IsAsciiLetterOrDigit)))
                throw new ArgumentException("Specify --port COM5 or --serial YOUR_FTDI_SERIAL; serial numbers must contain letters and digits.");
        }
    }

    public sealed class AuditLog : IDisposable
    {
        private readonly string folder;
        private readonly object gate = new();
        public AuditLog(string folder) { this.folder = folder; Directory.CreateDirectory(folder); }
        public void Write(string message)
        {
            lock (gate)
            {
                try
                {
                    var file = Path.Combine(folder, "kvmbridge.log");
                    if (File.Exists(file) && new FileInfo(file).Length > 5 * 1024 * 1024)
                    {
                        for (var i = 2; i >= 1; i--)
                        {
                            var previous = file + "." + i;
                            if (File.Exists(previous)) File.Move(previous, file + "." + (i + 1), true);
                        }
                        File.Move(file, file + ".1", true);
                    }
                    File.AppendAllText(file, DateTimeOffset.UtcNow.ToString("O") + " " + message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
                }
                catch (IOException ex) { Console.Error.WriteLine("Log write failed: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Console.Error.WriteLine("Log write failed: " + ex.Message); }
            }
        }
        public void Dispose() { }
    }
}
