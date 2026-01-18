using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using UsenetSharp.Concurrency;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private AsyncSemaphore _commandLock = new(1);
    private CancellationTokenSource _cts = new();
    private volatile ExceptionDispatchInfo? _backgroundException = null;

    /// <summary>
    /// Timeout in seconds for individual read/write operations.
    /// Default is 30 seconds, increased from 10 to handle large segments and slower providers.
    /// </summary>
    public int OperationTimeoutSeconds { get; set; } = 30;
}