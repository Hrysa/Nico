using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Nico.Net.Abstractions;

namespace Nico.Net;

public class R2UdpConnection : IConnection
{
    private struct FragmentInfo
    {
        public ushort No;
        public ushort AckNo;
        public ushort MsgNo;
        public ushort MsgChunkIndex;

        public bool MessageHead => MsgChunkIndex is 0;
    }

    // fragment number          2
    // fragement ack number     2
    // message number           2
    // message chunk index      2
    // body size(opt)           4
    // body

    private const int FirstHeadSize = 12;
    private const int NormalHeadSize = 8;

    private readonly int _fragmentSize;
    private readonly SocketAddress _remoteAddress;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly ChannelWriter<R2UdpConnection> _channelWriter;
    private readonly Socket _socket;

    public SocketAddress RemoteAddress => _remoteAddress;
    public EndPoint RemoteEndPoint => _remoteEndPoint;
    public Action<R2UdpConnection, byte[], int>? OnMessage;

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

    internal byte[]? RcvBuffer;
    internal int RcvLength;

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

    public int DropFragmentCount => _dropFragmentCount;
    public int FragmentCount => _fragmentCount;

    Channel<int> _channel = Channel.CreateBounded<int>(2);

    internal R2UdpConnection(int fragmentSize, SocketAddress remoteAddress, Socket socket)
    {
        _fragmentSize = fragmentSize;
        _sendFragment = new byte[fragmentSize];
        _remoteAddress = remoteAddress;
        // _remoteEndPoint = (IPEndPoint)EndPointFactory.Create(socketAddress);
        _socket = socket;
    }

    private void UpdateReceive(long ticks)
    {
        try
        {
            var span = RcvBuffer.AsSpan(0, RcvLength);

            var ackNo = BitConverter.ToUInt16(span.Slice(2));
            // ack with no body
            if (RcvLength == 4)
            {
                HandleAck(ackNo, ticks);
                return;
            }

            _incomeFrag.No = BitConverter.ToUInt16(span);
            _incomeFrag.AckNo = ackNo;
            _incomeFrag.MsgNo = BitConverter.ToUInt16(span.Slice(4));
            _incomeFrag.MsgChunkIndex = BitConverter.ToUInt16(span.Slice(6));

            HandleAck(_incomeFrag.AckNo, ticks);

            Helper.Log($"[rcv frag] {_incomeFrag.No} -> {_curFragment.No} msgId {_incomeFrag.MsgNo}");

            // resend fragment received
            if (_incomeFrag.No == _curFragment.No)
            {
                Helper.Log($"ignore finished resend frag {_incomeFrag.No} {_curFragment.No}");
                SendAck(_curFragment.No);
                return;
            }

            // not valid increment id
            if (_incomeFrag.No != (ushort)(_curFragment.No + 1))
            {
                Helper.Warn($"invalid fragment {_incomeFrag.No} {_curFragment.No}");
                return;
            }

            if (_incomeFrag.MessageHead)
            {
                if (RcvLength < FirstHeadSize)
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

                Helper.Log($"new msg {_incomeFrag.No} {_incomeFrag.MsgNo}");

                _curFragment = _incomeFrag;
            }
            else
            {
                if (_curFragment.MsgNo != _incomeFrag.MsgNo)
                {
                    Helper.Warn(
                        $"msg id incorrect: {_curFragment.MsgNo} {_incomeFrag.MsgNo} no {_incomeFrag.No} chunk {_incomeFrag.MsgChunkIndex} len {RcvLength}");
                    return;
                }

                if (!CopyBody(span.Slice(NormalHeadSize)))
                {
                    return;
                }

                _curFragment = _incomeFrag;
            }

            SendAck(_curFragment.No);

            Helper.Log(
                $"no {_curFragment.No} msg {_curFragment.MsgNo} size {_bodySize}/{RcvLength} fetched {_fetchedSize} rtt {_rtt} rto {_rto}");

            if (_bodySize == _fetchedSize)
            {
                _fetchedSize = 0;
                OnMessage?.Invoke(this, _body, _bodySize);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(RcvBuffer!);
            RcvBuffer = null;
            RcvLength = 0;
        }
    }

    private void SendAck(ushort fragment)
    {
        // TODO: merge ack into message

        _sendAck = true;
        _sendAckFragmentNo = fragment;

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

        _messageQueue.Enqueue(data);
    }

    internal void Update(long ticks)
    {
        UpdateReceive(ticks);
        UpdateSend(ticks);
    }

    private void UpdateSend(long ticks)
    {
        {
            if (!_sent)
            {
                var latency = ticks - _lastSnd;
                if (latency <= _rto)
                {

                    return;
                }

                _rto *= 2;
                _dropFragmentCount++;

                // merge ack fragment into resend data fragment
                if (_sendAck)
                {
                    BitConverter.GetBytes(_sendAckFragmentNo).CopyTo(_sendFragment.AsSpan().Slice(2));
                }

                RequestSend();
                UpdateSendTime();

                return;
            }

            if (_sendMessage is null || _lastChunk)
            {
                if (!_messageQueue.TryDequeue(out _sendMessage))
                {
                    return;
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

            _socket.SendToAsync(bufferMem, SocketFlags.None, RemoteAddress);
        }
        else
        {
            byte[] buffer = GC.AllocateArray<byte>(_sendFragmentSize, pinned: true);
            Memory<byte> bufferMem = buffer.AsMemory();
            _sendFragment.AsSpan()[.._sendFragmentSize].CopyTo(buffer.AsSpan());

            _socket.SendToAsync(bufferMem, SocketFlags.None,
                RemoteAddress);
        }
    }
}
