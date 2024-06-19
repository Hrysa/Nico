using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nico.Core;
using Nico.Net.Abstractions;

namespace Nico.Net;

internal struct BufferFragment
{
    public R2Connection Connection;
    public IntPtr Buffer;
    public int Length;
}

public class R2Udp : ISocketReceiver
{
    internal static ConcurrentBag<R2Udp> Group = new();

    public Action<IConnection, byte[], int> OnMessage { get; set; }

    private readonly Socket _socket;

    private readonly byte[] _receiveBuffer;
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
    private Task _connectionUpdater;
    private static HashSet<R2Connection> _updateThreadConnections = new();
    private static object _locker = new();

    private static ConcurrentStack<IntPtr> _ptrStack = new();

    private static SPSCQueue<BufferFragment> IncomeFragments = SPSCQueue<BufferFragment>.Create(10240);

    internal void ReturnPtr(IntPtr ptr)
    {
        _ptrStack.Push(ptr);
    }

    public void Start()
    {
        _socket.ReceiveFromAsync(ConfigureSocketEventArgs());
    }

    private unsafe void ReceivedSocketEvent(object? sender, SocketAsyncEventArgs e)
    {
        Console.WriteLine("RECVING");
        try
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.ReceiveFrom:
                {
                    Console.WriteLine($"RECV {e.BytesTransferred}");


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
                    }

                    if (!_ptrStack.TryPop(out var ptr))
                    {
                        ptr = Marshal.AllocHGlobal(Mtu);
                    }

                    Console.WriteLine(e.BytesTransferred);

                    var span = MemoryMarshal.CreateSpan(
                        ref Unsafe.AddByteOffset(ref Unsafe.AsRef<byte>((void*)ptr), 0), Mtu);
                    e.MemoryBuffer.Span.Slice(0, e.BytesTransferred).CopyTo(span);

                    IncomeFragments.Enqueue(new BufferFragment
                        { Connection = connection, Buffer = ptr, Length = e.BytesTransferred });

                    for (int i = 0; i < e.BytesTransferred; i++)
                    {
                        Console.Write($"{e.MemoryBuffer.Span[i]} ");
                    }

                    Console.WriteLine();

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

        _socket.ReceiveFromAsync(e);
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


        _receiveBuffer = new byte[_socket.ReceiveBufferSize];

        Group.Add(this);

        if (ipEndPoint is null)
        {
            return;
        }

        _socket.Bind(ipEndPoint);
        _listenMode = true;
    }

    private static void ConsumeBufferFragment()
    {
        while (IncomeFragments.Dequeue(out var fragment))
        {
            _updateThreadConnections.Add(fragment.Connection);

            if (fragment.Length is 0)
            {
                continue;
            }

            fragment.Connection.IncomeBuffer.Enqueue(fragment);
        }
    }

    private static void ConnectionUpdate()
    {
        while (true)
        {
            try
            {
                ConsumeBufferFragment();

                long ticks = DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

                foreach (var connection in _updateThreadConnections)
                {
                    while (connection.IncomeBuffer.TryDequeue(out var fragment))
                    {
                        connection.Receive(fragment.Buffer, fragment.Length, ticks);
                    }

                    connection.Update(ticks);
                    connection.SendAck();
                }

                Thread.Sleep(100);
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

        IncomeFragments.Enqueue(new BufferFragment { Connection = _connection });

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
