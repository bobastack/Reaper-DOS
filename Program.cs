using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static volatile bool running = true;
    static long ok = 0, errs = 0, total = 0;
    static readonly ConcurrentQueue<string> csvBuffer = new();

    static async Task Main()
    {
        Console.WriteLine("---Loader(Baseline / Spike / Soak)---");
        Console.Write("URL: ");
        var url = Console.ReadLine();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Console.WriteLine("Error: invalid URL");
            return;
        }

        Console.Write("Mode [baseline|spike|soak]: ");
        var mode = (Console.ReadLine() ?? "baseline").Trim().ToLower();

        Console.Write("Max concurrency: ");
        if (!int.TryParse(Console.ReadLine(), out var maxConc))
            maxConc = 200;

        Console.Write("Duration seconds: ");
        if (!int.TryParse(Console.ReadLine(), out var seconds))
            seconds = 60;

        Console.Write("Error stop threshold: ");
        if (!int.TryParse(Console.ReadLine(), out var stopErrPct))
            stopErrPct = 20;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = Math.Max(512, maxConc),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.ConnectionClose = false;
        client.DefaultRequestHeaders.Add("User-Agent", "Reaper/Softworks");

        var cts = new CancellationTokenSource();
        var endAt = DateTime.UtcNow.AddSeconds(seconds);

        // HUD
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var sent = Interlocked.Read(ref total);
                var good = Interlocked.Read(ref ok);
                var bad = Interlocked.Read(ref errs);
                var errPct = sent == 0 ? 0 : (int)(bad * 100 / sent);
                Console.Title = $"Sent {sent} | OK {good} | Err {bad} ({errPct}%) | Concurrency target {CurrentConcurrency(mode, maxConc, endAt):0}";
                await Task.Delay(300);
            }
        });

        // CSV writer
        var csvPath = Path.Combine(Environment.CurrentDirectory, $"load_results_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        using var sw = new StreamWriter(csvPath);
        await sw.WriteLineAsync("ts_ms,latency_ms,status_code");
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                while (csvBuffer.TryDequeue(out var line))
                    await sw.WriteLineAsync(line);
                await Task.Delay(200);
            }
        });

        var gate = new SemaphoreSlim(0);
        int active = 0;

        // Concurrency updater
        _ = Task.Run(async () =>
        {
            while (DateTime.UtcNow < endAt && !cts.IsCancellationRequested)
            {
                var target = (int)Math.Max(1, CurrentConcurrency(mode, maxConc, endAt));
                var delta = target - active;
                if (delta > 0)
                {
                    gate.Release(delta);
                    Interlocked.Add(ref active, delta);
                }
                await Task.Delay(500);
            }
        });

        // Error monitor
        _ = Task.Run(async () =>
        {
            while (DateTime.UtcNow < endAt && !cts.IsCancellationRequested)
            {
                var sent = Interlocked.Read(ref total);
                var bad = Interlocked.Read(ref errs);
                var errPct = sent == 0 ? 0 : (int)(bad * 100 / sent);
                if (errPct >= stopErrPct && sent > 50)
                {
                    Console.WriteLine($"\n[!] Stopping early: error rate {errPct}% >= threshold {stopErrPct}%.");
                    running = false;
                    cts.Cancel();
                    break;
                }
                await Task.Delay(1000);
            }
        });

        var tasks = new Task[Math.Max(1, maxConc)];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var rnd = new Random(Guid.NewGuid().GetHashCode());
                while (running && DateTime.UtcNow < endAt && !cts.IsCancellationRequested)
                {
                    await gate.WaitAsync(cts.Token).ConfigureAwait(false);
                    _ = SingleRequestLoop(client, uri, rnd, cts.Token).ContinueWith(_ =>
                    {
                        Interlocked.Decrement(ref active);
                    }, TaskScheduler.Default);
                }
            });
        }

        while (DateTime.UtcNow < endAt && running)
            await Task.Delay(250);

        running = false;
        cts.Cancel();
        await Task.WhenAll(tasks);

        Console.WriteLine("\nDone!");
        var sentFinal = Interlocked.Read(ref total);
        var okFinal = Interlocked.Read(ref ok);
        var errFinal = Interlocked.Read(ref errs);
        var errPctFinal = sentFinal == 0 ? 0 : (int)(errFinal * 100 / sentFinal);
        Console.WriteLine($"Sent: {sentFinal} | OK: {okFinal} | Err: {errFinal} ({errPctFinal}%)");
        Console.WriteLine($"Results saved: {csvPath}");
    }

    static async Task SingleRequestLoop(HttpClient client, Uri uri, Random rnd, CancellationToken token)
    {
        try
        {
            if (rnd.NextDouble() < 0.15) await Task.Delay(rnd.Next(1, 10), token);

            var swatch = Stopwatch.StartNew();
            using var resp = await client.GetAsync(uri, token).ConfigureAwait(false);
            swatch.Stop();

            Interlocked.Increment(ref total);
            if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 400)
                Interlocked.Increment(ref ok);
            else
                Interlocked.Increment(ref errs);

            csvBuffer.Enqueue($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{swatch.ElapsedMilliseconds},{(int)resp.StatusCode}");
        }
        catch
        {
            Interlocked.Increment(ref total);
            Interlocked.Increment(ref errs);
            csvBuffer.Enqueue($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},-1,0");
        }
    }

    static double CurrentConcurrency(string mode, int maxConc, DateTime endAt)
    {
        var now = DateTime.UtcNow;
        var total = (endAt - now).TotalSeconds;
        if (total <= 0) return 0;

        var start = endAt - TimeSpan.FromSeconds(Math.Max(1, (int)total));
        var t = Math.Clamp((now - start).TotalSeconds / (endAt - start).TotalSeconds, 0.0, 1.0);

        return mode switch
        {
            "baseline" => Math.Max(1, 0.1 * maxConc + 0.9 * maxConc * t),
            "spike" => t < 0.2 ? Math.Max(1, maxConc * (0.2 + 4.0 * t))
                      : t < 0.25 ? maxConc * 1.2
                      : Math.Max(1, maxConc * (0.8 + 0.2 * (t - 0.25) / 0.75)),
            "soak" => Math.Max(1, maxConc * (0.05 + 0.95 * t)),
            _ => Math.Max(1, 0.1 * maxConc + 0.9 * maxConc * t)
        };
    }
}
