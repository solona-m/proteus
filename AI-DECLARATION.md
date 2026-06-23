---
version: 0.1.2
level: assist
processes:
  architecture: none
  design: none
  implementation: assist
  testing: pair
  documentation: assist
  review: hint
  deployment: assist
---

This format is based on
[AI-DECLARATION.md](https://ai-declaration.md/en/0.1.2). Please read this to
understand the levels above.

## Notes

First of all, let me say that AI has no place in art, it is incapable of art,
and trying to use it to displace artists misses the point about why we do art
in the first place. My favorite take on this can be found here:
https://www.youtube.com/live/RXyWtp8tuYY?si=GUcD40pQut0sIiLJ

However, if a computer can be used to type boilerplate code, generate rote
tests, documentation and standard deployment scripts in a way that frees me to
get back to creating art, it is serving the greater good.

I am not a vibe coder. As of this writing, I have 25 years of professional
development experience, primarily in c#, the language used in this plugin. I
was laid off because I initially refused to use AI in any capacity. The perhaps
unfortunate reality is that any software you've used in the last two years has
incorporated AI assistance in some capacity. This document is meant to provide
transparency about the exact level of involvement in each process. I know my
craft and I am confident in the code.

## Architecture

No involvement. Honestly, there was not much to architect. Proteus is not
complicated. My initial design principle would be that it would basically just
be a headless filesystem watcher. The user should be able to install and
manipulate overlays mods using all the standard mod management tools like
penumbra priorities and glamourer designs.

## Design

No involvement. As it evolved, a small UI was added to surface colorsets, which
I know looks like it was designed by a backend dev because it was.

## Implementation

Assist. As this was my first plugin, I often queried Claude to learn and deeply
understand plugin structure and interfaces. Boilerplate code and comments were
generated in small, verifiable chunks using precise prompts. The core logic at
the heart of the plugin was mostly done by hand, although some of the image
manipulation functions were corrected by Claude in code review.

## Testing

Pair. Test description and coverage were decided by human and carefully
reviewed after. Test code was implemented by Claude.

## Documentation

Assist. An initial draft of the user guides was generated with a detailed
prompt and analysis of the code, then hand edited.

## Review

Hint. This is in fact the one area where AI provides the most value to the end
user. Especially as I entered this new area, it was used to code review at
several stages and greatly contributed to the quality of the finished product,
catching issues in the limited interaction with other plugins as well as the
complex image manipulation functions. All were carefully considered and
generally fixed by hand unless the fix was trivial.

## Deployment

Assist. I'm new to using github, and git in general. The deployment scripts
were a mix of things taken from other projects (taking into account the
license) and adapted by Claude for this project.
