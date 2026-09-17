//! Runtime-free metallic/roughness material assets. Texture interpretation belongs
//! to the material slot: base color/emissive are sRGB, all other slots are linear.
use crate::{
    Texture,
    model::{AlphaMode, Filter, WrapMode},
};
use std::sync::Arc;

/// One texture and its sampling policy. Current CPU images contain one mip level.
#[derive(Clone, Debug)]
pub struct MaterialTexture {
    pub image: Arc<Texture>,
    pub wrap_s: WrapMode,
    pub wrap_t: WrapMode,
    pub min_filter: Filter,
    pub mag_filter: Filter,
}
impl MaterialTexture {
    pub fn new(image: Arc<Texture>) -> Self {
        Self {
            image,
            wrap_s: WrapMode::Repeat,
            wrap_t: WrapMode::Repeat,
            min_filter: Filter::Linear,
            mag_filter: Filter::Linear,
        }
    }
}

/// Share via Arc; replacing material content creates a new GPU cache identity.
/// Color factors and lighting are linear. UVs address TEXCOORD_0.
#[derive(Clone, Debug)]
pub struct PbrMaterial {
    pub base_color: [f32; 4],
    pub metallic: f32,
    pub roughness: f32,
    pub base_color_texture: Option<MaterialTexture>,
    pub metallic_roughness_texture: Option<MaterialTexture>,
    pub normal_texture: Option<MaterialTexture>,
    pub normal_scale: f32,
    pub occlusion_texture: Option<MaterialTexture>,
    pub occlusion_strength: f32,
    pub emissive_texture: Option<MaterialTexture>,
    pub emissive: [f32; 3],
    pub alpha: AlphaMode,
    pub alpha_cutoff: f32,
    pub double_sided: bool,
}
impl Default for PbrMaterial {
    fn default() -> Self {
        Self {
            base_color: [1.; 4],
            metallic: 0.,
            roughness: 1.,
            base_color_texture: None,
            metallic_roughness_texture: None,
            normal_texture: None,
            normal_scale: 1.,
            occlusion_texture: None,
            occlusion_strength: 1.,
            emissive_texture: None,
            emissive: [0.; 3],
            alpha: AlphaMode::Opaque,
            alpha_cutoff: 0.5,
            double_sided: false,
        }
    }
}
impl PbrMaterial {
    #[must_use]
    pub fn is_valid(&self) -> bool {
        self.base_color
            .iter()
            .chain([&self.metallic, &self.roughness, &self.occlusion_strength])
            .all(|v| v.is_finite() && (0. ..=1.).contains(v))
            && self.emissive.iter().all(|v| v.is_finite() && *v >= 0.)
            && self.normal_scale.is_finite()
            && self.alpha_cutoff.is_finite()
            && self.alpha_cutoff >= 0.
            && self
                .textures()
                .into_iter()
                .flatten()
                .all(|t| matches!(t.mag_filter, Filter::Nearest | Filter::Linear))
    }
    /// Stable shader slot order: base color, metallic/roughness, normal, AO, emissive.
    pub fn textures(&self) -> [Option<&MaterialTexture>; 5] {
        [
            self.base_color_texture.as_ref(),
            self.metallic_roughness_texture.as_ref(),
            self.normal_texture.as_ref(),
            self.occlusion_texture.as_ref(),
            self.emissive_texture.as_ref(),
        ]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn material_rejects_invalid_factors_and_accepts_roughness_endpoints() {
        assert!(PbrMaterial::default().is_valid());
        for roughness in [0., 1.] {
            assert!(
                PbrMaterial {
                    roughness,
                    ..Default::default()
                }
                .is_valid()
            );
        }
        for roughness in [-0.1, 1.1, f32::NAN, f32::INFINITY] {
            assert!(
                !PbrMaterial {
                    roughness,
                    ..Default::default()
                }
                .is_valid()
            );
        }
        assert!(
            !PbrMaterial {
                emissive: [-1.; 3],
                ..Default::default()
            }
            .is_valid()
        );
        assert!(
            !PbrMaterial {
                normal_scale: f32::NAN,
                ..Default::default()
            }
            .is_valid()
        );
    }
}
