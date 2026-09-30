//! Shaped TrueType text with shared glyph textures. Outputs ordinary UI quads.
use cosmic_text::{
    Attrs, Buffer, CacheKey, Family, FontSystem, Metrics, Shaping, SwashCache, SwashContent, Wrap,
};
use nico_assets::Texture;
use nico_presentation::{Quad, UiScene};
use std::{
    collections::{HashMap, VecDeque},
    io,
    sync::{Arc, Mutex},
};

const MAX_FONT_BYTES: usize = 16 * 1024 * 1024;
const MAX_TEXT_BYTES: usize = 4096;
const MAX_LAYOUTS: usize = 64;
const MAX_GLYPHS: usize = 1024;
const MAX_GLYPH_BYTES: usize = 16 * 1024 * 1024;

/// Shared font and glyph cache. Clones retain prepared text across scene changes.
/// Fonts come from supplied bytes; this type never searches installed system fonts.
#[derive(Clone)]
pub struct TextFont {
    state: Arc<Mutex<State>>,
    raster_scale: f32,
}
struct State {
    fonts: FontSystem,
    rasterizer: SwashCache,
    family: String,
    glyphs: HashMap<CacheKey, Arc<Glyph>>,
    glyph_bytes: usize,
    layouts: VecDeque<CachedLayout>,
}
struct Glyph {
    texture: Arc<Texture>,
    left: i32,
    top: i32,
}
struct CachedLayout {
    text: String,
    size: f32,
    scale: f32,
    layout: Arc<Layout>,
}
#[derive(Default)]
struct Layout {
    extent: [f32; 2],
    quads: Vec<Quad>,
}
impl TextFont {
    /// Load one font. Invalid bytes return an error before text rendering starts.
    pub fn from_bytes(bytes: &[u8]) -> io::Result<Self> {
        Self::from_fonts(&[bytes])
    }
    /// Load a primary font followed by optional fallback fonts for other scripts.
    /// Supplied font bytes have a combined 16 MiB limit. Missing glyphs use the font's replacement glyph.
    pub fn from_fonts(sources: &[&[u8]]) -> io::Result<Self> {
        let length = sources
            .iter()
            .try_fold(0_usize, |sum, bytes| sum.checked_add(bytes.len()));
        if sources.is_empty() || length.is_none_or(|v| v > MAX_FONT_BYTES) {
            return Err(io::Error::other("font data is empty or exceeds 16 MiB"));
        }
        let mut database = cosmic_text::fontdb::Database::new();
        let mut family = None;
        for bytes in sources {
            let before = database.faces().count();
            database.load_font_data(bytes.to_vec());
            if database.faces().count() == before {
                return Err(io::Error::other("invalid font data"));
            }
            if family.is_none() {
                family = database
                    .faces()
                    .next()
                    .and_then(|face| face.families.first())
                    .map(|v| v.0.clone());
            }
        }
        let family = family.ok_or_else(|| io::Error::other("font has no family name"))?;
        database.set_sans_serif_family(&family);
        Ok(Self {
            state: Arc::new(Mutex::new(State {
                fonts: FontSystem::new_with_locale_and_db("en-US".into(), database),
                rasterizer: SwashCache::new(),
                family,
                glyphs: HashMap::new(),
                glyph_bytes: 0,
                layouts: VecDeque::new(),
            })),
            raster_scale: 1.,
        })
    }
    /// Rasterize at physical pixel density while keeping layout in logical pixels.
    /// Invalid density uses 1. Supported densities range from 0.25 through 4.
    pub fn set_raster_scale(&mut self, scale: f32) {
        self.raster_scale = if scale.is_finite() && (0.25..=4.).contains(&scale) {
            scale
        } else {
            1.
        };
    }
    /// Logical line extent, including spaces and blank lines. Font size is in logical pixels.
    pub fn measure(&self, text: &str, size: f32) -> [f32; 2] {
        self.layout(text, size).extent
    }
    /// Append shaped glyph quads. Color and position do not invalidate cached glyph textures.
    /// Empty or oversized text and invalid sizes draw nothing. Supported font sizes range from 1 through 256.
    pub fn draw(
        &self,
        output: &mut Vec<Quad>,
        text: &str,
        origin: [f32; 2],
        size: f32,
        color: [f32; 4],
    ) {
        if !origin.iter().chain(&color).all(|v| v.is_finite()) {
            return;
        }
        let layout = self.layout(text, size);
        output.extend(layout.quads.iter().cloned().map(|mut quad| {
            quad.center[0] += origin[0];
            quad.center[1] += origin[1];
            quad.color = color;
            quad
        }));
    }
    fn layout(&self, text: &str, size: f32) -> Arc<Layout> {
        if text.is_empty()
            || text.len() > MAX_TEXT_BYTES
            || !size.is_finite()
            || !(1. ..=256.).contains(&size)
        {
            return Arc::new(Layout::default());
        }
        let mut state = self.state.lock().expect("text cache lock");
        if let Some(index) = state
            .layouts
            .iter()
            .position(|v| v.text == text && v.size == size && v.scale == self.raster_scale)
        {
            let entry = state.layouts.remove(index).unwrap();
            let result = entry.layout.clone();
            state.layouts.push_back(entry);
            return result;
        }
        let mut buffer = Buffer::new(&mut state.fonts, Metrics::new(size, size * 1.2));
        buffer.set_wrap(Wrap::None);
        buffer.set_text(
            text,
            &Attrs::new().family(Family::Name(&state.family)),
            Shaping::Advanced,
            None,
        );
        buffer.shape_until_scroll(&mut state.fonts, false);
        let mut layout = Layout::default();
        for run in buffer.layout_runs() {
            layout.extent[0] = layout.extent[0].max(run.line_w);
            layout.extent[1] = layout.extent[1].max(run.line_top + run.line_height);
            for glyph in run.glyphs {
                let physical =
                    glyph.physical((0., run.line_y * self.raster_scale), self.raster_scale);
                let Some(image) = state.glyph(physical.cache_key) else {
                    continue;
                };
                let size = [
                    image.texture.width() as f32 / self.raster_scale,
                    image.texture.height() as f32 / self.raster_scale,
                ];
                layout.quads.push(Quad {
                    center: [
                        (physical.x + image.left) as f32 / self.raster_scale + size[0] / 2.,
                        (physical.y - image.top) as f32 / self.raster_scale + size[1] / 2.,
                    ],
                    size,
                    color: [1.; 4],
                    texture: Some(image.texture.clone()),
                });
            }
        }
        let layout = Arc::new(layout);
        if state.layouts.len() == MAX_LAYOUTS {
            state.layouts.pop_front();
        }
        state.layouts.push_back(CachedLayout {
            text: text.into(),
            size,
            scale: self.raster_scale,
            layout: layout.clone(),
        });
        layout
    }
}
impl State {
    fn glyph(&mut self, key: CacheKey) -> Option<Arc<Glyph>> {
        if let Some(value) = self.glyphs.get(&key) {
            return Some(value.clone());
        }
        let image = self.rasterizer.get_image_uncached(&mut self.fonts, key)?;
        let length = image.placement.width as usize * image.placement.height as usize * 4;
        if length == 0 || length > MAX_GLYPH_BYTES {
            return None;
        }
        let mut pixels = Vec::with_capacity(length);
        match image.content {
            SwashContent::Mask => {
                for alpha in image.data {
                    pixels.extend_from_slice(&[255, 255, 255, alpha]);
                }
            }
            SwashContent::Color => pixels = image.data,
            SwashContent::SubpixelMask => {
                for pixel in image.data.chunks_exact(4) {
                    pixels.extend_from_slice(&[255, 255, 255, *pixel[..3].iter().max().unwrap()]);
                }
            }
        }
        if self.glyphs.len() >= MAX_GLYPHS || self.glyph_bytes + length > MAX_GLYPH_BYTES {
            self.glyphs.clear();
            self.layouts.clear();
            self.glyph_bytes = 0;
        }
        let value = Arc::new(Glyph {
            texture: Arc::new(Texture::rgba8(
                image.placement.width,
                image.placement.height,
                pixels,
            )?),
            left: image.placement.left,
            top: image.placement.top,
        });
        self.glyph_bytes += length;
        self.glyphs.insert(key, value.clone());
        Some(value)
    }
}
/// Add a solid HUD rectangle using a caller-owned white texture.
pub fn rectangle(
    scene: &mut UiScene,
    origin: [f32; 2],
    size: [f32; 2],
    color: [f32; 4],
    white: Arc<Texture>,
) {
    scene.quads.push(Quad {
        center: [origin[0] + size[0] / 2., origin[1] + size[1] / 2.],
        size,
        color,
        texture: Some(white),
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    const FONT: &[u8] =
        include_bytes!("../../../games/arena-arpg/assets/presentation/fonts/NotoSans.ttf");

    fn font() -> TextFont {
        TextFont::from_bytes(FONT).unwrap()
    }

    fn draw(font: &TextFont, text: &str, size: f32) -> Vec<Quad> {
        let mut quads = Vec::new();
        font.draw(&mut quads, text, [0.; 2], size, [1.; 4]);
        quads
    }

    #[test]
    fn invalid_font_sources_fail_before_rendering() {
        assert!(TextFont::from_fonts(&[]).is_err());
        assert!(TextFont::from_bytes(&[]).is_err());
        assert!(TextFont::from_bytes(b"not a font").is_err());
        assert!(TextFont::from_fonts(&[FONT, b"invalid fallback"]).is_err());
        let oversized = vec![FONT; MAX_FONT_BYTES / FONT.len() + 1];
        assert!(TextFont::from_fonts(&oversized).is_err());
    }

    #[test]
    fn shaping_preserves_case_proportional_width_and_supported_scripts() {
        let font = font();
        assert!(font.measure("WWW", 20.)[0] > font.measure("iii", 20.)[0] * 2.);
        let upper = draw(&font, "A", 20.);
        let lower = draw(&font, "a", 20.);
        assert_ne!(
            upper[0].texture.as_ref().unwrap().pixels(),
            lower[0].texture.as_ref().unwrap().pixels()
        );
        assert_eq!(draw(&font, "éΩЖ!?", 20.).len(), 5);
        assert!(!draw(&font, "\u{10ffff}", 20.).is_empty());
    }

    #[test]
    fn spaces_and_blank_lines_advance_without_visible_quads() {
        let font = font();
        assert!(draw(&font, "   ", 20.).is_empty());
        assert!(font.measure(" A ", 20.)[0] > font.measure("A", 20.)[0]);
        let one = font.measure("A", 20.);
        let multiline = font.measure("A\n\nA", 20.);
        assert!((multiline[0] - one[0]).abs() < 0.01);
        assert!((multiline[1] - one[1] * 3.).abs() < 0.01);
        let quads = draw(&font, "A\n\nA", 20.);
        assert_eq!(quads.len(), 2);
        assert!((quads[1].center[1] - quads[0].center[1] - 48.).abs() < 0.01);
    }

    #[test]
    fn scene_handles_reuse_glyphs_after_position_color_and_scene_changes() {
        let root = font();
        let splash = root.clone();
        let before = draw(&splash, "Nico", 20.);
        drop(splash);
        let scene = root.clone();
        let mut after = Vec::new();
        scene.draw(&mut after, "Nico", [100., 50.], 20., [0.2, 0.4, 0.6, 1.]);
        assert_eq!(before.len(), after.len());
        for (a, b) in before.iter().zip(&after) {
            assert!(Arc::ptr_eq(
                a.texture.as_ref().unwrap(),
                b.texture.as_ref().unwrap()
            ));
            assert_eq!(b.center, [a.center[0] + 100., a.center[1] + 50.]);
            assert_eq!(b.color, [0.2, 0.4, 0.6, 1.]);
        }
        assert_eq!(root.state.lock().unwrap().layouts.len(), 1);
    }

    #[test]
    fn density_changes_raster_resolution_without_changing_logical_extent() {
        let mut font = font();
        let extent = font.measure("Nico", 24.);
        let before = draw(&font, "Nico", 24.);
        font.set_raster_scale(2.);
        assert_eq!(font.measure("Nico", 24.), extent);
        let after = draw(&font, "Nico", 24.);
        for (a, b) in before.iter().zip(&after) {
            let low = a.texture.as_ref().unwrap();
            let high = b.texture.as_ref().unwrap();
            assert!(!Arc::ptr_eq(low, high));
            assert!(high.height() >= low.height() * 2 - 2);
            assert!((a.size[1] - b.size[1]).abs() <= 1.);
        }
        assert!(after.iter().any(|quad| {
            quad.texture
                .as_ref()
                .unwrap()
                .pixels()
                .chunks_exact(4)
                .any(|p| p[3] > 0 && p[3] < 255)
        }));
        font.set_raster_scale(1.);
        let restored = draw(&font, "Nico", 24.);
        assert!(Arc::ptr_eq(
            before[0].texture.as_ref().unwrap(),
            restored[0].texture.as_ref().unwrap()
        ));
    }

    #[test]
    fn invalid_layout_inputs_draw_nothing() {
        let mut font = font();
        for size in [0., -1., 257., f32::NAN, f32::INFINITY] {
            assert!(draw(&font, "A", size).is_empty());
            assert_eq!(font.measure("A", size), [0.; 2]);
        }
        assert!(draw(&font, "", 20.).is_empty());
        assert!(draw(&font, &"a".repeat(MAX_TEXT_BYTES + 1), 20.).is_empty());
        let mut quads = Vec::new();
        font.draw(&mut quads, "A", [f32::NAN, 0.], 20., [1.; 4]);
        font.draw(&mut quads, "A", [0.; 2], 20., [f32::INFINITY; 4]);
        assert!(quads.is_empty());
        font.set_raster_scale(f32::NAN);
        assert_eq!(font.raster_scale, 1.);
    }

    #[test]
    fn changing_counters_keeps_layout_cache_bounded() {
        let font = font();
        for counter in 0..MAX_LAYOUTS * 2 {
            assert!(!draw(&font, &format!("FPS {counter}"), 20.).is_empty());
        }
        let state = font.state.lock().unwrap();
        assert_eq!(state.layouts.len(), MAX_LAYOUTS);
        assert!(state.glyphs.len() <= MAX_GLYPHS);
        assert!(state.glyph_bytes <= MAX_GLYPH_BYTES);
        assert_eq!(state.layouts.back().unwrap().text, "FPS 127");
    }

    #[test]
    fn glyph_eviction_preserves_textures_retained_by_snapshots() {
        let font = font();
        let retained = draw(&font, "W", 10.);
        let texture = retained[0].texture.as_ref().unwrap();
        for index in 1..=MAX_GLYPHS + 1 {
            draw(&font, "W", 10. + index as f32 / 100.);
        }
        let state = font.state.lock().unwrap();
        assert!(state.glyphs.len() <= MAX_GLYPHS);
        assert!(state.glyph_bytes <= MAX_GLYPH_BYTES);
        assert!(
            !state
                .glyphs
                .values()
                .any(|glyph| Arc::ptr_eq(&glyph.texture, texture))
        );
        assert!(texture.pixels().chunks_exact(4).any(|p| p[3] > 0));
    }
}
