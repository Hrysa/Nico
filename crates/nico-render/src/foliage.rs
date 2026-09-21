//! Fixed, bounded foliage uniform ABI shared by direct and storage-fetch paths.
use nico_presentation::foliage::{
    ChunkInfluences, FoliageProfile, InfluenceKind, MAX_CHUNK_INFLUENCES,
};

/// Two 16-byte headers followed by sixteen 48-byte field records. Explicit
/// little-endian serialization avoids Rust layout/alignment assumptions.
pub const FOLIAGE_UNIFORM_BYTES: usize = 32 + MAX_CHUNK_INFLUENCES * 48;

pub fn encode_foliage_uniform(
    profile: FoliageProfile,
    fields: &ChunkInfluences,
) -> [u8; FOLIAGE_UNIFORM_BYTES] {
    let mut bytes = [0; FOLIAGE_UNIFORM_BYTES];
    let mut word = |index: usize, value: u32| {
        bytes[index * 4..index * 4 + 4].copy_from_slice(&value.to_le_bytes());
    };
    word(0, profile.root_y().to_bits());
    word(1, profile.height().recip().to_bits());
    word(2, profile.max_bend().to_bits());
    // Time cannot affect deformation without active fields. Canonicalize it so
    // an advancing visual clock does not invalidate an otherwise empty uniform.
    if !fields.fields().is_empty() {
        word(3, (fields.time().rem_euclid(1.) as f32).to_bits());
    }
    word(4, fields.fields().len() as u32);
    for (index, field) in fields.fields().iter().enumerate() {
        let base = 8 + index * 12;
        for (axis, value) in field.position().into_iter().enumerate() {
            word(base + axis, value.to_bits());
        }
        word(base + 3, field.radius().to_bits());
        for (axis, value) in field.direction().into_iter().enumerate() {
            word(base + 4 + axis, value.to_bits());
        }
        word(base + 7, field.strength_at(fields.time()).to_bits());
        word(
            base + 8,
            match field.kind() {
                InfluenceKind::DirectionalWind => 0,
                InfluenceKind::RadialBend => 1,
            },
        );
    }
    bytes
}

#[cfg(test)]
mod tests {
    use super::*;
    use nico_presentation::{
        InstanceBounds,
        foliage::{InfluenceSnapshot, WorldInfluence},
    };

    #[test]
    fn empty_field_uniform_is_independent_of_visual_time() {
        let profile = FoliageProfile::new(0., 1., 0.5).unwrap();
        let bounds = InstanceBounds::new([0.; 3], [1.; 3]).unwrap();
        let first = InfluenceSnapshot::new(0., Vec::new())
            .unwrap()
            .for_chunk(bounds);
        let later = InfluenceSnapshot::new(0.75, Vec::new())
            .unwrap()
            .for_chunk(bounds);
        assert_eq!(
            encode_foliage_uniform(profile, &first),
            encode_foliage_uniform(profile, &later)
        );
    }

    #[test]
    fn uniform_layout_has_attenuated_strength_and_zero_unused_slots() {
        let field = WorldInfluence::new(
            7,
            InfluenceKind::RadialBend,
            [1., 2., 3.],
            [0., 1., 0.],
            4.,
            0.8,
            100.,
            110.,
        )
        .unwrap();
        let fields = InfluenceSnapshot::new(105., vec![field])
            .unwrap()
            .for_chunk(InstanceBounds::new([0.; 3], [4.; 3]).unwrap());
        let bytes = encode_foliage_uniform(FoliageProfile::new(-1., 2., 0.5).unwrap(), &fields);
        let word =
            |index: usize| u32::from_le_bytes(bytes[index * 4..index * 4 + 4].try_into().unwrap());
        assert_eq!(bytes.len(), 800);
        assert_eq!(word(0), (-1_f32).to_bits());
        assert_eq!(word(1), 0.5_f32.to_bits());
        assert_eq!(word(4), 1);
        assert_eq!(word(8), 1_f32.to_bits());
        assert_eq!(word(11), 4_f32.to_bits());
        assert_eq!(word(15), 0.4_f32.to_bits());
        assert_eq!(word(16), 1);
        assert!(bytes[68..].iter().all(|byte| *byte == 0));
    }
}
