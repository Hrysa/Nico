/// Backend-independent storage for one straight-alpha texture mip level.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextureEncoding {
    Rgba8,
    Bc3,
}

/// Color-space interpretation belongs to the material slot or canvas.
#[derive(Debug)]
pub struct Texture {
    width: u32,
    height: u32,
    encoding: TextureEncoding,
    data: Vec<u8>,
    #[cfg(feature = "texture-compression")]
    decoded: std::sync::OnceLock<Vec<u8>>,
}
impl Texture {
    /// Creates validated procedural content without a file decoder.
    #[must_use]
    pub fn rgba8(width: u32, height: u32, pixels: Vec<u8>) -> Option<Self> {
        Self::from_encoded(width, height, TextureEncoding::Rgba8, pixels)
    }
    /// Accepts tightly packed RGBA8 or 16-byte BC3 blocks. BC3 requires the
    /// texture-compression feature and dimensions divisible by four.
    #[must_use]
    pub fn from_encoded(
        width: u32,
        height: u32,
        encoding: TextureEncoding,
        data: Vec<u8>,
    ) -> Option<Self> {
        let rgba_size = (width as usize)
            .checked_mul(height as usize)?
            .checked_mul(4)?;
        if width == 0 || height == 0 {
            return None;
        }
        let expected = match encoding {
            TextureEncoding::Rgba8 => rgba_size,
            TextureEncoding::Bc3 => {
                if !cfg!(feature = "texture-compression")
                    || !width.is_multiple_of(4)
                    || !height.is_multiple_of(4)
                {
                    return None;
                }
                rgba_size / 4
            }
        };
        (data.len() == expected).then_some(Self {
            width,
            height,
            encoding,
            data,
            #[cfg(feature = "texture-compression")]
            decoded: std::sync::OnceLock::new(),
        })
    }
    #[must_use]
    pub const fn width(&self) -> u32 {
        self.width
    }
    #[must_use]
    pub const fn height(&self) -> u32 {
        self.height
    }
    #[must_use]
    pub const fn encoding(&self) -> TextureEncoding {
        self.encoding
    }
    /// GPU-ready bytes; reading these never decompresses the texture.
    #[must_use]
    pub fn encoded_bytes(&self) -> &[u8] {
        &self.data
    }
    /// RGBA size used for conservative decode budgets, without allocating.
    #[must_use]
    pub fn decoded_byte_len(&self) -> usize {
        self.width as usize * self.height as usize * 4
    }
    /// CPU access and unsupported-device fallback. Compressed textures decode
    /// lazily once; this does not recover the original pixels of a lossy import.
    #[must_use]
    pub fn pixels(&self) -> &[u8] {
        #[cfg(feature = "texture-compression")]
        if self.encoding == TextureEncoding::Bc3 {
            return self.decoded.get_or_init(|| {
                let mut pixels = vec![0; self.decoded_byte_len()];
                texpresso::Format::Bc3.decompress(
                    &self.data,
                    self.width as usize,
                    self.height as usize,
                    &mut pixels,
                );
                pixels
            });
        }
        &self.data
    }
    /// Cook block-aligned images to BC3. Small/unaligned images remain RGBA8.
    /// The callback runs between block rows for cancellation/progress checks.
    #[cfg(feature = "texture-compression")]
    pub fn compress_bc3<E>(self, mut checkpoint: impl FnMut() -> Result<(), E>) -> Result<Self, E> {
        checkpoint()?;
        if self.encoding == TextureEncoding::Bc3
            || self.width < 4
            || self.height < 4
            || !self.width.is_multiple_of(4)
            || !self.height.is_multiple_of(4)
        {
            return Ok(self);
        }
        let mut data = vec![0; self.decoded_byte_len() / 4];
        let width = self.width as usize;
        for (source, destination) in self
            .data
            .chunks_exact(width * 16)
            .zip(data.chunks_exact_mut(width * 4))
        {
            checkpoint()?;
            texpresso::Format::Bc3.compress(
                source,
                width,
                4,
                texpresso::Params {
                    algorithm: texpresso::Algorithm::RangeFit,
                    weights: texpresso::COLOUR_WEIGHTS_UNIFORM,
                    ..Default::default()
                },
                destination,
            );
        }
        checkpoint()?;
        Ok(Self::from_encoded(self.width, self.height, TextureEncoding::Bc3, data).unwrap())
    }
}

#[cfg(all(test, feature = "texture-compression"))]
mod tests {
    use super::*;
    #[test]
    fn blocks_stay_compressed_until_cpu_access_and_preserve_alpha() {
        let rgba = [255, 0, 0, 128].repeat(64);
        let texture = Texture::rgba8(8, 8, rgba.clone())
            .unwrap()
            .compress_bc3(|| Ok::<_, ()>(()))
            .unwrap();
        assert_eq!(texture.encoding(), TextureEncoding::Bc3);
        assert_eq!(texture.encoded_bytes().len(), 64);
        assert_eq!(texture.decoded_byte_len(), 256);
        assert!(texture.decoded.get().is_none());
        assert_eq!(texture.pixels(), rgba);
        assert!(std::ptr::eq(texture.pixels(), texture.pixels()));
    }
    #[test]
    fn odd_images_remain_exact_and_compression_can_be_cancelled() {
        let rgba = vec![127; 5 * 7 * 4];
        let texture = Texture::rgba8(5, 7, rgba.clone())
            .unwrap()
            .compress_bc3(|| Ok::<_, ()>(()))
            .unwrap();
        assert_eq!(texture.encoding(), TextureEncoding::Rgba8);
        assert_eq!(texture.pixels(), rgba);
        let mut checks = 0;
        assert!(
            Texture::rgba8(8, 8, vec![0; 256])
                .unwrap()
                .compress_bc3(|| {
                    checks += 1;
                    if checks == 3 { Err(()) } else { Ok(()) }
                })
                .is_err()
        );
        assert_eq!(checks, 3);
        for (w, h, n) in [
            (0, 4, 0),
            (5, 4, 20),
            (4, 4, 15),
            (4, 4, 17),
            (u32::MAX, u32::MAX, 0),
        ] {
            assert!(Texture::from_encoded(w, h, TextureEncoding::Bc3, vec![0; n]).is_none());
        }
    }
}
