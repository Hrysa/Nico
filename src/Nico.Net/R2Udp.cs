using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Nico.Net.Abstractions;

namespace Nico.Net;

public class R2Udp : ISocketReceiver
{
    internal static ConcurrentBag<R2Udp> Group = new();

    private static readonly IPEndPoint EndPointFactory = new(IPAddress.Any, 0);
    public Action<IConnection, byte[], int> OnMessage { get; set; }

    private readonly Socket _socket;

    private readonly byte[] _receiveBuffer;
    private int _mtu = 1440;
    private readonly bool _listenMode;

    #region server

    private Dictionary<SocketAddress, UdpConnection> _connections = new();

    // not thread safe, don't use it
    public Dictionary<SocketAddress, UdpConnection> Connections => _connections;

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

    public UdpConnection Connect(IPEndPoint ipEndPoint)
    {
        if (_connected)
        {
            throw new InvalidOperationException("already connected");
        }

        _remoteEndPoint = ipEndPoint;
        _remoteSocketAddress = ipEndPoint.Serialize();
        _connection = new UdpConnection(_mtu, _remoteSocketAddress, _socket);
        _connected = true;
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        _connection.Update();
        return _connection;
    }

    public async void StartReceive()
    {
        var address = new SocketAddress(_socket.AddressFamily);

        while (true)
        {
            try
            {
                int length = await _socket.ReceiveFromAsync(_receiveBuffer, SocketFlags.None, address);

                if (length < 4 || length > _mtu)
                {
                    continue;
                }

                byte[] buffer = GC.AllocateArray<byte>(length, pinned: true);
                _receiveBuffer.AsSpan().Slice(0, length).CopyTo(buffer);
                HandleReceivedDataAsync(length, address, buffer);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }

    private async void HandleReceivedDataAsync(int length, SocketAddress address, byte[] buffer)
    {
        long ticks = DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        if (_listenMode)
        {
            var fromCreate = GetOrCreate(address, out var connection);

            await Task.Yield();

            if (fromCreate)
            {
                connection.Update();
            }

            connection.Receive(buffer, length, ticks);
            return;
        }

        if (!address.Equals(_remoteSocketAddress))
        {
            return;
        }

        await Task.Yield();
        _connection?.Receive(buffer, length, ticks);
    }

    // only create in server mode
    /// <summary>
    ///
    /// </summary>
    /// <param name="socketAddress"></param>
    /// <param name="connection"></param>
    /// <returns>is new created connection</returns>
    private bool GetOrCreate(SocketAddress socketAddress, out UdpConnection connection)
    {
        if (!_connections.TryGetValue(socketAddress, out connection!))
        {
            connection = new UdpConnection(_mtu, socketAddress.Clone(), _socket);
            connection.OnMessage = OnMessage;

            _connections.TryAdd(connection.SocketAddress, connection);

            return true;
        }

        return false;
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
        private readonly Socket _socket;

        public SocketAddress SocketAddress => _socketAddress;
        public EndPoint RemoteEndPoint => _remoteEndPoint;
        public Action<UdpConnection, byte[], int>? OnMessage;

        public int MinRtt = 5;


        private long _lastSnd;
        private long _lastRcv;
        private int _rtt = 5;
        private int _rto = 10;

        public int RttMin { get; private set; } = 60;
        public int RttMax { get; private set; }
        public float RttMean { get; private set; }
        public float[] Rtts = new float[4000];


        #region receive message

        private const int MaxBodySize = 1024 * 1024 * 8;

        private FragmentInfo _curFragment;
        private FragmentInfo _incomeFrag;

        private byte[] _body = ArrayPool<byte>.Shared.Rent(1024);
        private int _fetchedSize;
        private int _bodySize;

        #endregion

        #region send message

        internal byte[] _sendFragment;
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
        private int _fragmentCount;

        private CancellationTokenSource? _resendCts;

        public int DropFragmentCount => _dropFragmentCount;
        public int FragmentCount => _fragmentCount;

        Channel<int> _channel = Channel.CreateBounded<int>(2);

        internal UdpConnection(int fragmentSize, SocketAddress socketAddress, Socket socket)
        {
            _fragmentSize = fragmentSize;
            _sendFragment = new byte[fragmentSize];
            _socketAddress = socketAddress;
            _remoteEndPoint = (IPEndPoint)EndPointFactory.Create(socketAddress);
            _socket = socket;
        }

        internal void Receive(byte[] buff, int length, long ticks)
        {
            lock (this)
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

                _incomeFrag.FrameId = BitConverter.ToUInt16(span);
                _incomeFrag.AckFrameId = BitConverter.ToUInt16(span.Slice(2));
                _incomeFrag.MsgId = BitConverter.ToUInt16(span.Slice(4));
                _incomeFrag.MsgChunkIndex = BitConverter.ToUInt16(span.Slice(6));

                HandleAck(_incomeFrag.AckFrameId, ticks);

                Helper.Log($"[rcv frag] {_incomeFrag.FrameId} -> {_curFragment.FrameId} msgId {_incomeFrag.MsgId}");

                // received resend fragment
                if (_incomeFrag.FrameId == _curFragment.FrameId)
                {
                    Helper.Log($"ignore finished resend frag {_incomeFrag.FrameId} {_curFragment.FrameId}");
                    SendAck(_curFragment.FrameId);
                    return;
                }

                // not valid increment id
                if (_incomeFrag.FrameId != (ushort)(_curFragment.FrameId + 1))
                {
                    Helper.Warn($"invalid fragment {_incomeFrag.FrameId} {_curFragment.FrameId}");
                    return;
                }

                if (_incomeFrag.MessageHead)
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

                    Helper.Log($"new msg {_incomeFrag.FrameId} {_incomeFrag.MsgId}");

                    _curFragment = _incomeFrag;
                }
                else
                {
                    if (_curFragment.MsgId != _incomeFrag.MsgId)
                    {
                        Helper.Warn(
                            $"msg id incorrect: {_curFragment.MsgId} {_incomeFrag.MsgId} no {_incomeFrag.FrameId} chunk {_incomeFrag.MsgChunkIndex} len {length}");
                        return;
                    }

                    if (!CopyBody(span.Slice(NormalHeadSize)))
                    {
                        return;
                    }

                    _curFragment = _incomeFrag;
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
        }

        private void SendAck(ushort fragment)
        {
            // TODO: merge ack into message

            _sendAck = true;
            _sendAckFragmentNo = fragment;

            Unlock();
            Helper.Log($"[send ack] {fragment}");
        }

        private void Unlock()
        {
            Channel<>
        }

        private void HandleAck(ushort fragment, long ticks)
        {
            if (_sendFragmentNo != fragment)
            {
                Helper.Log($"rcv invalid ack no {_sendFragmentNo} {fragment}");
                return;
            }

            _lastRcv = ticks;
            _rtt = Math.Max((int)((_rtt + (_lastRcv - _lastSnd) * 7) / 8), MinRtt);
            _rto = _rtt * 2;
            _sent = true;

            // if (_rtt > 20)
            // {
            // Helper.Warn($"slow _rtt {_rtt} no {fragment}");
            // }

            RttMin = Math.Min(RttMin, _rtt);
            RttMax = Math.Max(RttMax, _rtt);

            if (RttMean == 0)
            {
                RttMean = _rtt;
            }

            RttMean = (_rtt + RttMean) / 2;

            Unlock();
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

        public void Send(byte[]? data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            Unlock();
            _messageQueue.Enqueue(data);
        }


        internal async void Update()
        {
            while (true)
            {
                var opCount = 0;
                await foreach (var i in _channel.Reader.ReadAllAsync())
                {
                    opCount++;
                }

                long ticks = DateTimeOffset.Now.UtcTicks / TimeSpan.TicksPerMillisecond;

                if (!_sent)
                {
                    if (_sendAck)
                    {
                        RequestSend(true);
                        continue;
                    }

                    var latency = ticks - _lastSnd;
                    var lastSnd = _lastSnd;
                    if (latency <= _rto)
                    {
                        _resendCts = new CancellationTokenSource();
                        _ = Task.Delay((int)(_rto - latency), _resendCts.Token).ContinueWith(_ =>
                            {
                                if (lastSnd == _lastSnd)
                                {
                                    Console.WriteLine("timeout unlock");
                                    Unlock();
                                }
                            }, _resendCts.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    // Helper.Log($"resend frag: {_sendFragmentNo} latency {latency} rtt {_rtt} rto {_rto}");
                    // if (_dropFragmentCount > 0 && _dropFragmentCount % 100 == 0)
                    // {
                    //     Helper.Warn($"resend frag count: {_dropFragmentCount}/{_fragmentCount} id: {GetHashCode()}");
                    // }

                    _rto *= 2;
                    _dropFragmentCount++;

                    RequestSend();
                    UpdateSendTime();

                    continue;
                }

                if (_resendCts?.Token.CanBeCanceled is true)
                {
                    _resendCts.Cancel();
                }

                if (_sendMessage is null || _lastChunk)
                {
                    if (!_messageQueue.TryDequeue(out _sendMessage))
                    {
                        continue;
                    }

                    // reset message state
                    _lastChunk = false;
                    _sendMessageChunkIndex = 0;
                    _sendMessageNo++;
                }

                ParseFragment();
                RequestSend();
                UpdateSendTime();
            }
        }

        void ParseFragment()
        {
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

            Span<byte> body = _sendFragment.AsSpan().Slice(headSize);
            if (_sendMessageChunkIndex == 0)
            {
                BitConverter.GetBytes(_sendMessage.Length).CopyTo(_sendFragment.AsSpan().Slice(NormalHeadSize));
            }

            var sendFragmentSpan = _sendFragment.AsSpan();
            BitConverter.GetBytes(++_sendFragmentNo).CopyTo(sendFragmentSpan);
            BitConverter.GetBytes(_sendAckFragmentNo).CopyTo(sendFragmentSpan.Slice(2));
            BitConverter.GetBytes(_sendMessageNo).CopyTo(sendFragmentSpan.Slice(4));
            BitConverter.GetBytes(_sendMessageChunkIndex++).CopyTo(sendFragmentSpan.Slice(6));

            _sendMessage.AsSpan().Slice(startIndex, _sendFragmentBodySize).CopyTo(body);
            Helper.Log(
                $"[send frag]: {_sendFragmentNo} msg {_sendMessageNo} size: {_sendFragmentSize} body: {_sendFragmentBodySize} chunk {_sendMessageChunkIndex - 1}");
        }

        private void UpdateSendTime()
        {
            _lastSnd = DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private void RequestSend(bool sendAck = false)
        {
            _fragmentCount++;


            if (sendAck)
            {
                byte[] buffer = GC.AllocateArray<byte>(4, pinned: true);
                Memory<byte> bufferMem = buffer.AsMemory();
                BitConverter.GetBytes(_sendAckFragmentNo).CopyTo(buffer.AsSpan().Slice(2));
                _sendAck = false;

                _socket.SendToAsync(bufferMem, SocketFlags.None, SocketAddress);
            }
            else
            {
                byte[] buffer = GC.AllocateArray<byte>(_sendFragmentSize, pinned: true);
                Memory<byte> bufferMem = buffer.AsMemory();
                _sendFragment.AsSpan()[.._sendFragmentSize].CopyTo(buffer.AsSpan());

                _socket.SendToAsync(bufferMem, SocketFlags.None,
                    SocketAddress);
            }
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
