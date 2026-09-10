# Window accessibility and live font defaults (#267, #276)

Each library or quick Program owns its UI Automation context. Every root, fragment and text range
retains that context; child runtime IDs append their context identity to the HWND host identity.
Session fragments use lifetime tokens, never mutable sidebar ordinals, including queued focus
resolution. A removed session cannot redirect a retained fragment to its replacement. UIA snapshot reads
run on the owning UI thread (inline for reentrant same-thread calls), and posted actions recheck
closure before touching the window. No provider lock spans app callbacks or UIA event delivery.
Window destruction retires its root COM references and clears callbacks. Retained text ranges
refuse access after closure instead of finding a different active window.

Font-size inheritance is separate from its numeric value. A live default change updates unzoomed
normal, scratch, overlay and quick surfaces across windows. Explicit zoom survives even when it
happens to equal the default; reset resumes inheritance. Pane state records the nullable FontZoomed
flag: old files infer inheritance from equality with the current default, new files preserve the
distinction explicitly. An inherited saved size follows the current default on restoration.

Validation uses actual built providers in isolated tests, plus the private-job/token-protected
Accessibility fixture: distinct documents/runtime IDs, owner-local focus and Invoke, and retained
ranges across closing two windows while quick remains readable. This tests UIA provider behavior,
not a manual Narrator/NVDA presentation audit. Navigation integration additionally checks live
font geometry, explicit zoom preservation and reset across library, quick, scratch, pane-overlay
and session-overlay surfaces. Active-target font commands use the visible surface, not its hidden
underlying shell. Legacy absent FontZoomed keys remain absent on round-trip; explicit true/false
values are preserved. The private UIA client may cache an old property when a node retires; tests
verify it never rebinds that name or a focus request to the replacement, and isolated provider
tests verify UIA_E_ELEMENTNOTAVAILABLE directly.
