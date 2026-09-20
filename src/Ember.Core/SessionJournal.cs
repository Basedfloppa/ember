using System.Text.Json;
using System.Threading.Channels;

namespace Ember.Core;

/// <summary>Bounded, single-writer journal. Render callbacks only enqueue immutable batches.</summary>
public sealed class SessionJournal : IAsyncDisposable
{
    private readonly Channel<SessionBatch> queue;
    private readonly Task writer;
    private readonly string path;
    private readonly long byteLimit;
    private long dropped, bytes;
    private string? error;
    private int closed;
    public long DroppedBatches => Interlocked.Read(ref dropped);
    public long BytesWritten => Interlocked.Read(ref bytes);
    public string? Error => Volatile.Read(ref error);
    public bool IsClosed => Volatile.Read(ref closed) != 0;

    public SessionJournal(string directory, long maxBytes = 512L * 1024 * 1024, int queueCapacity = 32)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "batches.ndjson");
        byteLimit = Math.Max(1024, maxBytes);
        queue = Channel.CreateBounded<SessionBatch>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        // Create synchronously so startup IO errors are surfaced before declaring a session active.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        writer = Task.Run(() => Write(stream));
    }

    public void Enqueue(SessionBatch batch)
    {
        if (IsClosed || Error is not null || !queue.Writer.TryWrite(batch)) Interlocked.Increment(ref dropped);
    }

    private async Task Write(FileStream stream)
    {
        var options = new JsonSerializerOptions(Json.Options) { WriteIndented = false };
        byte[] newline = [(byte)'\n'];
        try
        {
            await using (stream)
            {
                await foreach (var batch in queue.Reader.ReadAllAsync())
                {
                    if (Error is not null) { Interlocked.Increment(ref dropped); continue; }
                    byte[] data = JsonSerializer.SerializeToUtf8Bytes(batch, options);
                    if (BytesWritten + data.Length + 1 > byteLimit)
                    {
                        Volatile.Write(ref error, "Session journal reached its disk budget.");
                        Interlocked.Increment(ref dropped);
                        continue;
                    }
                    await stream.WriteAsync(data);
                    await stream.WriteAsync(newline);
                    await stream.FlushAsync(); // Survives process exit without relying on a game shutdown hook.
                    Interlocked.Add(ref bytes, data.Length + 1);
                }
                await stream.FlushAsync();
            }
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref dropped); // The batch being serialized/written was not committed.
            Volatile.Write(ref error, e.GetType().Name + ": " + Redactor.Text(e.Message));
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
        }
    }

    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) queue.Writer.TryComplete();
        await writer;
    }

    public async ValueTask DisposeAsync() => await CompleteAsync();
}
