//! The Meadow quest is game-owned; progress travels with the character record.
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum QuestStage {
    #[default]
    Available,
    Active,
    Ready,
    Completed,
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct QuestProgress {
    pub stage: QuestStage,
    pub kills: u16,
}

impl QuestProgress {
    pub fn valid(&self) -> bool {
        match self.stage {
            QuestStage::Available => self.kills == 0,
            QuestStage::Active => self.kills < 3,
            QuestStage::Ready | QuestStage::Completed => self.kills == 3,
        }
    }

    pub fn credit(&mut self) {
        if self.stage == QuestStage::Active {
            self.kills += 1;
            if self.kills == 3 {
                self.stage = QuestStage::Ready;
            }
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct QuestDefinition {
    pub warden: super::Vec2,
    pub camp: super::Vec2,
    pub camp_radius_m: f64,
}

impl super::OpenWorld {
    pub(super) fn talk_to_warden(&mut self, id: super::ObjectId) {
        let e = self.ids[&id];
        let position = self
            .entities
            .entities()
            .get::<&super::Position>(e)
            .unwrap()
            .0;
        let mut player = self
            .entities
            .entities()
            .get::<&mut super::Player>(e)
            .unwrap();
        let Some(quest) = &self.zone.quest else {
            player.last_error = Some("quest_unavailable".into());
            return;
        };
        if super::distance(position, quest.warden) > 2.5 {
            player.last_error = Some("warden_out_of_reach".into());
            return;
        }
        match player.record.quest.stage {
            QuestStage::Available => player.record.quest.stage = QuestStage::Active,
            QuestStage::Active => player.last_error = Some("quest_incomplete".into()),
            QuestStage::Ready => {
                if !player
                    .record
                    .inventory
                    .iter()
                    .any(|i| i == super::ITEM_SWORD)
                {
                    if player.record.inventory.len() >= 32 {
                        player.last_error = Some("inventory_full".into());
                        return;
                    }
                    player.record.inventory.push(super::ITEM_SWORD.into());
                }
                player.record.experience = player.record.experience.saturating_add(50);
                player.record.quest.stage = QuestStage::Completed;
            }
            QuestStage::Completed => player.last_error = Some("quest_already_completed".into()),
        }
    }
}
