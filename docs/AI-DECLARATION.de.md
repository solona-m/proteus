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
Dies ist eine Übersetzung; maßgeblich ist das englische Original: [English](../AI-DECLARATION.md)

Dieses Format basiert auf
[AI-DECLARATION.md](https://ai-declaration.md/en/0.1.2). Bitte lies es, um
die oben genannten Stufen zu verstehen.

## Anmerkungen

Zunächst einmal möchte ich sagen, dass KI in der Kunst nichts zu suchen hat, dass sie zu Kunst
nicht fähig ist, und dass der Versuch, mit ihr Künstler zu verdrängen, völlig verkennt, warum wir
überhaupt Kunst machen. Meine liebste Sicht darauf findest du hier:
https://www.youtube.com/live/RXyWtp8tuYY?si=GUcD40pQut0sIiLJ

Zweitens nutzt Proteus zur Laufzeit keine KI, wenn du es verwendest. Die Algorithmen
ähneln denen, die du auch in Blender finden würdest, oder sind mit ihnen identisch, nutzen aber
in manchen Fällen FF-spezifische Gegebenheiten wie kompatible UVs, um ihre Aufgaben auf eine
Weise zu erledigen, die einem 3D-Modellierungsprogramm nicht möglich ist. Aber letzten Endes ist
es einfach ein klassisches Computerprogramm, das statische Algorithmen ausführt, die sich
beliebig oft gleich wiederholen, abstimmen und analysieren lassen.

Ich bin kein Vibe-Coder. Zum Zeitpunkt, an dem ich das schreibe, habe ich 26 Jahre Berufserfahrung
in der Softwareentwicklung, hauptsächlich in c#, der Sprache, in der dieses Plugin geschrieben ist.
Ich wurde entlassen, weil ich mich anfangs geweigert habe, KI in irgendeiner Form zu nutzen. Die
vielleicht unglückliche Wahrheit ist, dass 100 % der Software, die du in den letzten zwei Jahren
benutzt hast, in irgendeiner Form KI-Unterstützung einbezogen hat. Dieses Dokument soll offenlegen,
wie genau KI an jedem einzelnen Prozess beteiligt war. Ich beherrsche mein Handwerk und vertraue
dem Code. Es steht dir frei, ihn nicht zu nutzen, wenn du KI-generierten Code ablehnst, aber um
konsequent zu sein, müsstest du dann auch aufhören, das Internet oder irgendeine eigenständige
Software zu nutzen, die nach 2024 aktualisiert wurde.

## Architektur

Keine Beteiligung. 100 % der Entwürfe und Algorithmen stammen von mir.

## Design

Keine Beteiligung. Im Laufe der Entwicklung kam eine kleine Benutzeroberfläche hinzu, um
Colorsets zugänglich zu machen, und ich weiß, dass sie aussieht, als hätte sie ein Backend-Entwickler
gestaltet, weil es genau so war.

## Implementierung

Assist. Wenn ein Computer genutzt werden kann, um Boilerplate-Code zu tippen, routinemäßige
Tests, Dokumentation und Standard-Deployment-Skripte zu erzeugen, sodass ich wieder Zeit habe,
Kunst zu erschaffen, dient er dem größeren Wohl.

## Tests

Pair. Nicht ausgelieferter Testcode wurde von Claude implementiert.

## Dokumentation

Assist. Ein erster Entwurf der Benutzerhandbücher wurde mit einem detaillierten Prompt und einer
Analyse des Codes erzeugt und dann von Hand überarbeitet. So kann ich Dokumentation in mehreren
Sprachen anbieten.

## Review

Hint. Das ist tatsächlich der eine Bereich, in dem KI dem Endnutzer den größten Nutzen bringt.
Gerade als ich mich in dieses neue Gebiet begeben habe, wurde sie in mehreren Phasen für Code-Reviews
eingesetzt und hat erheblich zur Qualität des fertigen Produkts beigetragen, indem sie Probleme
im begrenzten Zusammenspiel mit anderen Plugins sowie in den komplexen
Bildbearbeitungsfunktionen gefunden hat. Alle wurden sorgfältig abgewogen und in der Regel von Hand
behoben, sofern die Korrektur nicht trivial war.

## Deployment

Assist. Ich bin neu bei github und bei git im Allgemeinen. Die Deployment-Skripte waren eine
Mischung aus Dingen, die aus anderen Projekten übernommen wurden (unter Beachtung der Lizenz), und
wurden von Claude für dieses Projekt angepasst.
