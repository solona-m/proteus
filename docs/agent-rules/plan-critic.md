# Project lenses — plan-critic

Project-specific extension of the AI Workflow plugin's `plan-critic` skill.
The skill reads this file on every critique and gives the areas below
explicit attention while applying its four methods (pre-mortem, inversion,
load-bearing assumptions, consistency). The skill itself is project-agnostic
and plugin-owned; **this file is owned by the project** (the plugin never
overwrites it — later `/init-workflow` runs only add content you confirm).

These are NOT a checklist; they direct extra attention. Write 4–7 concrete
lenses — the places where generic plans regularly miss issues that matter for
*this* product. Good lenses name a concrete failure surface and why it is
easy to get wrong, e.g.:

- "[Sensitive-data category] must never leak into [context]; any plan
  touching [visibility / export / analytics] needs explicit consent
  consideration."
- "[State machine X] visibility/permission rules across [viewer types];
  the state space is non-obvious."
- "[Abuse vector] on [flow]: spam, enumeration, scraping, harassment."

## Lenses

When applying the four methods, give explicit attention to:

- **Verification against the game's real inputs.** Unit tests on synthetic
  meshes routinely pass while the game shows a hole or a seam. Does the plan
  prove its harness matches a real game export, and measure *inside* the pass
  rather than on an exported model (export re-welds and hides the defect)?
  Does it check mesh validity (folded triangles), not just smoothness?
- **Body, race and mod variety.** Neolithe, Rue, YAB, Bibo+, TBSE and others
  differ in topology; head and body race codes differ; a body may have no
  crotch; a garment may be made for an older body. A plan that only names one
  body or race has an untested load-bearing assumption.
- **Persistent edits to someone else's mod.** Studio and refit write into the
  user's Penumbra mods; the change survives with Proteus off and travels with
  exports. What is the undo/backup, what happens to saved settings (Penumbra
  keys by group position), and what if the mod updates underneath?
- **The composite pipeline's timing.** Penumbra/Glamourer events arrive on
  other threads and in bursts; redraws, stale caches and restart starvation
  have each produced "not drawn" bugs. What triggers the change, what cancels
  it, and what stale output can the game still be pointing at?
- **Performance on the game's frame.** Compositor and shell builds cost real
  time per layer. Does the plan measure first (`PhaseCounter`, the refresh
  timeline) rather than assume where the time goes, and keep heavy work off the
  framework thread?
- **Shipping to every language and every machine.** New UI text needs all 8
  locales translated; native code must run on non-AVX2 CPUs; tests must not
  depend on local data being present.
