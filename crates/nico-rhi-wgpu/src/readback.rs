//! Nonblocking, single-use staging maps for bounded diagnostic payloads.
use super::*;
use std::{
    ops::Range,
    sync::mpsc::{Receiver, TryRecvError},
};

pub(super) struct Ticket {
    device: wgpu::Device,
    buffer: wgpu::Buffer,
    range: Range<u64>,
    receiver: Option<Receiver<Result<(), wgpu::BufferAsyncError>>>,
}
pub(super) fn begin(
    device: &WgpuDevice,
    buffer: &WgpuBuffer,
    range: Range<u64>,
) -> Result<Box<dyn BufferReadback>, RhiError> {
    device.check_failure()?;
    let length = range.end.saturating_sub(range.start);
    if length == 0
        || length > MAX_BUFFER_READBACK_BYTES
        || !length.is_multiple_of(4)
        || !range.start.is_multiple_of(8)
        || range.end > buffer.0.size()
        || !buffer.0.usage().contains(wgpu::BufferUsages::MAP_READ)
    {
        return Err(RhiError::new(
            RhiErrorKind::InvalidDescriptor,
            "invalid staging readback range or usage",
        ));
    }
    let (sender, receiver) = std::sync::mpsc::sync_channel(1);
    buffer
        .0
        .slice(range.clone())
        .map_async(wgpu::MapMode::Read, move |result| {
            let _ = sender.try_send(result);
        });
    Ok(Box::new(Ticket {
        device: device.inner.clone(),
        buffer: buffer.0.clone(),
        range,
        receiver: Some(receiver),
    }))
}
impl BufferReadback for Ticket {
    fn poll(&mut self) -> Result<Option<Vec<u8>>, RhiError> {
        let Some(receiver) = &self.receiver else {
            return Err(RhiError::new(
                RhiErrorKind::InvalidDescriptor,
                "readback already consumed",
            ));
        };
        self.device
            .poll(wgpu::PollType::Poll)
            .map_err(|error| RhiError::new(RhiErrorKind::Backend, error.to_string()))?;
        let result = match receiver.try_recv() {
            Err(TryRecvError::Empty) => return Ok(None),
            Err(TryRecvError::Disconnected) => Err(RhiError::new(
                RhiErrorKind::Backend,
                "readback callback disconnected",
            )),
            Ok(result) => {
                result.map_err(|error| RhiError::new(RhiErrorKind::Backend, error.to_string()))
            }
        };
        self.receiver = None;
        let bytes = result.and_then(|()| {
            self.buffer
                .slice(self.range.clone())
                .get_mapped_range()
                .map(|data| data.to_vec())
                .map_err(|error| RhiError::new(RhiErrorKind::Backend, error.to_string()))
        });
        self.buffer.unmap();
        bytes.map(Some)
    }
}
impl Drop for Ticket {
    fn drop(&mut self) {
        if self.receiver.is_some() {
            self.buffer.unmap();
        }
    }
}
