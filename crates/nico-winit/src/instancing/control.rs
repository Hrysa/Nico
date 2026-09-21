use nico_ops::rendering::{RenderAction, RenderMode, RenderingControl, RenderingState};
use nico_presentation::{Scene3d, foliage::InfluenceSnapshot};
use nico_presentation_control::foliage::{FoliageController, VisualTimeCommand};
use nico_render::{InstanceRenderMode, MeshRenderPipeline};
use std::{sync::Arc, time::Duration};

#[derive(Default)]
pub(crate) struct RenderControlOwner {
    clock: FoliageController,
    override_time: bool,
    override_fields: bool,
    cached: Option<(Arc<InfluenceSnapshot>, Arc<InfluenceSnapshot>)>,
}
fn mode(value: RenderMode) -> InstanceRenderMode {
    match value {
        RenderMode::Auto => InstanceRenderMode::Auto,
        RenderMode::Cpu => InstanceRenderMode::Cpu,
        RenderMode::Gpu => InstanceRenderMode::Gpu,
    }
}
impl RenderControlOwner {
    fn replace_fields(
        &mut self,
        fields: Option<Vec<nico_ops::rendering::RenderInfluence>>,
        current_time: f64,
    ) -> Result<(), String> {
        let Some(fields) = fields else {
            self.override_fields = false;
            self.clock
                .replace_fields(Vec::new())
                .map_err(|e| e.to_string())?;
            return Ok(());
        };
        if !nico_ops::rendering::valid_influences(&fields) {
            return Err("invalid_influences".into());
        }
        let fields = fields
            .into_iter()
            .map(|field| {
                nico_presentation::foliage::WorldInfluence::new(
                    field.id,
                    if field.radial {
                        nico_presentation::foliage::InfluenceKind::RadialBend
                    } else {
                        nico_presentation::foliage::InfluenceKind::DirectionalWind
                    },
                    field.position,
                    field.direction,
                    field.radius,
                    field.strength,
                    field.start,
                    field.end,
                )
                .ok_or_else(|| "invalid_influence".to_string())
            })
            .collect::<Result<Vec<_>, _>>()?;
        self.clock
            .apply(VisualTimeCommand::Seek(current_time))
            .map_err(|e| e.to_string())?;
        self.clock
            .replace_fields(fields)
            .map_err(|e| e.to_string())?;
        self.override_fields = true;
        Ok(())
    }
    /// Run on the render owner before cache comparison and resource acquisition.
    /// Returns true when a mode change requires a fresh viewport render.
    #[allow(clippy::too_many_arguments)]
    pub fn prepare<D: nico_rhi::RhiDevice>(
        &mut self,
        device: &D,
        renderer: &mut MeshRenderPipeline<D>,
        scene: &mut Scene3d,
        commands: &RenderingControl,
        elapsed: Duration,
        host_frame: u64,
    ) -> bool {
        renderer.poll_instance_readbacks();
        if self.clock.advance(elapsed).is_err() {
            self.clock
                .apply(VisualTimeCommand::SetPaused(true))
                .unwrap();
        }
        let current_time = if self.override_time || self.override_fields {
            self.clock.time()
        } else {
            scene
                .foliage_influences
                .as_ref()
                .map_or(self.clock.time(), |s| s.time())
        };
        let mut changed = false;
        if let Some((id, action)) = commands.take_request() {
            let result = match action {
                RenderAction::Mode(requested) => {
                    let old = renderer.instance_mode();
                    let result = renderer
                        .set_instance_mode(device, scene, mode(requested))
                        .map_err(|e| e.to_string());
                    changed = old != renderer.instance_mode();
                    result
                }
                RenderAction::Paused(_) | RenderAction::Seek(_) => {
                    let initialize = if !self.override_time {
                        self.clock.apply(VisualTimeCommand::Seek(current_time))
                    } else {
                        Ok(())
                    };
                    initialize
                        .and_then(|()| {
                            self.clock.apply(match action {
                                RenderAction::Paused(value) => VisualTimeCommand::SetPaused(value),
                                RenderAction::Seek(value) => VisualTimeCommand::Seek(value),
                                _ => unreachable!(),
                            })
                        })
                        .map(|()| {
                            self.override_time = true;
                        })
                        .map_err(|e| e.to_string())
                }
                RenderAction::Fields(fields) => self.replace_fields(fields, current_time),
            };
            commands.complete(id, result);
        }
        if self.override_fields {
            scene.foliage_influences = Some(self.clock.snapshot());
            self.cached = None;
        } else if self.override_time
            && let Some(source) = &scene.foliage_influences
        {
            let time = self.clock.time();
            let reuse = self
                .cached
                .as_ref()
                .is_some_and(|(input, output)| Arc::ptr_eq(input, source) && output.time() == time);
            if !reuse {
                self.cached = Some((
                    source.clone(),
                    Arc::new(source.at_time(time).expect("validated clock")),
                ));
            }
            scene.foliage_influences = Some(self.cached.as_ref().unwrap().1.clone());
        } else {
            self.cached = None;
        }
        let cpu = renderer.validate_instance_mode(device, scene, InstanceRenderMode::Cpu);
        let gpu = renderer.validate_instance_mode(device, scene, InstanceRenderMode::Gpu);
        let capabilities = device.capabilities();
        let limits = capabilities.limits;
        commands.publish(RenderingState {
            auto_gpu_min_records: renderer.instance_auto_gpu_min_records() as u64,
            mode: match renderer.instance_mode() {
                InstanceRenderMode::Auto => RenderMode::Auto,
                InstanceRenderMode::Cpu => RenderMode::Cpu,
                InstanceRenderMode::Gpu => RenderMode::Gpu,
            },
            paused: self.clock.paused(),
            visual_seconds: if self.override_time || self.override_fields {
                self.clock.time()
            } else {
                current_time
            },
            cpu_supported: cpu.is_ok(),
            gpu_supported: gpu.is_ok(),
            cpu_rejection: cpu.err().map(|e| e.to_string().chars().take(512).collect()),
            gpu_rejection: gpu.err().map(|e| e.to_string().chars().take(512).collect()),
            capabilities: nico_ops::rendering::RenderingCapabilities {
                compute: capabilities.compute,
                indexed_indirect: capabilities.indexed_indirect,
                vertex_storage: capabilities.vertex_storage,
                asynchronous_readback: device.supports_buffer_readback(),
                max_buffer_size: limits.max_buffer_size,
                max_vertex_buffer_array_stride: limits.max_vertex_buffer_array_stride,
                max_vertex_buffers: limits.max_vertex_buffers,
                max_vertex_attributes: limits.max_vertex_attributes,
                max_bind_groups: limits.max_bind_groups,
                max_storage_buffers_per_shader_stage: limits.max_storage_buffers_per_shader_stage,
                max_storage_buffer_binding_size: limits.max_storage_buffer_binding_size,
                max_uniform_buffer_binding_size: limits.max_uniform_buffer_binding_size,
                max_compute_workgroup_size_x: limits.max_compute_workgroup_size_x,
                max_compute_invocations_per_workgroup: limits.max_compute_invocations_per_workgroup,
                max_compute_workgroups_per_dimension: limits.max_compute_workgroups_per_dimension,
            },
            host_frame,
            fields_overridden: self.override_fields,
            influence_count: scene
                .foliage_influences
                .as_ref()
                .map_or(0, |s| s.fields().len() as u32),
        });
        changed
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn diagnostic_fields_replace_atomically_and_survive_pause_seek() {
        let mut owner = RenderControlOwner::default();
        let field = nico_ops::rendering::RenderInfluence {
            id: 3,
            radial: true,
            position: [0.; 3],
            direction: [1., 0., 0.],
            radius: 2.,
            strength: 1.,
            start: 10.,
            end: 12.,
        };
        owner.replace_fields(Some(vec![field]), 10.).unwrap();
        assert!(owner.override_fields);
        assert_eq!(owner.clock.time(), 10.);
        let initial = owner.clock.snapshot();
        assert!(owner.replace_fields(Some(vec![field, field]), 11.).is_err());
        assert!(Arc::ptr_eq(&initial, &owner.clock.snapshot()));
        assert!(owner.replace_fields(Some(vec![field]), f64::NAN).is_err());
        assert!(Arc::ptr_eq(&initial, &owner.clock.snapshot()));
        owner
            .clock
            .apply(VisualTimeCommand::SetPaused(true))
            .unwrap();
        owner.clock.advance(Duration::from_secs(3)).unwrap();
        assert!(Arc::ptr_eq(&initial, &owner.clock.snapshot()));
        let bounds = nico_presentation::InstanceBounds::new([-1.; 3], [1.; 3]).unwrap();
        owner.clock.apply(VisualTimeCommand::Seek(12.)).unwrap();
        assert!(owner.clock.snapshot().for_chunk(bounds).fields().is_empty());
        owner.clock.apply(VisualTimeCommand::Seek(10.)).unwrap();
        assert_eq!(owner.clock.snapshot().for_chunk(bounds).fields().len(), 1);
        owner.replace_fields(Some(Vec::new()), 10.).unwrap();
        assert!(owner.override_fields);
        assert!(owner.clock.snapshot().fields().is_empty());
        owner.replace_fields(None, 10.).unwrap();
        assert!(!owner.override_fields);
    }
}
