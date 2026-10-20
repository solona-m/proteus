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

Second, Proteus does not use AI at runtime when you use it. The algorithms
are similar or identical to what you might find in Blender, but in some 
cases take advantage of FF specific circumstances like compatible UVs
to accomplish its tasks in ways that a 3D modeling program can't. But at 
the end of the day, it's just a classical computer program running static 
algorithms that can be repeated consistently, tuned and analyzed.

I am not a vibe coder. As of this writing, I have 26 years of professional
development experience, primarily in c#, the language used in this plugin. I
was laid off because I initially refused to use AI in any capacity. The perhaps
unfortunate reality is that 100% of the software you've used in the last two 
years has incorporated AI assistance in some capacity. This document is meant
to provide transparency about the exact level of involvement in each process. 
I know my craft and I am confident in the code. You're welcome to not use it 
if you refuse to use AI generated code, but to be consistent, you'd need to 
also stop using the internet or any standalone software that has been updated
after 2024.

## Architecture

No involvement. 100% of the designs and algorithms were done by me.

## Design

No involvement. As it evolved, a small UI was added to surface colorsets, which
I know looks like it was designed by a backend dev because it was.

## Implementation

Assist. If a computer can be used to type boilerplate code, generate rote
tests, documentation and standard deployment scripts in a way that frees me to
get back to creating art, it is serving the greater good.

## Testing

Pair. Non-shipping test code was implemented by Claude.

## Documentation

Assist. An initial draft of the user guides was generated with a detailed
prompt and analysis of the code, then hand edited. This allows me to offer
documentation in multiple languages.

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
