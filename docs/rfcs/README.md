# RFCs (design questions still open)

An RFC presents options and a recommendation so a decision can be made; it
is not itself the decision. When the question is settled, write the
[ADR](../adrs/) and link it from the RFC's Status line.

There are no open RFCs today. The first event hook (`on_player_join`)
shipped as part of the zdtd alignment (ADR 0007) without an RFC; the
candidate questions still needing one before further work starts:

- Which further game events (entity killed, world saved, chat received)
  should the ABI expose, with what shape, and does the per-call fuel model
  hold for them?
- How should the init boot payload work (what does the host pass, and in
  what format)?
- When should export names gain versioned forms, and what compatibility
  policy applies?

Use [TEMPLATE.md](TEMPLATE.md) to open one.
