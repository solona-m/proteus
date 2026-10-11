# Product Context

Product vision, strategy, requirements, market/positioning, and any
domain/compliance notes that shape *what* gets built and *why*. This is the RAG
context the agents read to ground their work in the product, not just the code:

- The **planner** reads relevant docs here before drafting a plan.
- The **plan-critic** checks the plan for consistency with the product vision
  and strategy documented here (its method 4, "Consistency with prior decisions
  and product intent").

Add whatever your product needs, e.g.:

- `vision.md` — what the product is and the outcome it exists to produce
- `strategy.md` — the current strategy / roadmap / MVP scope
- `requirements/` — detailed feature or field specifications
- `compliance.md` — domain/legal/regulatory constraints, if any

Delete this README once you add real docs.
