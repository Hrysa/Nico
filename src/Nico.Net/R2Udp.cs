using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nico.Net.Abstractions;

namespace Nico.Net;

internal struct BufferFragment
{
    public IntPtr Buffer;
    public int Length;
}

public class R2Udp : ISocketReceiver
{
    public Action<IConnection, byte[], int> OnMessage { get; set; }

    private readonly Socket _socket;

    private const int Mtu = 1440;
    private readonly bool _listenMode;

    #region server

    private Dictionary<IPEndPoint, R2Connection> _connections = new();

    // not thread safe, don't use it
    public Dictionary<IPEndPoint, R2Connection> Connections => _connections;

    #endregion

    #region client

    private R2Connection? _connection;
    private IPEndPoint? _remoteEndPoint;
    private SocketAddress? _remoteSocketAddress;
    private bool _connected;

    public R2Connection? Connection => _connection;
    public IPEndPoint? RemoteEndPoint => _remoteEndPoint;

    #endregion

    // TODO: make it pool to separate connections into workers
    private static Task _connectionUpdater;
    private static HashSet<R2Connection> _updateThreadConnections = new();
    private static object _locker = new();

    private static ConcurrentQueue<R2Connection> _newConnections = new();

    private static ConcurrentStack<IntPtr> _ptrStack = new();

    internal static void ReturnPtr(IntPtr ptr)
    {
        _ptrStack.Push(ptr);
    }

    public void Start()
    {
        _socket.ReceiveFromAsync(ConfigureSocketEventArgs());
    }

    private void ReceivedSocketEvent(object? sender, SocketAsyncEventArgs e)
    {
        HandleSaea(e);

        while (!_socket.ReceiveFromAsync(e))
        {
            HandleSaea(e);
        }
    }

    private unsafe void HandleSaea(SocketAsyncEventArgs e)
    {
        try
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.ReceiveFrom:
                {
                    if (e.BytesTransferred is > Mtu or < 12)
                    {
                        break;
                    }

                    var endPoint = (IPEndPoint)e.RemoteEndPoint!;

                    if (!_connections.TryGetValue(endPoint, out var connection))
                    {
                        Helper.Log($"new connection created {endPoint}");
                        connection = new R2Connection(Mtu, endPoint.Serialize(), _socket)
                        {
                            OnMessage = OnMessage
                        };

                        _connections.Add(
                            new IPEndPoint(new IPAddress(endPoint.Address.GetAddressBytes()), endPoint.Port),
                            connection);

                        _newConnections.Enqueue(connection);
                    }

                    if (!_ptrStack.TryPop(out var ptr))
                    {
                        ptr = Marshal.AllocHGlobal(Mtu);
                    }

                    var span = MemoryMarshal.CreateSpan(
                        ref Unsafe.AddByteOffset(ref Unsafe.AsRef<byte>((void*)ptr), 0), Mtu);
                    e.MemoryBuffer.Span.Slice(0, e.BytesTransferred).CopyTo(span);

                    connection.IncomeBuffer.Enqueue(new BufferFragment
                        { Buffer = ptr, Length = e.BytesTransferred });

                    break;
                }
                default:
                    throw new InvalidOperationException();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private SocketAsyncEventArgs ConfigureSocketEventArgs()
    {
        var eventArg = new SocketAsyncEventArgs();
        eventArg.Completed += ReceivedSocketEvent;
        eventArg.SetBuffer(new byte[65536]);
        eventArg.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        return eventArg;
    }

    public R2Udp(IPEndPoint? ipEndPoint = null)
    {
        if (_connectionUpdater is null)
        {
            lock (_locker)
            {
                _connectionUpdater ??= Task.Run(ConnectionUpdate);
            }
        }

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            const uint IOC_IN = 0x80000000;
            const uint IOC_VENDOR = 0x18000000;
            const int SIO_UDP_CONNRESET = unchecked((int)(IOC_IN | IOC_VENDOR | 12));

            _socket.IOControl(SIO_UDP_CONNRESET, [Convert.ToByte(false)], null);
        }

        if (ipEndPoint is null)
        {
            return;
        }

        _socket.Bind(ipEndPoint);
        _listenMode = true;
    }

    private static void ConsumeBufferFragment()
    {
        while (_newConnections.TryDequeue(out var connection))
        {
            _updateThreadConnections.Add(connection);
        }
    }

    private static void ConnectionUpdate()
    {
        while (true)
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;

                ConsumeBufferFragment();

                long ticks = DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

                foreach (var connection in _updateThreadConnections)
                {
                    try
                    {
                        while (connection.IncomeBuffer.Dequeue(out var fragment))
                        {
                            connection.Receive(fragment.Buffer, fragment.Length, ticks);
                            ReturnPtr(fragment.Buffer);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }

                    connection.Update(ticks);
                    connection.SendAck();
                }

                var d = DateTimeOffset.Now - now;
                if (d > TimeSpan.FromMilliseconds(1))
                {
                    Console.WriteLine($"update cost {d}");
                }
                Thread.Sleep(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }

    public R2Connection Connect(IPEndPoint ipEndPoint)
    {
        if (_connected)
        {
            throw new InvalidOperationException("already connected");
        }

        _remoteEndPoint = ipEndPoint;
        _remoteSocketAddress = ipEndPoint.Serialize();
        _connection = new R2Connection(Mtu, _remoteSocketAddress, _socket);
        _connected = true;
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        _connections.Add(new IPEndPoint(new IPAddress(ipEndPoint.Address.GetAddressBytes()), ipEndPoint.Port),
            _connection);

        _newConnections.Enqueue(_connection);
        return _connection;
    }

    public struct FragmentInfo
    {
        public ushort FrameId;
        public ushort AckFrameId;
        public ushort MsgId;
        public ushort MsgChunkIndex;


        public bool MessageHead => MsgChunkIndex is 0;
    }
}
