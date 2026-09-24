using System.Collections.Concurrent;

namespace KvmBridge;

public static class SelfTests
{
    private sealed class FakeSerial : ISerialConnection
    {
        public readonly ConcurrentQueue<string> Incoming = new();
        public readonly ConcurrentQueue<byte[]> Writes = new();
        public volatile bool IsPresent = true;
        public volatile bool Reply = true;
        public string Name => "TEST";
        public void Open() { if (!IsPresent) throw new IOException("test disconnect"); }
        public bool Present() => IsPresent;
        public string Read() { Thread.Sleep(5); return Incoming.TryDequeue(out var value) ? value : ""; }
        public void Write(byte[] bytes)
        {
            Writes.Enqueue(bytes);
            if (Reply)
            {
                Incoming.Enqueue("Set ch");
                Incoming.Enqueue(" is " + (bytes[4] - 1) + "\r\r\n");
            }
        }
        public void Dispose() { }
    }
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAILED: " + label);
        Console.WriteLine("PASS: " + label);
    }
    private static async Task Until(Func<bool> condition)
    {
        var end = Environment.TickCount64 + 4000;
        while (!condition())
        {
            if (Environment.TickCount64 > end) throw new TimeoutException("Test condition timed out");
            await Task.Delay(10);
        }
    }
    public static async Task<int> Run()
    {
        var parser = new SerialParser();
        var states = new List<int>();
        void Line(string _, int? pc) { if (pc is int value) states.Add(value); }
        parser.Feed("QuerrySet ch i", Line);
        parser.Feed("s 0\r\r\nled_status_update=01 idx=2\r\nSet ch is 3\r\n", Line);
        parser.Feed("Set ch is 31\r\nSet ch is 4\r\nSet ch is -1\r\n", Line);
        Check(states.SequenceEqual(new[] { 1, 4 }), "fragmented/noisy input parsed; LED diagnostics and invalid channels ignored");
        parser.Feed(new string('x', 5000) + "\rSet ch is 2\n", Line);
        Check(states.Last() == 3, "bounded parser recovers from oversized diagnostic lines");
        var directory = Path.Combine(Path.GetTempPath(), "KvmBridge-test-" + Guid.NewGuid().ToString("N"));
        using var log = new AuditLog(directory);
        var serial = new FakeSerial();
        var config = new Configuration { ResponseTimeoutMs = 500 };
        using var controller = new KvmController(config, log, () => serial);
        await controller.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => controller.Snapshot().SerialConnected);
            Check(controller.Snapshot().Computer == null && controller.Snapshot().SelectionState == "unknown", "startup does not invent a channel");
            var selected = await controller.Select(4, CancellationToken.None);
            Check(selected.Confirmed && selected.Computer == 4 && selected.Reply == "Set ch is 3", "selection waits for a matching fragmented response");
            Check(serial.Writes.TryPeek(out var bytes) && bytes.SequenceEqual(new byte[] { 0xaa, 0xbb, 3, 1, 4, 0xee }), "exact TESmart binary command bytes");
            serial.Incoming.Enqueue("Set ch is 0\r\r\n");
            await Until(() => controller.Snapshot().Computer == 1);
            Check(true, "unsolicited front-panel event updates status");
            var invalid = await controller.Select(5, CancellationToken.None);
            Check(invalid.HttpStatus == 400, "invalid computer rejected");
            var jobs = Enumerable.Range(1, 4).Select(pc => controller.Select(pc, CancellationToken.None)).ToArray();
            var results = await Task.WhenAll(jobs);
            Check(results.All(r => r.Confirmed) && results.Select(r => r.Computer).SequenceEqual(new[] { 1, 2, 3, 4 }), "concurrent requests serialized with independent confirmations");
            var count = serial.Writes.Count;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await controller.Select(1, cancelled.Token); } catch (OperationCanceledException) { }
            }
            await Task.Delay(400);
            Check(serial.Writes.Count == count, "cancelled queued request never transmitted");
            serial.Reply = false;
            var timeout = await controller.Select(2, CancellationToken.None);
            Check(!timeout.Confirmed && timeout.HttpStatus == 504 && serial.Writes.Count == count + 1, "missing response returns timeout without retry");
            Check(controller.Snapshot().Computer == null && controller.Snapshot().SelectionState == "stale", "timeout invalidates current selection but retains last observation");
            serial.IsPresent = false;
            await Until(() => !controller.Snapshot().SerialConnected);
            Check((await controller.Select(1, CancellationToken.None)).HttpStatus == 503, "disconnect rejects switching");
            serial.IsPresent = true;
            serial.Reply = true;
            await Until(() => controller.Snapshot().SerialConnected);
            Check(controller.Snapshot().Computer == null, "reconnect does not restore stale selection as current");
            Check((await controller.Select(4, CancellationToken.None)).Confirmed, "switching recovers after reconnect");
        }
        finally
        {
            await controller.StopAsync(CancellationToken.None);
            Directory.Delete(directory, true);
        }
        Console.WriteLine("All hardware-free tests passed.");
        return 0;
    }
}
