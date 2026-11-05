using System.Diagnostics;
using System.Text;

namespace KlidanAudioProbe.Gui.Core;

public static class EndpointDiscoveryQaTrace
{
    private static readonly object Sync = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly AsyncLocal<Guid?> Operation = new();
    private static string? _path;

    public static bool Enabled => Volatile.Read(ref _path) is not null;

    public static IDisposable BeginOperation(Guid operation)
    {
        var previous = Operation.Value;
        Operation.Value = operation;
        return new Scope(() => Operation.Value = previous);
    }

    public static void StartFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("KLIDAN_ENDPOINT_DISCOVERY_QA_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            Volatile.Write(ref _path, path);
            lock (Sync)
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
                writer.WriteLine("elapsedMs=0 stage=trace-start");
                writer.Flush();
                stream.Flush(true);
            }
        }
        catch { Volatile.Write(ref _path, null); }
    }

    public static void Write(string stage, Guid? correlation = null, string? state = null, int? exitCode = null,
        string? exceptionType = null, string? errorCode = null, bool? loading = null)
    {
        var path = Volatile.Read(ref _path);
        if (path is null) return;
        var line = $"elapsedMs={Clock.ElapsedMilliseconds} stage={Safe(stage)}" +
            (Operation.Value is null ? "" : $" operation={Operation.Value:D}") +
            (correlation is null ? "" : $" correlation={correlation:D}") +
            (state is null ? "" : $" workerState={Safe(state)}") +
            (exitCode is null ? "" : $" exitCode={exitCode}") +
            (exceptionType is null ? "" : $" exceptionType={Safe(exceptionType)}") +
            (errorCode is null ? "" : $" errorCode={Safe(errorCode)}") +
            (loading is null ? "" : $" loading={loading}");
        try
        {
            lock (Sync)
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
                writer.WriteLine(line);
                writer.Flush();
                stream.Flush(true);
            }
        }
        catch { }
    }

    private static string Safe(string value) => new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
