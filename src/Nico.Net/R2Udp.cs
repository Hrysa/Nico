using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Nico.Net;

public class R2Udp : IMessageReceiver
{
    internal static ConcurrentBag<R2Udp> Group = new();

    private static readonly IPEndPoint EndPointFactory = new(IPAddress.Any, 0);
    public Action<IConnection, byte[], int> OnMessage { get; set; }

    private readonly Socket _socket;

    private readonly Channel<UdpConnection> _sendChannel = Channel.CreateUnbounded<UdpConnection>();

    private readonly byte[] _receiveBuffer;
    private int _mtu = 1440;
    private readonly bool _listenMode;

    #region server

    private ConcurrentDictionary<SocketAddress, UdpConnection> _connections = new();

    #endregion

    #region client

    private UdpConnection? _connection;
    private IPEndPoint? _remoteEndPoint;
    private SocketAddress? _remoteSocketAddress;
    private bool _connected;

    public UdpConnection? Connection => _connection;
    public IPEndPoint? RemoteEndPoint => _remoteEndPoint;

    #endregion

    public R2Udp(IPEndPoint? ipEndPoint = null)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            const uint IOC_IN = 0x80000000;
            const uint IOC_VENDOR = 0x18000000;
            const int SIO_UDP_CONNRESET = unchecked((int)(IOC_IN | IOC_VENDOR | 12));

            _socket.IOControl(SIO_UDP_CONNRESET, new[] { Convert.ToByte(false) }, null);
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

