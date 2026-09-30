//! Bundled game font. Application and scene handles share its prepared glyph cache.
use nico_presentation_control::text::TextFont;
pub fn load() -> std::io::Result<TextFont> {
    TextFont::from_bytes(include_bytes!(
        "../../assets/presentation/fonts/NotoSans.ttf"
    ))
}
