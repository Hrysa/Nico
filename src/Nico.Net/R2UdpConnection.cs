using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Nico.Core;
using Nico.Net;
using Nico.Net.Abstractions;

struct MessageFragment
{
    public ushort No;
    public ushort AckNo;
    public ushort MsgNo;
    public ushort ChunkIndex;
    public int Opt;

    public override string ToString()
    {
        return $"no {No} ack {AckNo} chunk {ChunkIndex} opt {Opt}";
    }
}

public class R2Connection : IConnection
{
    private readonly int _fragmentSize;
    private readonly SocketAddress _socketAddress;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly ChannelWriter<R2Connection> _channelWriter;
    private readonly Socket _socket;

    public SocketAddress SocketAddress => _socketAddress;
    public EndPoint RemoteEndPoint => _remoteEndPoint;
    public Action<R2Connection, byte[], int>? OnMessage;

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

    internal SPSCQueue<BufferFragment> IncomeBuffer = SPSCQueue<BufferFragment>.Create();

    internal byte[] _rcvBuffer;
    internal int _rcvLength;

    private const int MaxBodySize = 1024 * 1024 * 8;

    // private R2Udp.FragmentInfo _curFragment;
    // private R2Udp.FragmentInfo _incomeFrag;
    private ushort _curRcvNo = 0;

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

    private static readonly IPEndPoint EndPointFactory = new(IPAddress.Any, 0);

    internal R2Connection(int fragmentSize, SocketAddress socketAddress, Socket socket)
    {
        _fragmentSize = fragmentSize;
        _sendFragment = new byte[fragmentSize];
        _socketAddress = socketAddress;
        _remoteEndPoint = (IPEndPoint)EndPointFactory.Create(socketAddress);
        _socket = socket;
    }

    internal unsafe void Receive(IntPtr buffer, int length, long ticks)
    {
            var fragment = (MessageFragment*)buffer;

            var span = MemoryMarshal.CreateSpan(
                ref Unsafe.AddByteOffset(ref Unsafe.AsRef<byte>((void*)buffer), 12), length - 12);

            if (length < sizeof(MessageFragment))
            {
                Helper.Warn($"broken fragment, size: {length}");
                return;
            }

            HandleAck(fragment->AckNo, ticks);

            if (length == sizeof(MessageFragment))
            {
                return;
            }

            Helper.Log($"[rcv frag] {fragment->No}");

            // received resend fragment
            if (fragment->No == _curRcvNo)
            {
                Helper.Log($"ignore finished resend frag {fragment->No}");
                MarkSendAck(_curRcvNo);
                return;
            }

            // not valid increment id
            if (fragment->No != (ushort)(_curRcvNo + 1))
            {
                Helper.Warn($"invalid fragment {fragment->No} > {_curRcvNo}");
                return;
            }

            if (fragment->ChunkIndex == 0)
            {
                _bodySize = fragment->Opt;

                // current fragment flags is broken
                if (_bodySize == 0)
                {
                    Helper.Warn($"body size broken {fragment->ToString()}");
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

                if (!CopyBody(span))
                {
                    return;
                }

                Helper.Log($"new msg, no {fragment->No} msg {fragment->MsgNo} size {fragment->Opt}");

                _curRcvNo = fragment->No;
            }
            else
            {
                // TODO: verify chunk index computed data size equal received data size

                if (!CopyBody(span))
                {
                    return;
                }

                _curRcvNo = fragment->No;
            }

            MarkSendAck(_curRcvNo);

            Helper.Log(
                $"no {_curRcvNo} size {_bodySize}/{length} fetched {_fetchedSize} rtt {_rtt} rto {_rto}");

            if (_bodySize == _fetchedSize)
            {
                _fetchedSize = 0;
                OnMessage?.Invoke(this, _body, _bodySize);
            }
    }

    private void MarkSendAck(ushort fragment)
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

    internal void SendAck()
    {
        if (_sendAck)
        {
            RequestSend(true);
        }
    }

    public void Send(byte[]? data)
    {
        if (data == null || data.Length == 0)
        {
            return;
        }

        SendTime = DateTimeOffset.UtcNow;

        _messageQueue.Enqueue(data);
    }

    public DateTimeOffset SendTime { get; private set; }


    internal void Update(long ticks)
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

    void ParseFragment()
    {
        _sent = false;

        var headSize = 12;
        _sendFragmentBodySize = _fragmentSize - headSize;
        _sendFragmentSize = _fragmentSize;
        var startIndex = _sendMessageChunkIndex * _sendFragmentBodySize;

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
            BitConverter.GetBytes(_sendMessage.Length).CopyTo(_sendFragment.AsSpan().Slice(headSize - 4));
        }

        var sendFragmentSpan = _sendFragment.AsSpan();
        BitConverter.GetBytes(++_sendFragmentNo).CopyTo(sendFragmentSpan);

        if (_sendAck)
        {
            BitConverter.GetBytes(_sendAckFragmentNo).CopyTo(sendFragmentSpan.Slice(2));
            _sendAck = false;
        }

        BitConverter.GetBytes(_sendMessageNo).CopyTo(sendFragmentSpan.Slice(4));
        BitConverter.GetBytes(_sendMessageChunkIndex++).CopyTo(sendFragmentSpan.Slice(6));

        _sendMessage.AsSpan().Slice(startIndex, _sendFragmentBodySize).CopyTo(body);
        Helper.Log(
            $"[send frag]: {GetHashCode()} no {_sendFragmentNo} msg {_sendMessageNo} size: {_sendFragmentSize} body: {_sendFragmentBodySize} chunk {_sendMessageChunkIndex - 1}");
    }

    private void UpdateSendTime()
    {
        _lastSnd = DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    }

    byte[] _ackBuffer = GC.AllocateArray<byte>(12, pinned: true);

    private void RequestSend(bool sendAck = false)
    {
        _fragmentCount++;


        if (sendAck)
        {
            BitConverter.GetBytes(_sendAckFragmentNo).CopyTo(_ackBuffer.AsSpan()[2..]);
            _sendAck = false;

            _socket.SendTo(_ackBuffer, SocketFlags.None, SocketAddress);
        }
        else
        {
            _socket.SendTo(_sendFragment.AsSpan()[.._sendFragmentSize], SocketFlags.None,
                SocketAddress);
        }
    }
}
