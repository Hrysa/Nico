//! Bootstrap bitmap text layout and cached glyph textures. Outputs ordinary quads.
//! Coordinates use a top-left origin. This is an ASCII 5x7 font, not a shaping engine.
use nico_assets::Texture;
use nico_presentation::{Quad, Scene2d};
use std::{collections::BTreeMap, sync::Arc};

#[derive(Default)]
pub struct BitmapFont {
    glyphs: BTreeMap<char, Arc<Texture>>,
}
impl BitmapFont {
    /// Unscaled text extent; lines advance eight pixels and glyphs six pixels.
    pub fn measure(text: &str) -> [f32; 2] {
        if text.is_empty() {
            return [0.0; 2];
        }
        let width = text
            .split('\n')
            .map(|line| line.chars().count())
            .max()
            .unwrap_or(0);
        [
            (width * 6).saturating_sub(1) as f32,
            (text.split('\n').count() * 8 - 1) as f32,
        ]
    }
    /// Append quads, preserving spaces and line breaks. Invalid scale draws nothing.
    /// Unsupported characters use a question-mark glyph; cached keys are bounded to ASCII.
    pub fn draw(
        &mut self,
        output: &mut Vec<Quad>,
        text: &str,
        origin: [f32; 2],
        scale: f32,
        color: [f32; 4],
    ) {
        if !scale.is_finite() || scale <= 0.0 {
            return;
        }
        let mut column = 0;
        let mut line = 0;
        for c in text.chars() {
            if c == '\n' {
                line += 1;
                column = 0;
                continue;
            }
            if c != ' ' {
                let c = if c.is_ascii_alphanumeric() || c == '/' {
                    c.to_ascii_uppercase()
                } else {
                    '?'
                };
                let texture = self.glyphs.entry(c).or_insert_with(|| glyph(c)).clone();
                output.push(Quad {
                    center: [
                        origin[0] + (column as f32 * 6.0 + 2.5) * scale,
                        origin[1] + (line as f32 * 8.0 + 3.5) * scale,
                    ],
                    size: [5.0 * scale, 7.0 * scale],
                    color,
                    texture: Some(texture),
                });
            }
            column += 1;
        }
    }
}
/// Add a solid HUD rectangle using a caller-owned white texture.
pub fn rectangle(
    scene: &mut Scene2d,
    origin: [f32; 2],
    size: [f32; 2],
    color: [f32; 4],
    white: Arc<Texture>,
) {
    scene.hud.push(Quad {
        center: [origin[0] + size[0] / 2.0, origin[1] + size[1] / 2.0],
        size,
        color,
        texture: Some(white),
    });
}
fn glyph(c: char) -> Arc<Texture> {
    let rows: [u8; 7] = match c {
        'A' => [14, 17, 17, 31, 17, 17, 17],
        'B' => [30, 17, 17, 30, 17, 17, 30],
        'C' => [14, 17, 16, 16, 16, 17, 14],
        'D' => [30, 17, 17, 17, 17, 17, 30],
        'E' => [31, 16, 16, 30, 16, 16, 31],
        'F' => [31, 16, 16, 30, 16, 16, 16],
        'G' => [14, 17, 16, 23, 17, 17, 15],
        'H' => [17, 17, 17, 31, 17, 17, 17],
        'I' => [31, 4, 4, 4, 4, 4, 31],
        'J' => [7, 2, 2, 2, 2, 18, 12],
        'K' => [17, 18, 20, 24, 20, 18, 17],
        'L' => [16, 16, 16, 16, 16, 16, 31],
        'M' => [17, 27, 21, 21, 17, 17, 17],
        'N' => [17, 25, 21, 19, 17, 17, 17],
        'O' => [14, 17, 17, 17, 17, 17, 14],
        'P' => [30, 17, 17, 30, 16, 16, 16],
        'Q' => [14, 17, 17, 17, 21, 18, 13],
        'R' => [30, 17, 17, 30, 20, 18, 17],
        'S' => [15, 16, 16, 14, 1, 1, 30],
        'T' => [31, 4, 4, 4, 4, 4, 4],
        'U' => [17, 17, 17, 17, 17, 17, 14],
        'V' => [17, 17, 17, 17, 17, 10, 4],
        'W' => [17, 17, 17, 21, 21, 21, 10],
        'X' => [17, 17, 10, 4, 10, 17, 17],
        'Y' => [17, 17, 10, 4, 4, 4, 4],
        'Z' => [31, 1, 2, 4, 8, 16, 31],
        '0' => [14, 17, 19, 21, 25, 17, 14],
        '1' => [4, 12, 4, 4, 4, 4, 14],
        '2' => [14, 17, 1, 2, 4, 8, 31],
        '3' => [30, 1, 1, 14, 1, 1, 30],
        '4' => [2, 6, 10, 18, 31, 2, 2],
        '5' => [31, 16, 16, 30, 1, 1, 30],
        '6' => [14, 16, 16, 30, 17, 17, 14],
        '7' => [31, 1, 2, 4, 8, 8, 8],
        '8' => [14, 17, 17, 14, 17, 17, 14],
        '9' => [14, 17, 17, 15, 1, 1, 14],
        '/' => [1, 2, 2, 4, 8, 8, 16],
        _ => [14, 17, 1, 2, 4, 0, 4],
    };
    let mut pixels = Vec::new();
    for row in rows {
        for x in 0..5 {
            pixels.extend_from_slice(&[
                255,
                255,
                255,
                if row & (1 << (4 - x)) != 0 { 255 } else { 0 },
            ]);
        }
    }
    Arc::new(Texture::rgba8(5, 7, pixels).unwrap())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn text_preserves_spacing_newlines_and_reuses_owned_glyph_textures() {
        let mut font = BitmapFont::default();
        let mut quads = Vec::new();
        font.draw(&mut quads, "A A\nA", [10.0, 20.0], 2.0, [1.0; 4]);
        assert_eq!(quads.len(), 3);
        assert_eq!(quads[1].center[0] - quads[0].center[0], 24.0);
        assert_eq!(quads[2].center[1] - quads[0].center[1], 16.0);
        assert!(Arc::ptr_eq(
            quads[0].texture.as_ref().unwrap(),
            quads[2].texture.as_ref().unwrap()
        ));
        assert_eq!(BitmapFont::measure("A A\nA"), [17.0, 15.0]);
        font.draw(&mut quads, "x", [0.0; 2], f32::NAN, [1.0; 4]);
        assert_eq!(quads.len(), 3);
    }
}
