using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KvmBridge;

public interface ISerialConnection : IDisposable
{
    string Name { get; }
    void Open();
    string Read();
    void Write(byte[] bytes);
    bool Present();
}

public sealed class SerialConnection(Configuration config) : ISerialConnection
{
    private SerialPort? port;
    public string Name => port?.PortName ?? "unknown";
    public void Open()
    {
        var name = config.SerialPort ?? PortResolver.Find(config.UsbSerialNumber)
            ?? throw new IOException("Configured USB serial adapter is not present.");
        port = new SerialPort(name, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None, DtrEnable = false, RtsEnable = false,
            ReadTimeout = 100, WriteTimeout = 1000, Encoding = Encoding.ASCII
        };
        port.Open();
        // No flushing: it could discard front-panel events received during startup.
    }
    public string Read()
    {
        try
        {
            var buffer = new byte[512];
            var count = port!.Read(buffer, 0, buffer.Length);
            return Encoding.ASCII.GetString(buffer, 0, count);
        }
        catch (TimeoutException) { return ""; }
    }
    public void Write(byte[] bytes) => port!.Write(bytes, 0, bytes.Length);
    public bool Present() => !OperatingSystem.IsWindows() ? File.Exists(Name) :
        SerialPort.GetPortNames().Contains(Name, StringComparer.OrdinalIgnoreCase)
        && (config.SerialPort != null || string.Equals(PortResolver.Find(config.UsbSerialNumber), Name, StringComparison.OrdinalIgnoreCase));
    public void Dispose() { port?.Dispose(); }
}

public static class PortResolver
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint device, string deviceInstanceId, uint flags);
    public static string? Find(string serial)
    {
        var live = SerialPort.GetPortNames();
        if (!OperatingSystem.IsWindows()) return live.FirstOrDefault(p => p.StartsWith("/dev/cu.", StringComparison.Ordinal) && p.EndsWith("usbserial-" + serial, StringComparison.Ordinal))
            ?? live.SingleOrDefault(p => p.EndsWith("usbserial-" + serial, StringComparison.Ordinal));
        using var bus = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\FTDIBUS");
        if (bus == null) return null;
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in bus.GetSubKeyNames())
        {
            // FTDI adds the interface letter A to an FT232R serial number.
            if (!device.Equals("VID_0403+PID_6001+" + serial + "A", StringComparison.OrdinalIgnoreCase)
                && !device.Equals("VID_0403+PID_6001+" + serial, StringComparison.OrdinalIgnoreCase)) continue;
            using var key = bus.OpenSubKey(device);
            if (key == null) continue;
            foreach (var instance in key.GetSubKeyNames())
            {
                // Registry entries survive unplugging. Require a currently configured PnP
                // device so an old COM number cannot accidentally target another adapter.
                if (CM_Locate_DevNodeW(out _, @"FTDIBUS\" + device + @"\" + instance, 0) != 0) continue;
                using var parameters = key.OpenSubKey(instance + @"\Device Parameters");
                if (parameters?.GetValue("PortName") is string name && live.Contains(name, StringComparer.OrdinalIgnoreCase)) matches.Add(name);
            }
        }
        return matches.Count == 1 ? matches.Single() : null;
    }
}

public sealed record DeviceStatus(
    bool SerialConnected, string? SerialPort, int? Computer, string SelectionState,
    int? LastObservedComputer, DateTimeOffset? LastObservedAt, DateTimeOffset? LastSerialDataAt,
    string? LastError, string StatusSource = "serial-channel-events",
    bool IndependentStatusQuerySupported = false,
    string Note = "Event-tracked channel only; no heartbeat or video verification. Undetected KVM power loss can leave the observation stale. Split-display/focus state is not decoded.");

public sealed record SwitchResult(
    bool Confirmed, int Computer, string? Reply, DateTimeOffset? ObservedAt, string? Error,
    [property: JsonIgnore] int HttpStatus,
    string Confirmation = "Matching channel event; firmware supplies no request IDs.");

public sealed class SerialParser
{
    private readonly StringBuilder partial = new();
    private static readonly Regex Channel = new(@"Set ch is ([0-3])(?:\s|$)", RegexOptions.CultureInvariant);
    public void Clear() => partial.Clear();
    public void Feed(string text, Action<string, int?> line)
    {
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n')
            {
                if (partial.Length == 0) continue;
                var value = partial.ToString();
                partial.Clear();
                var match = Channel.Match(value);
                line(value, match.Success ? int.Parse(match.Groups[1].Value) + 1 : null);
            }
            else if (ch is >= ' ' and <= '~')
            {
                if (partial.Length >= 4096) partial.Clear();
                partial.Append(ch);
            }
        }
    }
}

