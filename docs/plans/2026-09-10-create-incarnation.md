# Lost-create-reply reconciliation (#279)

Implementation and validation plan; shipping remains gated on review and exact-head CI.

- Add an explicitly versioned optional hello capability, preserving v2 framing and legacy clients.
- Before spawning, a capable client obtains a host-issued random creation ticket for its pane ID.
  A lost preparation reply cannot leave a child: preparation allocates metadata only.
- A ticket authorizes at most one create. The first create claims it; duplicate creates query the
  existing result rather than spawn again. Creation with an unknown/expired/cancelled ticket refuses.
  Never reallocate or replay a create under a new ticket after an ambiguous create reply.
- Prepared tickets expire and are bounded. Creating/live tickets remain tied to their exact hosted
  object. Completed deletion may discard a ticket because unknown tickets cannot create anything.
  This avoids an unbounded permanent tombstone collection and prevents late-create resurrection.
- Query and cancellation address the ticket plus pane ID, not a reused pane ID alone. Cancellation
  during spawn records a pending obligation; publication checks it and disposes only that attempt.
  Do not report completed cleanup merely because cancellation was recorded.
- Attach, resize, detach and explicit close carry an expected ticket when available. Adoption learns
  the existing ticket from attach. Preserve app-quit detach, including an ambiguous in-flight create.
- Retire a control connection after ambiguous transport failure; reconciliation uses a new connection,
  never reads another reply on the damaged stream and never starts a replacement host just to clean up.
- Mirror protocol and lifecycle semantics in both C# and Rust hosts, C# and Lite clients, and generated
  bindings. Legacy peers retain documented v2 limitations; no release/tag/install is part of this work.
- Deterministic tests cover lost preparation/create replies, duplicate create, cancellation before and
  during spawn, pane-ID reuse, expired tickets, failed spawn, host shutdown, adoption and app detach.
  Run pure ledger/fault fixtures privately; actual host tests require the canonical lease or isolated CI.

## Capability and deployment boundary

- `HelloReply.creation_revision = 1` enables the ticket protocol; absent/zero means legacy v2.
  A legacy Create without a ticket remains supported. It cannot reconcile a lost Create reply
  by exact incarnation, and an old client cannot guard a reused pane ID. Do not claim otherwise.
- Merely updating a client does not upgrade an already-running host. Lite's existing pinned host
  stays unchanged here; ticket behavior activates only with a host advertising the capability.
  Releasing/staging a newer host is separate work, not authority to replace a running user's host.
- Cancellation acceptance (`CREATION_CANCELLING`) is not disposal proof. `CREATION_UNKNOWN`
  after cancellation on the issuing host means this ticket cannot create and no child is still owned
  by its attempt. Fresh recovery connections must handshake and match the original Hello host PID;
  another process serving the same pipe name cannot prove that original host's ticket absent.
  Unproven disposal retains the exact hosted object, releases exclusive cleanup ownership and is
  retried by the host cleanup worker. Both hosts wait for the ledger to drain before shutdown;
  Rust's 30-second warning is diagnostic, not an abandonment deadline.

## Evidence before review

- 37 private C# ledger/client/framing/cleanup/lifecycle checks and four Rust ledger checks pass.
- The same real-pipe lost-reply/id-reuse scenario passes against both C# and Rust hosts.
  Canonical token generation 136 released after the exact owned job reached zero descendants;
  clipboard, HKCU and foreground were untouched. This does not substitute for full integration CI.
- Lite's actual single-attempt coordinator passes 90 private fake-exchange checks; native build passes.

## Review-fix evidence

- 39 private C# checks and five Rust ledger checks pass, including failed cleanup handoff/reclaim
  before and after publication, double-disposal exclusion and shutdown drain.
- Three managed real-host checks plus Rust's real-ConPTY publication-barrier test pass. Both hosts
  now exercise cancellation after a real child starts but before publication, asserting no published
  session, exact-ticket absence and original-process exit. Canonical token generation 137 released
  after owned job zero; no clipboard, HKCU or foreground access.
- Orphan reaping carries the listed incarnation. C# and Lite fresh cancellation both bind host PID.