    public void Update()
    {
        try
        {
            long ticks = DateTimeOffset.Now.UtcTicks / TimeSpan.TicksPerMillisecond;
            _connection?.Update(ticks);
            foreach (var connection in _connections.Values)
            {
                connection.Update(ticks);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public UdpConnection Connect(IPEndPoint ipEndPoint)
    {
        if (_connected)
        {
            throw new InvalidOperationException("already connected");
        }

        _remoteEndPoint = ipEndPoint;
        _remoteSocketAddress = ipEndPoint.Serialize();
        _connection = new UdpConnection(_mtu, _remoteSocketAddress, _sendChannel.Writer);
        _connected = true;

        return _connection;
    }

    public void Receive()
    {
        try
        {
            var address = new SocketAddress(_socket.AddressFamily);

            if (_socket.Available == 0)
            {
                return;
            }

            int length = _socket.ReceiveFrom(_receiveBuffer, SocketFlags.None, address);

            if (length < 4 || length > _mtu)
            {
                return;
            }

            HandleReceivedData(length, address);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private void HandleReceivedData(int length, SocketAddress address)
    {
        long ticks = DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        if (_listenMode)
        {
            var connection = GetOrCreate(address);
            connection.Receive(_receiveBuffer, length, ticks);
            return;
        }

        if (!address.Equals(_remoteSocketAddress))
        {
            return;
        }

        _connection?.Receive(_receiveBuffer, length, ticks);
    }

    public void Send()
    {
        Span<byte> ackSpan = stackalloc byte[4];
        try
        {
            while (_sendChannel.Reader.TryRead(out var connection))
            {
                if (connection._sendAck)
                {
                    BitConverter.GetBytes(connection._sendAckFragmentNo).CopyTo(ackSpan.Slice(2));
                    connection._sendAck = false;

                    _socket.SendTo(ackSpan, SocketFlags.None, connection.SocketAddress);
                }
                else
                {
                    var span = connection._sendFragment.AsSpan()[..connection._sendFragmentSize];

                    _socket.SendTo(span, SocketFlags.None,
                        connection.SocketAddress);
                }

                connection.SendSemaphore.Release(1);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    // only create in server mode
    private UdpConnection GetOrCreate(SocketAddress socketAddress)
    {
        if (!_connections.TryGetValue(socketAddress, out var connection))
        {
            connection = new UdpConnection(_mtu, socketAddress.Clone(), _sendChannel.Writer);
            connection.OnMessage = OnMessage;

            _connections.TryAdd(connection.SocketAddress, connection);
        }

        return connection;
    }

    public class UdpConnection : IConnection
    {
        // fragment number          2
        // fragement ack number     2
        // message number           2
        // message chunk index      2
        // body size(opt)           4
        // body

        private const int FirstHeadSize = 12;
        private const int NormalHeadSize = 8;

        private readonly int _fragmentSize;
        private readonly SocketAddress _socketAddress;
        private readonly IPEndPoint _remoteEndPoint;
        private readonly ChannelWriter<UdpConnection> _channelWriter;
        internal readonly SemaphoreSlim SendSemaphore = new(1);


        public SocketAddress SocketAddress => _socketAddress;
        public EndPoint RemoteEndPoint => _remoteEndPoint;
        public Action<UdpConnection, byte[], int>? OnMessage;


        private long _lastSnd;
        private long _lastRcv;
        private int _rtt = 60;
        private int _rto = 120;


        #region receive message

        private const int MaxBodySize = 1024 * 1024 * 8;

        private FragmentInfo _curFragment;

        private byte[] _body = ArrayPool<byte>.Shared.Rent(1024);
        private int _fetchedSize;
        private int _bodySize;

        #endregion

        #region send message

        internal byte[] _sendFragment = ArrayPool<byte>.Shared.Rent(1024);
        private byte[]? _sendMessage;
        private ConcurrentQueue<byte[]> _messageQueue = new();


        internal bool _sendAck;
        internal ushort _sendAckFragmentNo;
        internal int _sendFragmentSize;

        private int _sendFragmentBodySize;
        private ushort _sendMessageChunkIndex;
        private ushort _sendMessageNo;
        private ushort _sendFragmentNo;
        private bool _lastChunk;
        private bool _sent = true;

        #endregion

        private int _dropFragmentCount;

        public int DropFragmentCount => _dropFragmentCount;

        internal UdpConnection(int fragmentSize, SocketAddress socketAddress,
            ChannelWriter<UdpConnection> channelWriter)
        {
            _fragmentSize = fragmentSize;
            _socketAddress = socketAddress;
            _remoteEndPoint = (IPEndPoint)EndPointFactory.Create(socketAddress);
            _channelWriter = channelWriter;
        }

        internal void Receive(byte[] buff, int length, long ticks)
        {
            var span = buff.AsSpan(0, length);

            // ack with no body
            if (length == 4)
            {
                HandleAck(BitConverter.ToUInt16(span.Slice(2)), ticks);
                return;
            }

            if (length < 6)
            {
                Helper.Warn($"broken fragment, size: {length}");
                return;
            }

            var incomeFrag = new FragmentInfo
            {
                FrameId = BitConverter.ToUInt16(span),
                AckFrameId = BitConverter.ToUInt16(span.Slice(2)),
                MsgId = BitConverter.ToUInt16(span.Slice(4)),
                MsgChunkIndex = BitConverter.ToUInt16(span.Slice(6)),
            };

            Helper.Log($"[rcv frag] {incomeFrag.FrameId} -> {_curFragment.FrameId} msgId {incomeFrag.MsgId}");

            // received resend fragment
            if (incomeFrag.FrameId == _curFragment.FrameId)
            {
                Helper.Log($"ignore finished resend frag {incomeFrag.FrameId} {_curFragment.FrameId}");
                SendAck(_curFragment.FrameId);
                return;
            }

            // not valid increment id
            if (incomeFrag.FrameId != (ushort)(_curFragment.FrameId + 1))
            {
                Helper.Warn($"invalid fragment {incomeFrag.FrameId} {_curFragment.FrameId}");
                return;
            }

            if (incomeFrag.MessageHead)
            {
                if (length < FirstHeadSize)
                {
                    Helper.Warn($"invalid length, less than 14");
                    return;
                }

                _bodySize = BitConverter.ToInt32(span.Slice(NormalHeadSize));

                // current fragment flags is broken
                if (_bodySize == 0)
                {
                    Helper.Warn("body size broken");
                    return;
                }

                if (_bodySize > MaxBodySize)
                {
                    Helper.Warn("body size reached max body size limit");
                    return;
                }

                // grow body size if coming message size larger than current body size
                if (_bodySize > _body.Length)
                {
                    ArrayPool<byte>.Shared.Return(_body);
                    _body = ArrayPool<byte>.Shared.Rent(_bodySize);
                }

                if (!CopyBody(span.Slice(FirstHeadSize)))
                {
                    return;
                }

                Helper.Log($"new msg {incomeFrag.FrameId} {incomeFrag.MsgId}");

                _curFragment = incomeFrag;
            }
            else
            {
                if (_curFragment.MsgId != incomeFrag.MsgId)
                {
                    Helper.Warn(
                        $"msg id incorrect: {_curFragment.MsgId} {incomeFrag.MsgId} no {incomeFrag.FrameId} chunk {incomeFrag.MsgChunkIndex} len {length}");
                    return;
                }

                if (!CopyBody(span.Slice(NormalHeadSize)))
                {
                    return;
                }

                _curFragment = incomeFrag;
            }

            SendAck(_curFragment.FrameId);

            Helper.Log(
                $"no {_curFragment.FrameId} msg {_curFragment.MsgId} size {_bodySize}/{length} fetched {_fetchedSize} rtt {_rtt} rto {_rto}");

            if (_bodySize == _fetchedSize)
            {
                _fetchedSize = 0;
                OnMessage?.Invoke(this, _body, _bodySize);
            }
        }

        private void SendAck(ushort fragment)
        {
            // TODO: merge ack into message

            Lock();

            _sendAck = true;
            _sendAckFragmentNo = fragment;
            RequestSend();

            Helper.Log($"[send ack] {fragment}");
        }

        private void HandleAck(ushort fragment, long ticks)
        {
            if (_sendFragmentNo != fragment)
            {
                Helper.Log($"rcv invalid ack no {_sendFragmentNo} {fragment}");
                return;
            }

            _lastRcv = ticks;
            _rtt = Math.Max((int)((_rtt + (_lastRcv - _lastSnd) * 7) / 8), 3);
            _rto = _rtt * 2;
            _sent = true;
        }

        private bool CopyBody(Span<byte> source)
        {
            if (source.Length > _body.Length - _fetchedSize)
            {
                Helper.Warn(
                    $"body size error: income {source.Length} body {_body.Length} fetch {_fetchedSize} body def {_bodySize}");
                return false;
            }

            Helper.Log(
                $"copy body income {source.Length} body {_body.Length} fetch {_fetchedSize} body def {_bodySize}");
            source.CopyTo(_body.AsSpan().Slice(_fetchedSize));
            _fetchedSize += source.Length;
            return true;
        }

        public void Send(byte[] data)
        {
            if (data.Length == 0)
            {
                return;
            }

            _messageQueue.Enqueue(data);
        }


        internal void Update(long ticks)
        {
            if (!_sent)
            {
                var latency = ticks - _lastSnd;
                if (latency <= _rto)
                {
                    return;
                }

                Helper.Warn($"resend frag: {_sendFragmentNo} latency {latency} rtt {_rtt} rto {_rto}");
                _rto *= 2;
                _dropFragmentCount++;

                Lock();
                RequestSend();
                UpdateSendTime();

                return;
            }

            if (_sendMessage is null || _lastChunk)
            {
                if (_messageQueue.IsEmpty)
                {
                    return;
                }

                Lock();
                if (!_messageQueue.TryDequeue(out _sendMessage))
                {
                    Unlock();
                    return;
                }

                // reset message state
                _lastChunk = false;
                _sendMessageChunkIndex = 0;
                _sendMessageNo++;
            }
            else
            {
                Lock();
            }

            _sent = false;

            var headSize = _sendMessageChunkIndex == 0 ? FirstHeadSize : NormalHeadSize;
            _sendFragmentBodySize = _fragmentSize - headSize;
            _sendFragmentSize = _fragmentSize;
            var startIndex = _sendMessageChunkIndex * _sendFragmentBodySize;

            // using normal head size bytes step calculation will be missing first chunk extra 4 byte
            if (headSize == NormalHeadSize)
            {
                startIndex -= 4;
            }

            if (startIndex + _sendFragmentBodySize >= _sendMessage!.Length)
            {
                // re-calculate if last frag not full size matched
                _sendFragmentBodySize = _sendMessage.Length - startIndex;
                _sendFragmentSize = _sendFragmentBodySize + headSize;
                _lastChunk = true;
            }


            if (_sendFragmentSize > _sendFragment.Length)
            {
                ArrayPool<byte>.Shared.Return(_sendFragment);
                _sendFragment = ArrayPool<byte>.Shared.Rent(_sendFragmentSize);
            }

            Span<byte> body = _sendFragment.AsSpan().Slice(headSize);
            if (_sendMessageChunkIndex == 0)
            {
                BitConverter.GetBytes(_sendMessage.Length).CopyTo(_sendFragment.AsSpan().Slice(NormalHeadSize));
            }

            var sendFragmentSpan = _sendFragment.AsSpan();
            BitConverter.GetBytes(++_sendFragmentNo).CopyTo(sendFragmentSpan);
            BitConverter.GetBytes((ushort)0).CopyTo(sendFragmentSpan.Slice(2));
            BitConverter.GetBytes(_sendMessageNo).CopyTo(sendFragmentSpan.Slice(4));
            BitConverter.GetBytes(_sendMessageChunkIndex++).CopyTo(sendFragmentSpan.Slice(6));

            _sendMessage.AsSpan().Slice(startIndex, _sendFragmentBodySize).CopyTo(body);

            Helper.Log(
                $"[send frag]: {_sendFragmentNo} msg {_sendMessageNo} size: {_sendFragmentSize} body: {_sendFragmentBodySize} chunk {_sendMessageChunkIndex - 1}");

            RequestSend();
            UpdateSendTime();
        }

        private void UpdateSendTime()
        {
            _lastSnd = DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private void RequestSend()
        {
            _channelWriter.TryWrite(this);
        }

        private void Lock()
        {
            SendSemaphore.Wait();
        }

        private void Unlock()
        {
            SendSemaphore.Release(1);
        }
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
