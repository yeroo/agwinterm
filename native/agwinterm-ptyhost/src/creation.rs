//! Metadata-only prepared tickets; the HostState mutex also guards session publication.
use std::collections::HashMap;
use std::time::{Duration, Instant};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Phase {
    Prepared,
    Creating,
    Live,
    Cancelling,
}
pub struct Entry<T> {
    pub id: String,
    pub phase: Phase,
    pub value: Option<T>,
    prepared: Instant,
}
pub struct Tickets<T> {
    entries: HashMap<String, Entry<T>>,
    stopped: bool,
}
impl<T: Clone> Tickets<T> {
    pub fn new() -> Self {
        Self {
            entries: HashMap::new(),
            stopped: false,
        }
    }
    fn expire(&mut self, now: Instant) {
        self.entries.retain(|_, e| {
            e.phase != Phase::Prepared
                || now.saturating_duration_since(e.prepared) < Duration::from_secs(60)
        });
    }
    pub fn prepare(&mut self, id: &str, ticket: String, now: Instant) -> Option<String> {
        self.expire(now);
        if self.stopped
            || id.is_empty()
            || self.entries.contains_key(&ticket)
            || self
                .entries
                .values()
                .filter(|e| e.phase == Phase::Prepared)
                .count()
                >= 1024
        {
            return None;
        }
        self.entries.insert(
            ticket.clone(),
            Entry {
                id: id.into(),
                phase: Phase::Prepared,
                value: None,
                prepared: now,
            },
        );
        Some(ticket)
    }
    pub fn find(&mut self, id: &str, ticket: &str, now: Instant) -> Option<&Entry<T>> {
        self.expire(now);
        self.entries
            .get(ticket)
            .filter(|e| e.id.eq_ignore_ascii_case(id))
    }
    pub fn begin(&mut self, id: &str, ticket: &str, now: Instant) -> bool {
        if self.stopped
            || self
                .find(id, ticket, now)
                .is_none_or(|e| e.phase != Phase::Prepared)
        {
            return false;
        }
        if self.entries.iter().any(|(key, e)| {
            key != ticket && e.phase != Phase::Prepared && e.id.eq_ignore_ascii_case(id)
        }) {
            self.entries.remove(ticket);
            return false;
        }
        self.entries.get_mut(ticket).unwrap().phase = Phase::Creating;
        true
    }
    pub fn complete(&mut self, ticket: &str, value: T) -> bool {
        let e = self
            .entries
            .get_mut(ticket)
            .expect("active creation must retain its ticket");
        assert!(matches!(e.phase, Phase::Creating | Phase::Cancelling) && e.value.is_none());
        e.value = Some(value);
        if self.stopped || e.phase == Phase::Cancelling {
            e.phase = Phase::Cancelling;
            false
        } else {
            e.phase = Phase::Live;
            true
        }
    }
    // A live result has exactly one cleanup claimant; an in-flight creator retains its own.
    pub fn cancel(&mut self, ticket: &str) -> Option<T> {
        let e = self.entries.get_mut(ticket)?;
        if e.phase == Phase::Prepared {
            self.entries.remove(ticket);
            return None;
        }
        if e.phase == Phase::Cancelling {
            return None;
        }
        let value = if e.phase == Phase::Live {
            e.value.clone()
        } else {
            None
        };
        e.phase = Phase::Cancelling;
        value
    }
    pub fn cleaned(&mut self, ticket: &str) {
        self.entries.remove(ticket);
    }
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
    pub fn stop(&mut self) -> Vec<(String, T)> {
        self.stopped = true;
        let keys: Vec<_> = self.entries.keys().cloned().collect();
        keys.into_iter()
            .filter_map(|key| self.cancel(&key).map(|value| (key, value)))
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn unknown_expired_and_cancelled_never_start() {
        let now = Instant::now();
        let mut t = Tickets::<u32>::new();
        assert!(!t.begin("pane", "unknown", now));
        t.prepare("pane", "expired".into(), now).unwrap();
        assert!(!t.begin("pane", "expired", now + Duration::from_secs(60)));
        t.prepare("pane", "cancelled".into(), now).unwrap();
        assert_eq!(t.cancel("cancelled"), None);
        assert!(!t.begin("pane", "cancelled", now));
    }
    #[test]
    fn duplicate_create_and_id_reuse_are_separate() {
        let now = Instant::now();
        let mut t = Tickets::new();
        t.prepare("pane", "a".into(), now).unwrap();
        assert!(t.begin("pane", "a", now));
        assert!(!t.begin("pane", "a", now));
        assert!(t.complete("a", 1));
        assert!(!t.begin("pane", "a", now));
        assert_eq!(t.find("pane", "a", now).unwrap().value, Some(1));
        assert_eq!(t.cancel("a"), Some(1));
        assert_eq!(t.cancel("a"), None);
        t.cleaned("a");
        t.prepare("pane", "b".into(), now).unwrap();
        assert!(t.begin("pane", "b", now));
        assert!(t.complete("b", 2));
        assert_eq!(t.cancel("a"), None);
        t.cleaned("a");
        assert_eq!(t.find("pane", "b", now).unwrap().value, Some(2));
    }
    #[test]
    fn cancellation_during_spawn_stays_pending_until_creator_cleans() {
        let now = Instant::now();
        let mut t = Tickets::new();
        t.prepare("pane", "a".into(), now).unwrap();
        assert!(t.begin("pane", "a", now));
        assert_eq!(t.cancel("a"), None);
        assert!(!t.complete("a", 1));
        assert_eq!(t.find("pane", "a", now).unwrap().phase, Phase::Cancelling);
        assert_eq!(t.cancel("a"), None);
        t.prepare("PANE", "b".into(), now).unwrap();
        assert!(!t.begin("pane", "b", now));
        t.cleaned("a");
        assert!(!t.begin("pane", "b", now));
        t.prepare("pane", "c".into(), now).unwrap();
        assert!(t.begin("pane", "c", now));
    }
    #[test]
    fn shutdown_claims_live_but_pending_belongs_to_creator() {
        let now = Instant::now();
        let mut t = Tickets::new();
        t.prepare("prepared", "a".into(), now).unwrap();
        t.prepare("pending", "b".into(), now).unwrap();
        assert!(t.begin("pending", "b", now));
        t.prepare("live", "c".into(), now).unwrap();
        assert!(t.begin("live", "c", now));
        assert!(t.complete("c", 3));
        assert_eq!(t.stop(), vec![("c".into(), 3)]);
        assert!(t.stop().is_empty());
        assert!(!t.complete("b", 2));
        assert!(!t.begin("prepared", "a", now));
        assert!(t.prepare("new", "d".into(), now).is_none());
    }
}
