# Architecture Decision Records

One file per significant, hard-to-reverse decision (a chosen technology,
a boundary, a data model, a security model). The agents read these:

- The **planner** reads relevant ADRs before drafting and flags any plan that
  contradicts one.
- The **plan-critic** checks the plan for consistency against them.
- The **code-critic** reads them, lists which it read, and flags any diff that
  contradicts an ADR as `FAIL`.

Keep them short and durable. A common lightweight format:

```markdown
# ADR NNNN: [Title]

## Status
Proposed | Accepted | Superseded by ADR-XXXX

## Context
[The forces at play — technical, product, organizational — that make this a
real decision.]

## Decision
[What we decided, stated plainly.]

## Consequences
[What becomes easier and what becomes harder as a result.]
```

Delete this README (or keep it as a guide) once you add real ADRs.
