use glam::DVec2;

#[derive(Clone, Copy, Debug)]
pub struct Circle {
    pub center: [f64; 2],
    pub radius: f64,
}
#[derive(Clone, Copy, Debug)]
pub struct SlideSettings {
    pub iterations: u8,
    pub separation: f64,
}

/// Sweep a circle inside rectangular bounds and against stationary circles.
/// Equal-time hits preserve bounds/obstacle order. No pushing or depenetration;
/// callers must supply a nonoverlapping start. Work is bounded by iterations.
/// Invalid dimensions, radii, or nonfinite inputs return None.
pub fn slide_circle(
    circle: Circle,
    travel: [f64; 2],
    bounds: [[f64; 2]; 2],
    blockers: &[Circle],
    settings: SlideSettings,
) -> Option<[f64; 2]> {
    let mut p = DVec2::from(circle.center);
    let mut travel = DVec2::from(travel);
    if !p.is_finite()
        || !travel.is_finite()
        || !circle.radius.is_finite()
        || circle.radius < 0.0
        || !settings.separation.is_finite()
        || settings.separation < 0.0
        || settings.iterations == 0
        || bounds.iter().flatten().any(|v| !v.is_finite())
        || blockers.iter().any(|b| {
            !b.radius.is_finite() || b.radius < 0.0 || b.center.iter().any(|v| !v.is_finite())
        })
    {
        return None;
    }
    let min = DVec2::from(bounds[0]) + DVec2::splat(circle.radius);
    let max = DVec2::from(bounds[1]) - DVec2::splat(circle.radius);
    if min.x > max.x || min.y > max.y {
        return None;
    }
    for _ in 0..settings.iterations {
        if travel.length_squared() < 1e-20 {
            break;
        }
        let mut hit: Option<(f64, DVec2)> = None;
        let mut consider = |t: f64, normal: DVec2| {
            if (0.0..=1.0).contains(&t) && hit.is_none_or(|(old, _)| t < old) {
                hit = Some((t, normal));
            }
        };
        for (gap, delta, normal) in [
            (max.x - p.x, travel.x, -DVec2::X),
            (p.x - min.x, -travel.x, DVec2::X),
            (max.y - p.y, travel.y, -DVec2::Y),
            (p.y - min.y, -travel.y, DVec2::Y),
        ] {
            if delta > 0.0 {
                consider((gap / delta).max(0.0), normal);
            }
        }
        for blocker in blockers {
            let offset = p - DVec2::from(blocker.center);
            let a = travel.length_squared();
            let b = offset.dot(travel);
            if b >= 0.0 {
                continue;
            }
            let c = offset.length_squared() - (circle.radius + blocker.radius).powi(2);
            let discriminant = b * b - a * c;
            if discriminant <= 0.0 {
                continue;
            }
            let t = ((-b - discriminant.sqrt()) / a).max(0.0);
            consider(t, (offset + travel * t).normalize_or_zero());
        }
        let Some((t, normal)) = hit else {
            p += travel;
            break;
        };
        let safe = (t - settings.separation / travel.length()).max(0.0);
        p += travel * safe;
        travel *= 1.0 - safe;
        travel -= normal * travel.dot(normal).min(0.0);
    }
    Some(p.clamp(min, max).to_array())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn sweeps_stop_at_circles_and_slide_along_rectangular_walls() {
        let settings = SlideSettings {
            iterations: 4,
            separation: 0.0001,
        };
        let p = slide_circle(
            Circle {
                center: [0.0; 2],
                radius: 0.5,
            },
            [100.0, 2.0],
            [[-3.0, -4.0], [5.0, 4.0]],
            &[],
            settings,
        )
        .unwrap();
        assert!((p[0] - 4.5).abs() < 0.001 && (p[1] - 2.0).abs() < 1e-8);
        let p = slide_circle(
            Circle {
                center: [-2.0, 0.0],
                radius: 0.5,
            },
            [100.0, 0.0],
            [[-5.0; 2], [5.0; 2]],
            &[Circle {
                center: [0.0; 2],
                radius: 1.0,
            }],
            settings,
        )
        .unwrap();
        assert!((p[0] + 1.5).abs() < 0.001);
        assert!(
            slide_circle(
                Circle {
                    center: [0.0; 2],
                    radius: 10.0
                },
                [0.0; 2],
                [[-1.0; 2], [1.0; 2]],
                &[],
                settings
            )
            .is_none()
        );
    }
}
