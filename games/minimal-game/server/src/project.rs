//! Game-owned authored object loading; transport and process control stay in the host.
use nico_ops::{
    mcp::{CallToolResult, Tool, ToolAccess, ToolExtensions},
    publication::Publication,
};
use nico_runtime::{AppBuilder, Stage};
use nico_scene::{Object, Project};
use serde_json::{Value, json};
use std::{
    io,
    path::PathBuf,
    sync::{Arc, Mutex},
};

pub fn register(
    mut builder: AppBuilder,
    tools: &mut Option<ToolExtensions>,
    root: PathBuf,
) -> io::Result<(AppBuilder, String)> {
    let project = Project::open(root)?;
    let revision = nico_scene::content::revision(&project, &|| false)?;
    let document = project.load_scene()?;
    if revision != nico_scene::content::revision(&project, &|| false)? {
        return Err(io::Error::other("project content changed while loading"));
    }
    let published = Arc::new(Mutex::new(Publication::<Value>::default()));
    if let Some(tools) = tools {
        let observed = published.clone();
        tools.register(
            Tool::new(
                "scene_state",
                "Read server-authored objects from its owned update snapshot.",
                json!({"type":"object","properties":{},"additionalProperties":false})
                    .as_object()
                    .unwrap()
                    .clone(),
            ),
            move |args| {
                if !args.is_empty() {
                    return CallToolResult::structured_error(
                        json!({"error":"scene_state takes no arguments"}),
                    );
                }
                observed
                    .lock()
                    .unwrap()
                    .json()
                    .map(CallToolResult::structured)
                    .unwrap_or_else(|| {
                        CallToolResult::structured_error(json!({"error":"not ready"}))
                    })
            },
        )?;
        tools.set_access("scene_state", ToolAccess::Inspect)?;
    }
    builder.add_system(Stage::Startup, "scene::instantiate", move |ctx| {
        nico_scene::instantiate(&document, ctx.world).expect("validated scene");
        Ok(())
    });
    let output = published.clone();
    builder.add_system(Stage::Update, "scene::publish", move |ctx| {
        let mut objects: Vec<Object> = ctx.world.query::<&Object>().iter().cloned().collect();
        objects.sort_by_key(|object| object.id);
        output
            .lock()
            .unwrap()
            .publish(json!({"ready":true,"objects":objects}));
        Ok(())
    });
    builder.add_system(Stage::Shutdown, "scene::close", move |_| {
        published.lock().unwrap().close();
        Ok(())
    });
    Ok((builder, revision))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn server_loads_saved_objects_and_reports_the_saved_content_revision() {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("..");
        let project = Project::open(&root).unwrap();
        let expected = project.load_scene().unwrap();
        let (builder, revision) = register(AppBuilder::new(), &mut None, root).unwrap();
        assert_eq!(
            revision,
            nico_scene::content::revision(&project, &|| false).unwrap()
        );
        let mut app = builder.build().unwrap();
        app.start().unwrap();
        app.tick(std::time::Duration::ZERO).unwrap();
        assert_eq!(
            app.world()
                .query::<&Object>()
                .iter()
                .cloned()
                .collect::<Vec<_>>(),
            expected.objects
        );
        app.shutdown().unwrap();
    }
}
