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

Ceci est une traduction ; l'original anglais fait foi : [English](../AI-DECLARATION.md)

Ce format est basé sur
[AI-DECLARATION.md](https://ai-declaration.md/en/0.1.2). Veuillez le lire pour
comprendre les niveaux ci-dessus.

## Remarques

Tout d'abord, permettez-moi de dire que l'IA n'a pas sa place dans l'art, qu'elle est incapable de faire de l'art,
et que vouloir s'en servir pour remplacer les artistes, c'est passer à côté de la raison même pour laquelle nous faisons de l'art.
Mon point de vue préféré sur la question se trouve ici :
https://www.youtube.com/live/RXyWtp8tuYY?si=GUcD40pQut0sIiLJ

Ensuite, Proteus n'utilise pas d'IA à l'exécution quand vous vous en servez. Les algorithmes
sont semblables ou identiques à ce que vous pourriez trouver dans Blender, mais dans certains
cas ils tirent parti de particularités propres à FF, comme les UV compatibles,
pour accomplir leurs tâches d'une façon dont un logiciel de modélisation 3D est incapable. Mais au
bout du compte, ce n'est qu'un programme informatique classique qui exécute des algorithmes
statiques, que l'on peut répéter de manière cohérente, ajuster et analyser.

Je ne fais pas de « vibe coding ». À l'heure où j'écris ces lignes, j'ai 26 ans d'expérience professionnelle
en développement, principalement en c#, le langage utilisé dans ce plugin. J'ai
perdu mon emploi parce que j'avais d'abord refusé d'utiliser l'IA sous quelque forme que ce soit. La réalité, peut-être
regrettable, c'est que 100 % des logiciels que vous avez utilisés ces deux dernières
années ont intégré une assistance par IA d'une manière ou d'une autre. Ce document a pour but
d'apporter de la transparence sur le niveau exact d'implication dans chaque processus.
Je connais mon métier et j'ai confiance en ce code. Libre à vous de ne pas l'utiliser
si vous refusez d'utiliser du code généré par IA, mais pour être cohérent, il vous faudrait
aussi cesser d'utiliser Internet ou tout logiciel autonome mis à jour
après 2024.

## Architecture

Aucune implication. 100 % des conceptions et des algorithmes ont été réalisés par moi.

## Design

Aucune implication. Au fil de son évolution, une petite interface a été ajoutée pour afficher les colorsets, et
je sais qu'elle a l'air d'avoir été conçue par quelqu'un qui fait du backend, parce que c'est le cas.

## Implémentation

Assist. Si un ordinateur peut servir à taper du code répétitif, à générer des
tests routiniers, de la documentation et des scripts de déploiement standard, d'une manière qui me libère pour
retourner créer de l'art, il sert le bien commun.

## Tests

Pair. Le code de test, qui n'est pas livré, a été écrit par Claude.

## Documentation

Assist. Une première ébauche des guides d'utilisation a été générée à partir d'un prompt
détaillé et d'une analyse du code, puis retouchée à la main. Cela me permet de proposer
la documentation en plusieurs langues.

## Relecture

Hint. C'est en fait le domaine où l'IA apporte le plus de valeur à l'utilisateur
final. Surtout à mes débuts dans ce nouveau domaine, elle a servi à relire le code à
plusieurs étapes et a grandement contribué à la qualité du produit fini,
en repérant des problèmes dans les interactions limitées avec d'autres plugins ainsi que dans les
fonctions complexes de manipulation d'images. Tous ont été examinés attentivement et
généralement corrigés à la main, sauf quand la correction était triviale.

## Déploiement

Assist. Je débute avec github, et avec git en général. Les scripts de déploiement
sont un mélange d'éléments repris d'autres projets (en tenant compte de la
licence) et adaptés par Claude pour ce projet.
