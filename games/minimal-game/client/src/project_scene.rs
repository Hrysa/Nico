//! Game composition of the engine's authored scene contracts.
use nico_assets::{
    cache::ImportCache,
    import::ImportBudget,
    importers::{ModelGlbImporter, ModelGlbSettings},
    model_loading::model_bundle,
};
use nico_ops::{
    mcp::{CallToolResult, Tool, ToolExtensions},
    publication::Publication,
};
use nico_presentation::{Camera3d, Scene3d};
use nico_presentation_control::scene::{place_meshes, rest_meshes};
use nico_runtime::{AppBuilder, Stage};
use nico_scene::{Object, Project};
use serde_json::{Value, json};
use std::{
    collections::BTreeMap,
    io,
    path::PathBuf,
    sync::{Arc, Mutex},
};

pub fn register(
    mut builder: AppBuilder,
    tools: &mut ToolExtensions,
    root: PathBuf,
) -> io::Result<AppBuilder> {
    let project = Project::open(root)?;
    let document = project.load_scene()?;
    let cache = ImportCache::new(project.root()).map_err(io::Error::other)?;
    let mut models = BTreeMap::new();
    let mut errors = BTreeMap::new();
    for object in &document.objects {
        if models.contains_key(&object.asset) || errors.contains_key(&object.asset) {
            continue;
        }
        let result = (|| {
            let path = project.resolve_asset(&object.asset)?;
            let model = cache
                .load(
                    &path,
                    &ModelGlbImporter,
                    &ModelGlbSettings::default(),
                    ImportBudget::default(),
                    &|| false,
                )
                .map_err(io::Error::other)?;
            let bundle = model_bundle(model, &|| false).map_err(io::Error::other)?;
            rest_meshes(&bundle)
        })();
        match result {
            Ok(meshes) => {
                models.insert(object.asset.clone(), meshes);
            }
            Err(e) => {
                errors.insert(object.asset.clone(), e.to_string());
            }
        }
    }
    let published = Arc::new(Mutex::new(Publication::<Value>::default()));
    let observed = published.clone();
    tools.register(Tool::new("scene_state", "Read authored objects instantiated by game code and CPU draw readiness. Sources load at startup; GPU completion is reported separately by the host.",
        json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone()), move |args| {
        if !args.is_empty() { return CallToolResult::structured_error(json!({"error":{"code":"invalid_arguments","message":"scene_state takes no arguments"}})); }
        observed.lock().unwrap().json().map(CallToolResult::structured).unwrap_or_else(|| CallToolResult::structured_error(json!({"error":{"code":"not_ready","message":"scene has not been extracted"}})))
    })?;
    builder.add_system(Stage::Startup, "scene::instantiate", move |ctx| {
        nico_scene::instantiate(&document, ctx.world).expect("validated scene");
        Ok(())
    });
    builder.insert_resource(Scene3d::default());
    let output = published.clone();
    builder.add_system(Stage::Update, "scene::extract", move |ctx| {
        let mut objects: Vec<Object> = ctx.world.query::<&Object>().iter().cloned().collect();
        objects.sort_by_key(|o| o.id);
        let mut scene = Scene3d { camera: Camera3d::looking_at([3.602876, 2.7431824, 6.594], [0.;3], [0.,1.,0.]).unwrap() , ..Default::default() };
        let mut error = None;
        for object in &objects {
            if let Some(meshes) = models.get(&object.asset) {
                if scene.meshes.len() + meshes.len() > 256 { error = Some("scene exceeds 256 rendered primitives"); break; }
                scene.meshes.extend(place_meshes(meshes, object));
            }
        }
        output.lock().unwrap().publish(json!({"project":project.root(),"manifest":project.manifest,"objects":objects,"draws":scene.meshes.len(),"ready":errors.is_empty() && error.is_none(),"asset_errors":errors,"error":error}));
        ctx.world.insert_resource(scene);
        Ok(())
    });
    builder.add_system(Stage::Shutdown, "scene::close", move |_| {
        published.lock().unwrap().close();
        Ok(())
    });
    Ok(builder)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn saved_scene_instantiates_and_ecs_transform_changes_reach_draws() {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("..");
        let project = Project::open(&root).unwrap();
        let document = project.load_scene().unwrap();
        let builder = register(AppBuilder::new(), &mut ToolExtensions::default(), root).unwrap();
        let mut app = builder.build().unwrap();
        app.start().unwrap();
        app.tick(std::time::Duration::ZERO).unwrap();
        let objects: Vec<_> = app.world().query::<&Object>().iter().cloned().collect();
        assert_eq!(objects, document.objects);
        assert!(!app.world().resource::<Scene3d>().unwrap().meshes.is_empty());
        for object in app.world_mut().query::<&mut Object>().iter() {
            object.position = [1., 2., 3.];
            object.rotation = [0., 90., 0.];
            object.scale = 2.;
        }
        app.tick(std::time::Duration::ZERO).unwrap();
        let scene = app.world().resource::<Scene3d>().unwrap();
        assert!(
            scene
                .meshes
                .iter()
                .all(|m| m.position == [1., 2., 3.] && m.scale == 2.)
        );
        app.shutdown().unwrap();
    }
}