public sealed class KvmController : BackgroundService
{
    private sealed class Request(int computer, CancellationToken cancellation)
    {
        public int Computer { get; } = computer;
        public CancellationToken Cancellation { get; } = cancellation;
        public long Expires { get; } = Environment.TickCount64 + 8000;
        public TaskCompletionSource<SwitchResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Configuration config;
    private readonly AuditLog log;
    private readonly Func<ISerialConnection> factory;
    private readonly BlockingCollection<Request> queue = new(16);
    private readonly object gate = new();
    private bool connected;
    private string? portName;
    private int? observed;
    private bool valid;
    private DateTimeOffset? observedAt;
    private DateTimeOffset? dataAt;
    private string? error;
    public KvmController(Configuration config, AuditLog log, Func<ISerialConnection> factory)
    { this.config = config; this.log = log; this.factory = factory; }
    public DeviceStatus Snapshot()
    {
        lock (gate) return new(connected, portName, valid ? observed : null,
            valid ? "observed" : observed.HasValue ? "stale" : "unknown", observed, observedAt, dataAt, error);
    }
    public async Task<SwitchResult> Select(int computer, CancellationToken cancellation)
    {
        if (computer is < 1 or > 4) return new(false, computer, null, null, "invalid_computer", 400);
        if (!Snapshot().SerialConnected) return new(false, computer, null, null, "serial_disconnected", 503);
        var request = new Request(computer, cancellation);
        if (!queue.TryAdd(request)) return new(false, computer, null, null, "command_queue_full", 429);
        try { return await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(9), cancellation); }
        catch (TimeoutException) { return new(false, computer, null, null, "request_expired", 504); }
    }
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => Loop(stoppingToken), stoppingToken);
    private void Loop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Request? pending = null;
            ISerialConnection? serial = null;
            var parser = new SerialParser();
            long sentAt = 0, nextSend = 0, nextPresence = 0;
            try
            {
                serial = factory();
                serial.Open();
                lock (gate) { connected = true; portName = serial.Name; valid = false; error = null; }
                log.Write("Serial connected: " + serial.Name);
                while (!stop.IsCancellationRequested)
                {
                    var now = Environment.TickCount64;
                    if (now >= nextPresence)
                    {
                        if (!serial.Present()) throw new IOException("USB serial adapter disconnected.");
                        nextPresence = now + 1000;
                    }
                    // Read before dispatch, preserving button events and draining earlier replies.
                    var data = serial.Read();
                    if (data.Length > 0)
                    {
                        lock (gate) dataAt = DateTimeOffset.UtcNow;
                        parser.Feed(data, (line, computer) =>
                        {
                            log.Write("RX " + line);
                            if (computer is not int pc) return;
                            var timestamp = DateTimeOffset.UtcNow;
                            lock (gate) { observed = pc; observedAt = timestamp; valid = true; error = null; }
                            if (pending?.Computer == pc)
                            {
                                pending.Completion.TrySetResult(new(true, pc, line, timestamp, null, 200));
                                pending = null;
                                nextSend = Environment.TickCount64 + 300;
                            }
                        });
                    }
                    now = Environment.TickCount64;
                    if (pending != null && (now - sentAt >= config.ResponseTimeoutMs || now >= pending.Expires))
                    {
                        pending.Completion.TrySetResult(new(false, pending.Computer, null, null, "kvm_confirmation_timeout", 504));
                        lock (gate) { valid = false; error = "KVM did not confirm the requested channel; command may still have executed."; }
                        log.Write("Switch confirmation timed out; no automatic retry");
                        pending = null;
                        nextSend = now + 300;
                    }
                    if (pending == null && now >= nextSend && queue.TryTake(out var request))
                    {
                        if (request.Cancellation.IsCancellationRequested || now >= request.Expires)
                        {
                            request.Completion.TrySetResult(new(false, request.Computer, null, null, "request_expired_before_send", 504));
                            continue;
                        }
                        // No retry: a serial write can succeed even if its acknowledgement is lost.
                        pending = request;
                        lock (gate) valid = false;
                        serial.Write([0xAA, 0xBB, 0x03, 0x01, (byte)request.Computer, 0xEE]);
                        sentAt = Environment.TickCount64;
                        log.Write("TX selection " + request.Computer);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or ArgumentException)
            {
                lock (gate) error = ex.Message;
                log.Write("Serial unavailable: " + ex.Message);
            }
            catch (Exception ex)
            {
                log.Write("Fatal serial worker failure: " + ex.Message);
                Environment.ExitCode = 1;
                throw;
            }
            finally
            {
                lock (gate) { connected = false; valid = false; }
                pending?.Completion.TrySetResult(new(false, pending.Computer, null, null, "serial_disconnected_during_command", 503));
                while (queue.TryTake(out var waiting)) waiting.Completion.TrySetResult(new(false, waiting.Computer, null, null, "serial_disconnected", 503));
                try { serial?.Dispose(); } catch (Exception ex) { log.Write("Serial close: " + ex.Message); }
            }
            if (!stop.IsCancellationRequested) stop.WaitHandle.WaitOne(2000);
        }
    }
}
