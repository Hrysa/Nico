//! Translate renderer counters into an owned, dependency-free host report.
pub(crate) mod control;
// Provisional native policy measured with cached bounds and fused GPU culling.
pub(super) const AUTO_GPU_MIN_RECORDS: usize = 1024;
fn read_shader(path: &std::path::Path, label: &str) -> Result<Vec<u8>, nico_rhi::RhiError> {
    std::fs::read(path).map_err(|e| {
        nico_rhi::RhiError::new(
            nico_rhi::RhiErrorKind::Backend,
            format!("failed to read {label} shader {}: {e}", path.display()),
        )
    })
}
pub(super) fn install_foliage<D: nico_rhi::RhiDevice>(
    device: &D,
    renderer: &mut nico_render::MeshRenderPipeline<D>,
    direct: &std::path::Path,
    storage: &std::path::Path,
    compute: &std::path::Path,
) -> Result<(), nico_rhi::RhiError> {
    let direct = read_shader(direct, "foliage")?;
    match renderer.enable_foliage(device, nico_rhi::builtin_shaders::bootstrap_wgsl(&direct)) {
        Err(e) if e.kind() == nico_rhi::RhiErrorKind::Unsupported => {
            tracing::info!(reason=%e,"foliage rendering unavailable on this device");
            return Ok(());
        }
        result => result?,
    }
    let storage = read_shader(storage, "foliage")?;
    let compute = read_shader(compute, "foliage")?;
    match renderer.enable_gpu_foliage(
        device,
        nico_rhi::builtin_shaders::bootstrap_wgsl(&storage),
        nico_rhi::ShaderModuleDescriptor {
            label: Some("foliage visibility"),
            format: nico_rhi::ShaderFormat::Wgsl,
            code: &compute,
        },
    ) {
        Err(e) if e.kind() == nico_rhi::RhiErrorKind::Unsupported => {
            tracing::info!(reason=%e,"using CPU foliage visibility fallback");
            Ok(())
        }
        result => result,
    }?;
    // Native hosts install the matching built-in direct/storage/foliage artifacts.
    // Unsafe affine reconstruction still selects full records per batch.
    match renderer.enable_compact_instance_records(device) {
        Ok(()) => {
            tracing::info!("guarded compact instance records enabled");
            Ok(())
        }
        Err(error) if error.kind() == nico_rhi::RhiErrorKind::Unsupported => {
            tracing::info!(reason=%error, "using full instance record fallback");
            Ok(())
        }
        Err(error) => Err(error),
    }
}

pub(super) fn install_gpu<D: nico_rhi::RhiDevice>(
    device: &D,
    renderer: &mut nico_render::MeshRenderPipeline<D>,
    storage: &std::path::Path,
    compute: &std::path::Path,
) -> Result<(), nico_rhi::RhiError> {
    let storage = read_shader(storage, "instance")?;
    let compute = read_shader(compute, "instance")?;
    match renderer.enable_gpu_instancing(
        device,
        nico_rhi::builtin_shaders::bootstrap_wgsl(&storage),
        nico_rhi::ShaderModuleDescriptor {
            label: Some("instance visibility"),
            format: nico_rhi::ShaderFormat::Wgsl,
            code: &compute,
        },
    ) {
        Err(e) if e.kind() == nico_rhi::RhiErrorKind::Unsupported => {
            tracing::info!(reason=%e,"using CPU instance visibility fallback");
            Ok(())
        }
        other => other,
    }
}

pub(super) fn report(
    host_frame: u64,
    stats: nico_render::InstanceRenderStats,
) -> nico_ops::InstancingStatus {
    nico_ops::InstancingStatus {
        gpu_sample: stats
            .gpu_readback
            .sample
            .map(|sample| nico_ops::GpuVisibilityStatus {
                prepared_view: sample.prepared_view,
                visible_instances: sample.visible_instances,
                candidate_instances: sample.candidate_instances,
                indirect_draws: sample.indirect_draws,
                age_ms: sample.age_ms,
            }),
        gpu_readback_pending: stats.gpu_readback.pending,
        gpu_readback_skipped: stats.gpu_readback.skipped,
        gpu_readback_failed: stats.gpu_readback.failed,
        host_frame,
        prepared_view: stats.prepared_view,
        visible_chunks: stats.visible_chunks,
        culled_chunks: stats.culled_chunks,
        submitted_instances: stats.submitted_instances,
        submitted_draws: stats.submitted_draws,
        indirect_draws: stats.indirect_draws,
        gpu_candidate_instances: stats.gpu_candidate_instances,
        visibility_upload_bytes: stats.visibility_upload_bytes,
        visibility_retirement_upload_bytes: stats.visibility_retirement_upload_bytes,
        foliage_upload_bytes: stats.foliage_upload_bytes,
        influence_overflow: stats.influence_overflow,
        visible_record_upload_bytes: stats.visible_record_upload_bytes,
        instance_upload_bytes: stats.instance_upload_bytes,
        deferred_upload_chunks: stats.deferred_upload_chunks,
        visibility_reused_batches: stats.visibility_reused_batches,
        visibility_dispatched_pages: stats.visibility_dispatched_pages,
        visibility_dispatches: stats.visibility_dispatches,
        mesh_upload_bytes: stats.mesh_upload_bytes,
        retained_instance_bytes: stats.retained_instance_bytes,
        retained_split_cpu_bytes: stats.retained_split_cpu_bytes,
        retained_batches: stats.retained_batches,
        ordinary_fallback: stats.ordinary_fallback,
    }
}
