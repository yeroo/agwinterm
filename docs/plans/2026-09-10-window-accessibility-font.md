# Window accessibility and live font defaults (#267, #276)

Each library or quick Program owns its UI Automation context. Every root, fragment and text range
retains that context; runtime IDs include a process-unique context identity. UIA snapshot reads
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
font geometry, explicit zoom preservation and reset across library and quick windows.
