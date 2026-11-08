using System.IO.Pipes;
using System.Text;

namespace Osas.Core.Broker;

/// <summary>
/// Serves <see cref="SessionBroker"/> on a named pipe. The pipe is made for the current user only (no one else, and no network), in message
/// mode: one JSON message in, one JSON message out, then the connection is closed.
/// </summary>
public sealed class SessionBrokerPipe : IDisposable
{
    public const string DefaultName = "osas-session-broker";
    private const int MaxRequestBytes = 64 * 1024;

    private readonly SessionBroker _broker;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public SessionBrokerPipe(SessionBroker broker, string name = DefaultName)
    {
        _broker = broker;
        Name = name;
    }

    public string Name { get; }

    /// <summary>
    /// Creates the pipe and starts answering. False if the pipe already exists (another Kivo is serving it), in which case this one does not.
    /// </summary>
    public bool Start()
    {
        NamedPipeServerStream first;
        try
        {
            first = Create(first: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows answers "access denied" when the name is already taken by a first instance.
            return false;
        }
        _loop = Task.Run(() => Accept(first));
        return true;
    }

    private NamedPipeServerStream Create(bool first = false) =>
        new(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 8192, 8192);

    private async Task Accept(NamedPipeServerStream pipe)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }
            catch (IOException)
            {
                // A caller that connected and vanished: wait for the next.
                pipe.Dispose();
                pipe = Create();
                continue;
            }

            // Each caller is served on its own, since a sign-in can keep one waiting for minutes.
            var served = pipe;
            _ = Task.Run(() => Serve(served));
            try
            {
                pipe = Create();
            }
            catch (IOException)
            {
                return;
            }
        }
        pipe.Dispose();
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                var request = await ReadMessage(pipe);
                if (request == null)
                    return;
                var response = await _broker.HandleAsync(request, _stop.Token);
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(response), _stop.Token);
                await pipe.FlushAsync(_stop.Token);
                if (OperatingSystem.IsWindows())
                    pipe.WaitForPipeDrain();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The caller went away, or Kivo is closing.
            }
        }
    }

    private static async Task<string?> ReadMessage(NamedPipeServerStream pipe)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        do
        {
            var n = await pipe.ReadAsync(chunk);
            if (n == 0)
                return null;
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxRequestBytes)
                return "{}";
        }
        while (!pipe.IsMessageComplete);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
    }
}
