# Declaration of existing code

Registered on 2026-10-27 for the Gjirafa AI Hackathon 2026, Track A (Money), card #4 E-commerce
business. This file records what the registration declared; the organisers' copy of the form is
authoritative.

## Existing: the pricing calculator (prototype, "v0.5"), running internally since July 2026

- A .NET 8 app on SQL Server (`PricingTool.Core`, `.Data`, `.Engine`, `.Web`) with **no AI model in it**.
- It reads shop data and calculates proposed prices with rules and statistics.
- It isn't connected to the shop's pricing, so nothing it calculates goes live by itself.
- Floors and targets are set by hand. People review its proposals, export them and apply them
  manually, on request.
- Its measured effect: about €10k extra gross profit a month in Aug–Sep 2026 (Aug €11,184,
  Sep €10,079, Kosovo only, before/after on applied batches). This is **not claimed** as hackathon
  benefit.

It is imported unchanged in this repo's second commit, labelled with the source commit, so it can be
checked against the original.

**Reused:** the calculation engine, the database layer with its history, the Bands page, and the
before/after profit measurement.

## New: the hackathon build

1. AI floor and target agent (Alpha).
2. Agent data tools, including the agents' own read-only queries.
3. Learning loop.
4. AI run checker.
5. Exceptions inbox (approve-first mode kept as an option).
6. Safety limits, audit log, and a 15-minute cost watcher.
7. Backtest.
8. Agent chat, including the weekly brief.
9. Discount publisher, go-live ready, writing NOP discounts the way the NOP discount module does
   (to a sandbox during the event).
10. Campaign agent (Nova).

Details: `docs/hackathon-2026/kickoff-plan.md`, section 2.

The registration text says "with a person approving its changes". The build keeps that as the
approve-first mode; the demo runs in autonomous mode, where a person handles only the exceptions.

## How the money claim relates to this declaration (added 1 Nov 2026)

The text above records the registration and is not changed. The calculator's past effect stays unclaimed: no month
before the hackathon is in the money claim. The claim is the automated system's next 12 months (the user's decision,
31 Oct): the engine's rate measured in its Aug–Sep 2026 test, here against untouched products in the same shop, category
and weeks (B0, E27: €7,597 a month, a different measure from the before/after €10k above), plus what the agents' levers
add. B0 counts in that forward claim because, as the user states, without this build the calculator stays a test run by
hand and does not go live; that is labelled **assumed (stated by the user)** wherever it is used (`HACKATHON.md`, the
one-pager, the financial sheet's Inputs).
