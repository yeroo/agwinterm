use std::sync::Mutex;

pub fn valid(cols: u32, rows: u32) -> bool {
    (1..=10000).contains(&cols) && (1..=10000).contains(&rows)
}

// Keep the complete repaint (including its pause) in the same transaction as a real resize.
pub fn transaction<T>(gate: &Mutex<()>, action: impl FnOnce() -> T) -> T {
    let _held = gate.lock().unwrap();
    action()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Arc, mpsc};
    #[test]
    fn real_resize_waits_for_repaint_restore() {
        let gate = Arc::new(Mutex::new(()));
        let size = Arc::new(Mutex::new((100, 24)));
        let (jiggled_tx, jiggled_rx) = mpsc::channel();
        let (restore_tx, restore_rx) = mpsc::channel();
        let (attempt_tx, attempt_rx) = mpsc::channel();
        let (done_tx, done_rx) = mpsc::channel();
        let repaint = {
            let gate = gate.clone(); let size = size.clone();
            std::thread::spawn(move || transaction(&gate, || {
                let original = *size.lock().unwrap();
                *size.lock().unwrap() = (100, 23);
                jiggled_tx.send(()).unwrap();
                restore_rx.recv().unwrap();
                *size.lock().unwrap() = original;
            }))
        };
        jiggled_rx.recv().unwrap();
        // Directly prove that the shared gate stays held during the controlled repaint pause.
        let locked_during_pause = gate.try_lock().is_err();
        let real = {
            let gate = gate.clone(); let size = size.clone();
            std::thread::spawn(move || {
                attempt_tx.send(()).unwrap();
                transaction(&gate, || { *size.lock().unwrap() = (132, 40); });
                done_tx.send(()).unwrap();
            })
        };
        attempt_rx.recv().unwrap();
        restore_tx.send(()).unwrap();
        repaint.join().unwrap(); real.join().unwrap(); done_rx.recv().unwrap();
        assert!(locked_during_pause);
        assert_eq!(*size.lock().unwrap(), (132, 40));
    }
    #[test]
    fn dimensions_are_bounded_before_allocation() {
        for (c, r) in [(0,24),(80,0),(10001,24),(80,10001),(u32::MAX,24)] {
            assert!(!valid(c,r));
        }
        assert!(valid(1,1)); assert!(valid(10000,10000));
    }
}
