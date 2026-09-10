# Host input and output lifecycle (#258, #268)

The input and output halves have different terminal conditions. `InputClosed` means this backend
knows it can no longer accept input; `HasExited` and `Exited` retain their output-settled meaning.
In-process child death closes input before the settle window. Hosted clients close input at EOF,
local detach/disposal or a synchronous write failure, even if the host still reports a living child.
The host keeps the duplex output channel alive after an input failure until output settlement or
transport disconnection. A late terminal query cannot abort the output pump by trying to reply to
already closed input.

The API deliberately keeps its existing unacknowledged input transport. `pasted`, `typed`, and a
successful send-command reply mean the local transport accepted the write without a synchronous
error, not that the child read it or executed a command. Neither a host acknowledgment nor a PID
probe could establish application execution. Automation needing that proof must use an
application-level output/receipt marker and must not blindly retry a possibly partial write.
No protobuf version or cross-product ABI changes are needed for this contract.

The #258 exit-ordering test uses two private events: output begins after sink attachment; exit is
released only after the replica sees a readiness marker. The child then prints a second marker and
immediately exits; that second marker must be in the grid captured by the exit handler.
The color-reattach test already has the corresponding output-start gate. These are controlled
ordering tests, not guarantees of lossless output from a process that finishes before first attach,
or of unbounded ConPTY draining beyond the existing bounded settle window.

Isolated tests exercise early input-closed states, broadcast failure isolation, actual pane-host
query replies followed by output, and cancellation of an already-pending read. A real hosted
supersede test checks EOF without a child-exit claim. Real host/child scenarios remain in the
isolated Windows CI gate; local execution requires the canonical shared suite token.
