# ADR 0001: Record architecture decisions

## Status

Accepted

## Context

This project is developed largely by AI coding agents orchestrated through
the `/feature` workflow. The agents ground their work in documented
decisions: the planner reads ADRs before drafting, the plan-critic checks
plans for consistency against them, and the code-critic flags diffs that
contradict them as `FAIL`. A decision that lives only in a chat transcript
or in someone's head is invisible to every future session — it will be
silently re-litigated instead of respected.

## Decision

Every significant, hard-to-reverse decision — a chosen technology, a
boundary, a data model, a security model — is recorded as a numbered ADR in
this directory, using the format described in `docs/adr/README.md`. Superseded
decisions are not deleted; their status is updated to point at the ADR that
replaces them.

## Consequences

Easier: agents and humans share one durable decision log, and plans or diffs
that contradict a past decision get flagged instead of merged by accident.
Harder: a decision must be written down to count — the discipline of writing
the ADR is the price of having it enforced.
