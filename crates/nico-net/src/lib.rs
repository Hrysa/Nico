//! Bounded, nonblocking framed TCP transport for native game hosts.
//! The caller owns polling and connection lifetime. This crate has no game,
//! runtime, world or bridge dependency. It never starts a service thread.
use std::{
    collections::{BTreeMap, VecDeque},
    io::{self, Read, Write},
    net::{SocketAddr, TcpListener, TcpStream},
    time::Duration,
};
pub type ConnectionId = u64;
pub const MAX_FRAME: usize = 256 * 1024;
pub const MAX_PENDING_BYTES: usize = 2 * MAX_FRAME;
const IO_BUDGET: usize = 64 * 1024;
const MESSAGE_BUDGET: usize = 64;

/// One bounded background connection attempt. Poll from the host; dropping waits
/// for the configured timeout at most. Worker threads never access game state.
pub struct Connector {
    result: std::sync::Mutex<std::sync::mpsc::Receiver<io::Result<Connection>>>,
    worker: Option<std::thread::JoinHandle<()>>,
}
impl Connector {
    pub fn start(address: SocketAddr) -> io::Result<Self> {
        let (sender, receiver) = std::sync::mpsc::sync_channel(1);
        let worker = std::thread::Builder::new()
            .name("nico-connect".into())
            .spawn(move || {
                let result = Connection::connect(address, Duration::from_millis(250));
                let _ = sender.send(result);
            })?;
        Ok(Self {
            result: std::sync::Mutex::new(receiver),
            worker: Some(worker),
        })
    }
    pub fn poll(&mut self) -> Option<io::Result<Connection>> {
        match self.result.lock().unwrap().try_recv() {
            Ok(result) => Some(result),
            Err(std::sync::mpsc::TryRecvError::Empty) => None,
            Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                Some(Err(io::Error::other("connection worker ended")))
            }
        }
    }
}
impl Drop for Connector {
    fn drop(&mut self) {
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

/// An owned connection. Queueing succeeds before delivery; poll flushes bounded
/// bytes and returns owned received frames. Any poll error ends this connection.
pub struct Connection {
    socket: TcpStream,
    incoming: Vec<u8>,
    outgoing: VecDeque<Vec<u8>>,
    offset: usize,
    pending: usize,
    read_closed: bool,
}
impl Connection {
    pub fn connect(address: SocketAddr, timeout: Duration) -> io::Result<Self> {
        Self::from_stream(TcpStream::connect_timeout(&address, timeout)?)
    }
    fn from_stream(socket: TcpStream) -> io::Result<Self> {
        socket.set_nonblocking(true)?;
        socket.set_nodelay(true)?;
        Ok(Self {
            socket,
            incoming: Vec::new(),
            outgoing: VecDeque::new(),
            offset: 0,
            pending: 0,
            read_closed: false,
        })
    }
    pub fn pending_bytes(&self) -> usize {
        self.pending
    }
    pub fn send(&mut self, message: &[u8]) -> io::Result<()> {
        if message.is_empty() || message.len() > MAX_FRAME {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "frame length outside bounds",
            ));
        }
        if self.pending + message.len() + 4 > MAX_PENDING_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::WouldBlock,
                "outgoing queue full",
            ));
        }
        let mut frame = Vec::with_capacity(message.len() + 4);
        frame.extend_from_slice(&(message.len() as u32).to_be_bytes());
        frame.extend_from_slice(message);
        self.pending += frame.len();
        self.outgoing.push_back(frame);
        Ok(())
    }
    pub fn poll(&mut self) -> io::Result<Vec<Vec<u8>>> {
        if self.read_closed {
            return Err(io::Error::new(
                io::ErrorKind::ConnectionAborted,
                "peer disconnected",
            ));
        }
        let mut budget = IO_BUDGET;
        while budget > 0 {
            let Some(frame) = self.outgoing.front() else {
                break;
            };
            let end = frame.len().min(self.offset + budget);
            match self.socket.write(&frame[self.offset..end]) {
                Ok(0) => {
                    return Err(io::Error::new(
                        io::ErrorKind::WriteZero,
                        "peer stopped accepting bytes",
                    ));
                }
                Ok(n) => {
                    self.offset += n;
                    self.pending -= n;
                    budget -= n;
                    if self.offset == frame.len() {
                        self.outgoing.pop_front();
                        self.offset = 0;
                    }
                }
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
                Err(e) => return Err(e),
            }
        }
        let mut frames = Vec::new();
        decode(&mut self.incoming, &mut frames)?;
        let mut buffer = [0u8; 8192];
        let mut read = 0;
        while read < IO_BUDGET && frames.len() < MESSAGE_BUDGET {
            match self.socket.read(&mut buffer) {
                Ok(0) => {
                    self.read_closed = true;
                    if frames.is_empty() {
                        return Err(io::Error::new(
                            io::ErrorKind::ConnectionAborted,
                            "peer disconnected",
                        ));
                    }
                    break;
                }
                Ok(n) => {
                    read += n;
                    self.incoming.extend_from_slice(&buffer[..n]);
                    decode(&mut self.incoming, &mut frames)?;
                }
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
                Err(e) => return Err(e),
            }
        }
        Ok(frames)
    }
}
fn decode(bytes: &mut Vec<u8>, frames: &mut Vec<Vec<u8>>) -> io::Result<()> {
    let mut consumed = 0;
    while bytes.len() - consumed >= 4 && frames.len() < MESSAGE_BUDGET {
        let len = u32::from_be_bytes(bytes[consumed..consumed + 4].try_into().unwrap()) as usize;
        if len == 0 || len > MAX_FRAME {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid frame length",
            ));
        }
        if bytes.len() - consumed < 4 + len {
            break;
        }
        frames.push(bytes[consumed + 4..consumed + 4 + len].to_vec());
        consumed += 4 + len;
    }
    bytes.drain(..consumed);
    if bytes.len() > MAX_FRAME + 4 + 8192 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "incoming buffer exceeded",
        ));
    }
    Ok(())
}
#[derive(Debug)]
pub enum Event {
    Connected {
        connection: ConnectionId,
        address: SocketAddr,
    },
    Message {
        connection: ConnectionId,
        bytes: Vec<u8>,
    },
    Disconnected {
        connection: ConnectionId,
        reason: String,
    },
}
/// Listener and peers owned by one host. Slow/broken peers are disconnected;
/// their failure cannot stop other sessions. Dropping the server closes sockets.
pub struct Server {
    listener: TcpListener,
    peers: BTreeMap<ConnectionId, Connection>,
    next_id: ConnectionId,
    capacity: usize,
}
impl Server {
    pub fn bind(address: SocketAddr, capacity: usize) -> io::Result<Self> {
        if capacity == 0 || capacity > 1024 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid peer capacity",
            ));
        }
        let listener = TcpListener::bind(address)?;
        listener.set_nonblocking(true)?;
        Ok(Self {
            listener,
            peers: BTreeMap::new(),
            next_id: 1,
            capacity,
        })
    }
    pub fn local_addr(&self) -> io::Result<SocketAddr> {
        self.listener.local_addr()
    }
    pub fn connections(&self) -> impl Iterator<Item = ConnectionId> + '_ {
        self.peers.keys().copied()
    }
    pub fn disconnect(&mut self, id: ConnectionId) {
        self.peers.remove(&id);
    }
    pub fn send(&mut self, id: ConnectionId, bytes: &[u8]) -> io::Result<()> {
        self.peers
            .get_mut(&id)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "unknown connection"))?
            .send(bytes)
    }
    pub fn poll(&mut self) -> io::Result<Vec<Event>> {
        let mut events = Vec::new();
        for _ in 0..16 {
            match self.listener.accept() {
                Ok((socket, address)) => {
                    if self.peers.len() >= self.capacity {
                        continue;
                    }
                    let Some(next) = self.next_id.checked_add(1) else {
                        continue;
                    };
                    let connection = self.next_id;
                    self.next_id = next;
                    if let Ok(peer) = Connection::from_stream(socket) {
                        self.peers.insert(connection, peer);
                        events.push(Event::Connected {
                            connection,
                            address,
                        });
                    }
                }
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
                Err(e) => return Err(e),
            }
        }
        let mut closed = Vec::new();
        for (&connection, peer) in &mut self.peers {
            match peer.poll() {
                Ok(frames) => events.extend(
                    frames
                        .into_iter()
                        .map(|bytes| Event::Message { connection, bytes }),
                ),
                Err(e) => {
                    closed.push(connection);
                    events.push(Event::Disconnected {
                        connection,
                        reason: e.to_string(),
                    });
                }
            }
        }
        for id in closed {
            self.peers.remove(&id);
        }
        Ok(events)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fragmented_and_coalesced_frames_preserve_boundaries_and_reject_oversize() {
        let mut bytes = vec![0, 0];
        let mut frames = vec![];
        decode(&mut bytes, &mut frames).unwrap();
        assert!(frames.is_empty());
        bytes.extend_from_slice(&[0, 3, 1]);
        decode(&mut bytes, &mut frames).unwrap();
        assert!(frames.is_empty());
        bytes.extend_from_slice(&[2, 3, 0, 0, 0, 1, 9]);
        decode(&mut bytes, &mut frames).unwrap();
        assert_eq!(frames, vec![vec![1, 2, 3], vec![9]]);
        assert!(bytes.is_empty());
        let mut oversize = ((MAX_FRAME + 1) as u32).to_be_bytes().to_vec();
        assert!(decode(&mut oversize, &mut vec![]).is_err());
    }
    #[test]
    fn two_connections_exchange_messages_and_disconnect_independently() {
        let mut server = Server::bind("127.0.0.1:0".parse().unwrap(), 2).unwrap();
        let mut a =
            Connection::connect(server.local_addr().unwrap(), Duration::from_secs(1)).unwrap();
        let mut b =
            Connection::connect(server.local_addr().unwrap(), Duration::from_secs(1)).unwrap();
        a.send(b"alice").unwrap();
        b.send(b"bob").unwrap();
        let mut messages = vec![];
        let deadline = std::time::Instant::now() + Duration::from_secs(2);
        while messages.len() < 2 && std::time::Instant::now() < deadline {
            a.poll().unwrap();
            b.poll().unwrap();
            for e in server.poll().unwrap() {
                if let Event::Message { connection, bytes } = e {
                    messages.push((connection, bytes));
                }
            }
            std::thread::sleep(Duration::from_millis(1));
        }
        assert_eq!(messages.len(), 2);
        assert_ne!(messages[0].0, messages[1].0);
        drop(a);
        let mut disconnected = false;
        while !disconnected && std::time::Instant::now() < deadline {
            disconnected = server
                .poll()
                .unwrap()
                .iter()
                .any(|e| matches!(e, Event::Disconnected { .. }));
            std::thread::sleep(Duration::from_millis(1));
        }
        assert!(disconnected);
        assert_eq!(server.connections().count(), 1);
        b.poll().unwrap();
    }
    #[test]
    fn complete_final_frame_is_delivered_before_disconnect() {
        let mut server = Server::bind("127.0.0.1:0".parse().unwrap(), 1).unwrap();
        let mut peer =
            Connection::connect(server.local_addr().unwrap(), Duration::from_secs(1)).unwrap();
        peer.send(b"last message").unwrap();
        peer.poll().unwrap();
        drop(peer);
        let deadline = std::time::Instant::now() + Duration::from_secs(2);
        let mut delivered = false;
        let mut disconnected = false;
        while !disconnected && std::time::Instant::now() < deadline {
            for event in server.poll().unwrap() {
                match event {
                    Event::Message { bytes, .. } => {
                        assert_eq!(bytes, b"last message");
                        delivered = true;
                    }
                    Event::Disconnected { .. } => {
                        assert!(delivered);
                        disconnected = true;
                    }
                    _ => {}
                }
            }
            std::thread::sleep(Duration::from_millis(1));
        }
        assert!(delivered && disconnected);
    }
    #[test]
    fn outgoing_memory_is_bounded_and_invalid_frames_do_not_queue() {
        let server = Server::bind("127.0.0.1:0".parse().unwrap(), 1).unwrap();
        let mut peer =
            Connection::connect(server.local_addr().unwrap(), Duration::from_secs(1)).unwrap();
        assert!(peer.send(&[]).is_err());
        peer.send(&vec![1; MAX_FRAME]).unwrap();
        assert_eq!(
            peer.send(&vec![2; MAX_FRAME]).unwrap_err().kind(),
            io::ErrorKind::WouldBlock
        );
        assert_eq!(peer.pending_bytes(), MAX_FRAME + 4);
    }
}
