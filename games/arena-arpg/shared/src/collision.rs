//! Game collision policy over engine spatial queries.
use crate::{Vec2, geometry};
use nico_spatial::{Circle, SlideSettings, slide_circle};
pub(crate) const RADIUS: f64 = geometry::ACTOR_RADIUS;
pub(crate) fn valid_position(p: Vec2) -> bool {
    p.finite() && p.x.abs() <= geometry::CENTER_LIMIT && p.z.abs() <= geometry::CENTER_LIMIT
}
pub(crate) fn slide(p: Vec2, travel: Vec2, blockers: &[Vec2]) -> Vec2 {
    let blockers: Vec<_> = blockers
        .iter()
        .map(|p| Circle {
            center: [p.x, p.z],
            radius: RADIUS,
        })
        .collect();
    let point = slide_circle(
        Circle {
            center: [p.x, p.z],
            radius: RADIUS,
        },
        [travel.x, travel.z],
        [[-geometry::INNER_FACE; 2], [geometry::INNER_FACE; 2]],
        &blockers,
        SlideSettings {
            iterations: 4,
            separation: 0.0001,
        },
    )
    .expect("validated arena movement");
    Vec2::new(point[0], point[1])
}
