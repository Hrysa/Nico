//! Equipment pose and primitive construction from validated character content.
use super::*;
pub(super) fn authored_reference(
    model: &Model,
    overrides: &[definition::PoseOverride],
) -> Result<Vec<Transform>> {
    let mut local = Pose::rest(model).local().to_vec();
    let resolve = |name: &str| -> Result<usize> {
        let mut nodes = model
            .data()
            .nodes
            .iter()
            .enumerate()
            .filter(|(_, n)| n.name == name);
        let index = nodes
            .next()
            .ok_or_else(|| format!("pose: missing bone {name}"))?
            .0;
        if nodes.next().is_some() {
            return Err(format!("pose: ambiguous bone {name}").into());
        }
        Ok(index)
    };
    for entry in overrides {
        let index = resolve(&entry.bone)?;
        let parent = resolve(&entry.parent)?;
        if !model.data().nodes[parent].children.contains(&index) {
            return Err(format!("pose: invalid parent for {}", entry.bone).into());
        }
        local[index].rotation = Quat::from_array(entry.rotation_xyzw).normalize().to_array();
    }
    Ok(local)
}
#[cfg(test)]
fn reference(model: &Model) -> Result<Vec<Transform>> {
    authored_reference(model, &definition::VisualDefinition::builtin(0).core.pose)
}
pub(super) fn weapon(parts: &[definition::WeaponPart]) -> Result<Mesh> {
    let mut vertices = Vec::new();
    let mut indices = Vec::new();
    for (swatch, entry) in parts.iter().enumerate() {
        let part = crate::visuals::box_mesh(entry.size_m);
        let base = vertices.len() as u32;
        for vertex in part.vertices() {
            let mut vertex = *vertex;
            for (v, offset) in vertex.position.iter_mut().zip(entry.center_m) {
                *v += offset;
            }
            vertex.uv = [(swatch as f32 + 0.5) / parts.len() as f32, 0.5];
            vertices.push(vertex);
        }
        indices.extend(part.indices().iter().map(|i| i + base));
    }
    let weights = vec![
        SkinWeights {
            joints: [0; 4],
            weights: [1., 0., 0., 0.]
        };
        vertices.len()
    ];
    Mesh::skinned_triangles(vertices, indices, weights, 1)
        .ok_or_else(|| "invalid weapon mesh".into())
}
#[cfg(test)]
mod tests {
    use super::*;
    fn assets() -> Arc<CharacterAssets> {
        let root =
            Path::new(env!("CARGO_MANIFEST_DIR")).join("../assets/presentation/characters/hero");
        CharacterAssets::load(&root.join("model.glb"), &root.join("animations")).unwrap()
    }

    #[test]
    fn equipped_fingers_hold_the_handle_through_every_clip_and_blend() {
        let assets = assets();
        let model = assets.set.model();
        let reference = reference(model).unwrap();
        let fingers: Vec<_> = model
            .data()
            .nodes
            .iter()
            .enumerate()
            .filter(|(_, n)| {
                definition::VisualDefinition::builtin(0)
                    .core
                    .pose
                    .iter()
                    .any(|p| n.name == p.bone)
            })
            .map(|(i, _)| i)
            .collect();
        assert_eq!(fingers.len(), 15);
        let mut player = AnimationPlayer::new(assets.set.clone());
        for clip in 0..definition::MOTIONS.len() {
            player.play(clip, PlayMode::Once, Duration::ZERO).unwrap();
            for fraction in [0., 0.25, 0.5, 0.75, 1.] {
                player.seek(player.duration() * fraction).unwrap();
                let pose = player.pose();
                for &i in &fingers {
                    assert_eq!(pose.local()[i], reference[i]);
                }
                assert!(pose.globals().unwrap().iter().all(|m| m.is_finite()));
            }
        }
        player
            .play(0, PlayMode::Loop, Duration::from_millis(200))
            .unwrap();
        player.update(Duration::from_millis(100)).unwrap();
        for &i in &fingers {
            assert!(
                Quat::from_array(player.pose().local()[i].rotation)
                    .dot(Quat::from_array(reference[i].rotation))
                    .abs()
                    > 0.99999
            );
        }
    }

