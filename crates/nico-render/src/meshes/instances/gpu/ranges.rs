//! Plan reservations without mutating live ownership. The complement of live
//! intervals coalesces retired neighbors and includes unused allocation capacity.
use std::ops::Range;

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct Reservation {
    pub group: u32,
    pub records: Range<u32>,
}

pub(super) fn plan(
    capacity: (u32, u32),
    live: impl IntoIterator<Item = Reservation>,
    counts: &[u32],
) -> Option<Vec<Reservation>> {
    let mut occupied = Vec::new();
    let mut groups = vec![false; capacity.1 as usize];
    for reservation in live {
        let used = groups.get_mut(reservation.group as usize)?;
        if *used
            || reservation.records.start > reservation.records.end
            || reservation.records.end > capacity.0
        {
            return None;
        }
        *used = true;
        if !reservation.records.is_empty() {
            occupied.push(reservation.records);
        }
    }
    occupied.sort_unstable_by_key(|range| range.start);
    let mut free = Vec::new();
    let mut end = 0;
    for range in occupied {
        if range.start < end {
            return None;
        }
        if end < range.start {
            free.push(end..range.start);
        }
        end = range.end;
    }
    if end < capacity.0 {
        free.push(end..capacity.0);
    }
    let mut groups = groups
        .into_iter()
        .enumerate()
        .filter_map(|(group, used)| (!used).then_some(group as u32));
    let mut result = Vec::with_capacity(counts.len());
    for &count in counts {
        let group = groups.next()?;
        let records = if count == 0 {
            0..0
        } else {
            let range = free
                .iter_mut()
                .find(|range| range.end - range.start >= count)?;
            let records = range.start..range.start + count;
            range.start += count;
            records
        };
        result.push(Reservation { group, records });
    }
    Some(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn retired_neighbors_coalesce_without_moving_a_live_group() {
        let live = Reservation {
            group: 2,
            records: 5..8,
        };
        let planned = plan((12, 4), [live.clone()], &[4, 3]).unwrap();
        assert_eq!(
            planned,
            [
                Reservation {
                    group: 0,
                    records: 0..4
                },
                Reservation {
                    group: 1,
                    records: 8..11
                }
            ]
        );
        assert_eq!(live.records, 5..8);
        let shrunk = Reservation {
            group: 0,
            records: 0..1,
        };
        assert_eq!(
            plan((12, 4), [shrunk, live], &[4]).unwrap(),
            [Reservation {
                group: 1,
                records: 1..5
            }]
        );
    }
    #[test]
    fn failed_plan_preserves_capacity_for_a_smaller_transaction() {
        let live = Reservation {
            group: 1,
            records: 2..4,
        };
        assert!(plan((6, 3), [live.clone()], &[3]).is_none());
        assert!(plan((6, 3), [live.clone()], &[1, 1, 1]).is_none());
        let planned = plan((6, 3), [live], &[2, 2]).unwrap();
        assert_eq!(
            planned,
            [
                Reservation {
                    group: 0,
                    records: 0..2
                },
                Reservation {
                    group: 2,
                    records: 4..6
                }
            ]
        );
    }
}
