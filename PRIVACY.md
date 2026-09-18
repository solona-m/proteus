# Proteus privacy notice

*Last updated: 2026-09-18*

Proteus collects **nothing** unless you choose to share usage statistics. The first time you open
Proteus, it asks you once. You can change your answer at any time in **Settings → Privacy**.

This notice covers that one optional feature. Proteus also downloads files it needs (UV maps and
starter glow effects) from `dl.solona.info` and GitHub. Those downloads send no information about
you or your use of the plugin, and nothing about them is stored.

## Who is responsible

Proteus is made by **Solona**, who is the data controller for the usage statistics. To reach Solona:

- open an issue at <https://github.com/solona-m/proteus/issues>;
- or ask in the Discord at <https://discord.gg/solona>.

**Please never post your install ID in a public issue or channel.** To delete your data, use the
button described under [Your rights](#your-rights).

## What is collected, if you opt in

Proteus sends one small report a day. Each report contains only:

| Field | Example | Why |
|---|---|---|
| Install ID | `0f8fad5b-d9cb-469f-a165-70867728950e` | A random number created when you opt in, so that one person using a feature 50 times isn't mistaken for 50 people. It isn't derived from your PC, game account, character or anything else, and it is deleted if you opt out. |
| Day | `2026-09-18` | Which day the counts are for. |
| Plugin version and build | `2609.1.0.0`, `924` | To see whether a problem belongs to an old version. |
| Language | `en` | The language Dalamud shows its interface in, so translation work goes where it's needed. |
| Feature counts | `{"composite": 12, "studio_open": 1}` | How many times each Proteus feature was used that day. |

The full list of features that can be counted is
[`stats/src/features.js`](stats/src/features.js). The server refuses any report containing anything
else.

**Never collected:**

- your character, account or world;
- the names of your mods, files or folders;
- what you paint, import or wear;
- chat;
- your IP address. See below.

### Your IP address

Your PC's IP address necessarily reaches Cloudflare when the report is sent, as with any
connection over the internet. The receiving code never reads it, and request logging is turned off,
so it isn't stored with your report or anywhere else by Proteus.

## Why, and on what legal basis

The purpose is to learn which features people actually use, so development time goes to the right
places. The legal basis is your **consent** (GDPR Article 6(1)(a)).

- Saying no has no effect on how Proteus works.
- You can withdraw consent at any time by unticking **Share usage statistics** in **Settings →
  Privacy**. That also deletes what was already collected (see below).

## Where it is stored, and for how long

Reports are stored in a database run by **Cloudflare, Inc.** (Cloudflare Workers and D1), which acts
as a data processor under
[Cloudflare's Data Processing Addendum](https://www.cloudflare.com/cloudflare-customer-dpa/).

- The database is placed in Western Europe.
- Cloudflare may still process data outside the EEA. Transfers are covered by the EU Standard
  Contractual Clauses and the EU–US Data Privacy Framework, both of which Cloudflare's DPA
  incorporates.

**Each report is deleted automatically 13 months after it is received.** Only aggregate figures
worked out from the reports are kept after that, such as "40% of users used the Studio this month".
Those figures can't be traced back to anyone.

## Your rights

Under the GDPR you have the right to:

- access your data;
- have it corrected or deleted;
- restrict or object to its processing;
- receive it in a portable form;
- withdraw consent.

The simplest ways:

- **Delete everything:** go to **Settings → Privacy → Delete my data**. All reports sent under your
  install ID are erased from the server at once, and Proteus stops collecting. Unticking **Share usage
  statistics** does the same.
- **See your data:** Settings → Privacy shows your install ID. The only thing ever stored against it
  is the table above. For a copy, contact Solona through a private channel (a Discord direct
  message), not a public post.

You also have the right to complain to your local data protection authority.

## Changes

Any change to what is collected will be listed here first. If a change would collect something new,
Proteus will ask you again rather than carry your earlier answer over.