    #[test]
    fn grip_changes_only_right_finger_rotations_and_rejects_incompatible_rigs() {
        let assets = assets();
        let model = assets.set.model();
        let posed = reference(model).unwrap();
        for (i, node) in model.data().nodes.iter().enumerate() {
            assert_eq!(posed[i].translation, node.transform.translation);
            assert_eq!(posed[i].scale, node.transform.scale);
            if !definition::VisualDefinition::builtin(0)
                .core
                .pose
                .iter()
                .any(|p| node.name == p.bone)
            {
                assert_eq!(posed[i], node.transform);
            }
        }
        let mut missing = model.data().clone();
        let index = missing
            .nodes
            .iter()
            .position(|n| n.name == "mixamorig:RightHandIndex1")
            .unwrap();
        missing.nodes[index].name = "missing".into();
        assert!(reference(&Model::new(missing).unwrap()).is_err());
        let mut duplicate = model.data().clone();
        duplicate.nodes[0].name = "mixamorig:RightHandIndex1".into();
        assert!(reference(&Model::new(duplicate).unwrap()).is_err());
        let mut hierarchy = model.data().clone();
        let thumb = hierarchy
            .nodes
            .iter()
            .position(|n| n.name == "mixamorig:RightHandThumb1")
            .unwrap();
        hierarchy.nodes[thumb].name = "mixamorig:RightHandIndex1".into();
        hierarchy.nodes[index].name = "mixamorig:RightHandThumb1".into();
        assert!(reference(&Model::new(hierarchy).unwrap()).is_err());
    }

    #[test]
    fn sword_contacts_forward_target_at_active_boundary_and_clears_floor() {
        let assets = assets();
        let equipped = assets.weapon.as_ref().unwrap();
        let mut player = AnimationPlayer::new(assets.set.clone());
        let placement = Mat4::from_scale_rotation_translation(
            Vec3::splat(assets.scale),
            Quat::IDENTITY,
            Vec3::new(0., -assets.floor * assets.scale, 0.),
        );
        player.play(2, PlayMode::Once, Duration::ZERO).unwrap();
        player
            .seek(
                assets.definition.arena.animations["attack"]
                    .contact_seconds
                    .unwrap(),
            )
            .unwrap();
        let m = equipped
            .weapon_socket
            .matrix(&player.pose(), placement)
            .unwrap()
            * Mat4::from_scale(Vec3::splat(equipped.weapon_scale));
        let tip = m.transform_point3(Vec3::new(0., 0., equipped.weapon_length));
        let range = arena_arpg_shared::ActorKind::Hero.stats().range as f32;
        let target_distance = Vec3::new(tip.x, 0., tip.z - range).length();
        assert!(
            target_distance < arena_arpg_shared::geometry::ACTOR_RADIUS as f32,
            "blade misses target at gameplay range: {tip:?}"
        );
        assert!(
            (0.4..1.2).contains(&tip.y),
            "contact must meet the target body"
        );
        // Dense endpoint-inclusive sampling detects the roll's near-ground arc.
        for clip in 0..definition::MOTIONS.len() {
            player.play(clip, PlayMode::Once, Duration::ZERO).unwrap();
            for i in 0..=240 {
                player
                    .seek(player.duration() * f64::from(i) / 240.)
                    .unwrap();
                let m = equipped
                    .weapon_socket
                    .matrix(&player.pose(), placement)
                    .unwrap()
                    * Mat4::from_scale(Vec3::splat(equipped.weapon_scale));
                for v in equipped.blade.vertices() {
                    let point = m.transform_point3(v.position.into());
                    assert!(
                        point.is_finite() && point.y >= 0.,
                        "clip {clip} at {} puts weapon below floor: {point:?}",
                        player.time()
                    );
                }
            }
        }
        // Full rendered bounds must include handle, guard and blade.
        let mut character = Character::new(assets.clone());
        let state = arena_arpg_shared::Arena::default().snapshot().clone();
        let meshes = character.render(&state, Duration::ZERO, None).unwrap();
        assert_eq!(meshes.len(), assets.visual.primitive_count() + 1);
        let bounds = character.render_bounds.unwrap();
        for vertex in equipped.blade.vertices() {
            let point = character
                .weapon_matrix
                .transform_point3(vertex.position.into());
            assert!(point.cmpge(Vec3::from(bounds.min)).all());
            assert!(point.cmple(Vec3::from(bounds.max)).all());
        }
    }
}
