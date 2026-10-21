# Proteus

<!--i18n-->
[English](../README.md) · [日本語](README.ja.md) · [Deutsch](README.de.md) · **Français** · [简体中文](README.zh.md) · [한국어](README.ko.md) · [Español](README.es.md) · [Русский](README.ru.md)
<!--/i18n-->

Proteus est un plugin Dalamud pour FFXIV qui offre les fonctionnalités suivantes :

- **Composez des textures de calque.** Proteus ajoute des calques sur la peau et l'équipement de votre personnage en temps réel, que vous pouvez activer et désactiver comme des mods ordinaires. Les calques sont appliqués sur tous les UV compatibles, comme bibo/gen3/gen2/vanilla.
- **Laissez l'équipement s'ajuster tout seul quand vous le portez.** Choisissez votre corps et votre taille une fois par collection Penumbra, et l'équipement de torse, de jambes, de mains et de pieds, ainsi que les accessoires qui couvrent le corps, comme une veste sur un collier, y sont ajustés dès que vous les portez.
- **Studio.** Fournit des outils semblables à ceux de Blender pour modifier les maillages directement sur votre personnage : corriger les endroits où la peau passe au travers, ajouter des effets de vent, et adapter d'un coup un équipement à de nombreux corps en quelques secondes.
- **Portez des mods sans sacrifier un emplacement d'équipement.** Une seconde peau doit forcément être dessinée comme un objet, mais Proteus la dissimule sur quelque chose que vous n'utilisez pas — des lunettes invisibles, une bague que vous ne portez pas, ou en l'ajoutant à vos accessoires équipés — de sorte que votre glamour réel n'est pas touché. Il n'y a rien à configurer : Proteus choisit l'hôte tout seul et ne prend jamais un objet que vous portez.
- **Ajoutez des interrupteurs à n'importe quelle pièce de n'importe quel mod, pas seulement ceux de Proteus.** Quand un mod soude un nœud, un collier ou une sangle dans une géométrie que son auteur n'a jamais rendue optionnelle, l'onglet **Studio** peut détacher cette pièce et lui donner un vrai interrupteur.
- **Portez des mods de cheveux faits pour une autre race.** Beaucoup de mods de cheveux ne sont faits que pour une seule race, si bien que sur toute autre, le jeu affiche sa propre coiffure à la place. Proteus plie la coiffure du mod sur la tête de votre race et l'ajoute au même mod, physique comprise : ça fonctionne, tout simplement.
- **Adaptez les cheveux moddés aux chapeaux.** Proteus plaque contre votre tête les cheveux qu'un chapeau recouvrirait, pour que le chapeau cesse de passer au travers. C'est activé par défaut, et cela n'agit que pendant que vous portez un chapeau.


Si vous avez besoin d'aide, consultez d'abord ce [guide de dépannage](../TROUBLESHOOTING.md).
Rejoignez ensuite https://discord.gg/solona et posez votre question dans le salon #help. C'est encore tout neuf, mais je corrigerai les bugs dès que possible !

Si vous êtes créateur et souhaitez faire des mods pour Proteus, lisez le [guide du créateur](../For%20Creators.md).

Comment l'IA a été utilisée, ou non, pour créer Proteus (aucune IA n'est utilisée pendant que vous jouez) : [déclaration sur l'IA](AI-DECLARATION.fr.md)

---

## Pour les utilisateurs

### Installation

Ajoutez ce dépôt https://dl.solona.info/repo.json dans l'onglet expérimental de /xlplugins.
Enregistrez, puis cherchez Proteus dans la fenêtre principale de /xlplugins.

> Déjà installé depuis `raw.githubusercontent.com/solona-m/plugins/main/repo.json` ? Cela continue
> de fonctionner et fonctionnera toujours, mais la nouvelle URL est plus fiable et n'est pas soumise
> aux limitations de GitHub.

Installez quelques mods de calques faits pour Proteus, choisissez vos options, et votre personnage se met à jour.

### Commandes de chat

| Commande | Rôle |
|---------|-------------|
| `/proteus` | Ouvre ou ferme la fenêtre d'état. |
| `/proteus config` | Ouvre la fenêtre d'état sur les Paramètres. |
| `/proteus refresh` | Recompose immédiatement, comme le bouton **Actualiser**. Pratique dans une macro. |
| `/proteus refresh full` | Reconstruit d'abord tout, puis recompose, comme un Maj-clic sur **Actualiser**. Plus lent qu'une actualisation simple. |
| `/proteus refresh silent` | L'une ou l'autre des commandes ci-dessus, sans le message dans le chat. `full` et `silent` peuvent être combinés. |

### Fenêtre d'état

Ouvrez la fenêtre d'état avec `/proteus`. Elle comporte sept onglets, et le résultat de la dernière composition (textures modifiées, mods utilisés, il y a combien de temps) s'affiche toujours en bas.

Après une mise à jour, une fenêtre **Nouveautés** s'ouvre une fois pour montrer ce qui a changé. Elle ne se rouvre pas tant qu'il n'y a rien de nouveau.

#### Mods

Liste tous les mods Penumbra qui contiennent un fichier Proteus. Cliquez sur un en-tête de colonne pour trier selon celle-ci. Survolez le nom d'un mod pour voir l'image que son auteur a livrée avec lui.

| Colonne | Rôle |
|--------|-------------|
| Act. | Active ou désactive la composition Proteus pour ce mod. |
| Mod | Le nom affiché du mod. Cliquez dessus pour ouvrir le mod dans Penumbra. |
| Prio | Priorité dans la pile de composition de Proteus. Les nombres les plus bas passent en premier (couche du bas). Glissez pour changer ; Ctrl-clic pour saisir une valeur. |
| Préréglage | Le look enregistré que porte ce mod. Choisis-en un autre pour changer sans ouvrir l'éditeur de couleurs. Affiche — pour un mod sans préréglages. |
| Couleurs | Ouvre l'éditeur de couleurs de ce mod. Skindent, l'ombre de contact et le creux sur les bords de sangle, se règle là, sous **Effets**. |

Cliquez sur **Actualiser** pour forcer une recomposition manuelle, ou faites un Maj-clic dessus pour d'abord tout reconstruire. Proteus recompose aussi automatiquement dès que vous changez une option ou un réglage Penumbra, changez d'équipement, ou changez de race ou de corps.

#### Studio

Modifie les modèles de **n'importe quel** mod installé, pas seulement ceux de Proteus, y compris les vêtements des packs que vous avez importés. Vous pouvez remodeler un vêtement pour que votre corps cesse de passer au travers, déplacer ou redimensionner certaines de ses pièces, le réajuster à un autre corps, peindre là où le vent le fait balancer, ou détacher une pièce derrière son propre interrupteur. Chaque changement est écrit dans les fichiers du mod lui-même, il **continue donc de fonctionner avec Proteus désactivé** et suit le mod si vous l'exportez.

Choisissez un mod, puis l'un de ses modèles. L'onglet s'ouvre sur la pièce de torse que vous portez (ou sur vos jambes s'il n'y en a pas), et les mods et modèles que vous portez sont listés en premier, en vert. Cliquer sur un vêtement de votre personnage ouvre son mod et son modèle, vanilla ou moddé. Toutes les listes de l'onglet peuvent être filtrées : tapez une partie d'un nom pour la restreindre. Quand un objet livre un modèle distinct par race, chacun porte le nom de sa race pour que vous puissiez les distinguer.

Les outils sont à gauche et les réglages de l'outil choisi à côté, avec les pièces du modèle derrière un bouton en haut des réglages : un clic ouvre la liste des pièces en colonne à côté, et pousse le modèle vers la droite pour qu'il reste visible. Cochez **Afficher le modèle** pour une troisième colonne avec le modèle à droite.

**L'équipement vanilla** que vous portez est aussi listé, marqué *(vanilla)*. Il s'ouvre tel quel avec n'importe quel outil, et rien n'est enregistré tant que vous ne le modifiez pas : votre première modification le copie dans un mod à part nommé d'après l'objet, *<Objet> — Proteus edit*, et les modifications suivantes y vont aussi. Supprimez ce mod dans Penumbra et il ne revient pas ; rouvrir la pièce ne le recrée pas. Avec **Taille du corps** choisi, enregistrer un réajustement crée plutôt un mod à part, voir [Taille de corps](#taille-de-corps).

Les outils :

| Outil | Rôle |
|------|-------------|
| Basculer des pièces | Sélectionne des pièces du modèle et leur donne un interrupteur. Voir [Interrupteurs de pièces](#interrupteurs-de-pièces) plus bas. |
| Tirer dehors | Pousse la surface vers l'extérieur, pour qu'un corps qui passe au travers d'un vêtement soit de nouveau couvert. Le tissu s'éloigne en ligne droite de la peau qui se trouve dessous. |
| Pousser dedans | Le même pinceau à l'envers, pour un vêtement qui se tient trop loin du corps. |
| Lisser | Lisse la surface : les bosses d'origine du vêtement, ou une traction qui a donné un résultat irrégulier. Comme le relax de 3ds Max, il rétrécit : les courbes s'aplatissent et le tissu peut s'enfoncer vers le corps. |
| Adoucir | Efface les bosses et les ondulations **sans** rétrécir : contrairement à Lisser, les courbes gardent leur forme et le tissu reste à l'écart du corps. **Force** règle sa rapidité. |
| Saisir | Appuyez sur le tissu et faites glisser : le tissu autour du pinceau suit la souris comme du caoutchouc, plus fort au centre, sans bord dur. La taille du pinceau règle la quantité de tissu entraînée, et Ctrl+Z annule une saisie. |
| Pont | Peignez par-dessus un creux, comme le sillon entre les fesses ou un pli, pour tendre le tissu tout droit par-dessus au lieu de suivre le corps jusqu'au fond. Il ne fait jamais que soulever. |
| Vent | Peint à quel point le vent du jeu fait balancer le vêtement, affiché en rouge. Maintenez Ctrl, ou réglez **Quantité** sur 0 %, pour effacer. |
| Déplacer | Fait glisser une pièce, ou quelques polygones, à l'aide de flèches : une flèche la déplace le long de cet axe, un carré dans ce plan. Voir [Déplacer, tourner et redimensionner](#déplacer-tourner-et-redimensionner) plus bas. |
| Tourner | Fait pivoter une pièce autour de son centre : faites glisser un anneau coloré pour la tourner autour de cet axe, ou l'anneau extérieur pour la tourner autour de votre vue. |
| Redimensionner | Agrandit, rétrécit ou étire une pièce autour de son centre : faites glisser une poignée colorée pour l'étirer le long de cet axe, ou appuyez sur la pièce et faites glisser vers la droite pour l'agrandir, vers la gauche pour la rétrécir. |
| Taille du corps | Réajuste le vêtement à une autre taille de corps, ou à un autre mod de corps. Voir [Taille de corps](#taille-de-corps) plus bas. |

##### Pinceau

- **Vous peignez directement sur votre personnage**, et un vêtement montre chaque trait pendant que vous le peignez. Maintenez **Alt** pour déplacer la caméra. Cochez **Afficher le modèle** pour peindre plutôt sur le modèle dans la fenêtre : glissez depuis le fond pour le tourner, Maj+glisser pour le déplacer, molette pour zoomer.
- **Taille du pinceau** descend jusqu'à 1 mm. `[` et `]` la changent, et Maj donne des pas plus fins. L'effet est le plus fort au centre et s'estompe jusqu'à rien sur le bord ; la portée du pinceau est dessinée en arc-en-ciel sur le tissu pour que vous la voyiez. Les pinceaux travaillent au même rythme quelle que soit la fréquence d'images. Pour **Tirer dehors** et **Pousser dedans**, **Force** est la distance dont le centre du pinceau déplace la surface pour chaque quart de seconde où vous le maintenez ; pour les autres pinceaux, c'est leur rapidité. Une petite valeur est généralement la bonne : un vêtement n'a besoin de dégager le corps que d'une fraction de millimètre, et vous pouvez toujours repeindre au même endroit.
- **Limite** est la distance maximale dont les pinceaux peuvent éloigner un point de l'endroit où l'auteur du mod l'a placé. La peinture s'arrête à la limite, donc un pinceau maintenu ne peut pas faire gonfler le vêtement. Augmentez-la pour un changement plus important.
- **Miroir gauche/droite** peint les deux côtés à la fois. Déplacer, Tourner et Redimensionner fonctionnent aussi en miroir.
- **La peau ne bouge jamais.** Pour tenir un pinceau à l'écart de quoi que ce soit d'autre, verrouillez la pièce : Maj+clic dessus sur votre personnage ou sur le modèle, ou décochez-la dans la liste des pièces. Une pièce verrouillée est aussi masquée, sur le modèle et sur votre personnage, pour que les clics la traversent jusqu'à ce qui se trouve dessous — verrouillez une jupe pour atteindre les sous-vêtements en dessous. Les coutures soudées à une pièce verrouillée tiennent aussi.
- **Les cheveux fonctionnent aussi.** Les cheveux, le visage, les oreilles et la queue se redessinent quand vous relâchez, au lieu de montrer le trait pendant que vous peignez.
- **Appliquer aux autres tailles** copie la modification sur les autres fichiers du mod pour le même vêtement, en les faisant correspondre selon leur position sur celui-ci.

##### Enregistrement et annulation

- Chaque trait est enregistré dans le mod un instant après que vous relâchez. Le premier enregistrement copie le modèle d'origine dans `Proteus/meshvolume-backup/` à l'intérieur du mod.
- **Annuler le trait** (ou Ctrl+Z) retire le dernier trait. **Tout reprendre** abandonne tous les traits sur ce modèle.
- **Annuler les modifications** (maintenez Ctrl ou Maj et cliquez) remet chaque modèle que le pinceau a changé dans ce mod exactement tel que son auteur l'a fait.
- Les interrupteurs de pièces, l'adaptation aux chapeaux et le pinceau gardent chacun leur propre sauvegarde. Si plusieurs d'entre eux ont modifié le même modèle, annulez d'abord le plus récent. Proteus vous prévient si vous essayez un autre ordre.
- Les curseurs de corps d'un modèle ne peuvent pas toujours suivre une modification. Quand certains points n'ont pas pu bouger avec elle, Proteus indique combien, et activer ce curseur peut faire revenir le passage au travers par endroits.
- La plupart des maillages moddés n'ont pas de canal de vent. Le premier trait de vent enregistré en ajoute un, et règle les matériaux du vêtement pour que le vent puisse le faire bouger. Les matériaux qui viennent du jeu plutôt que du mod ne peuvent pas être réglés.
- **Vos amis qui utilisent un plugin de synchronisation voient vos modifications.** Une fois qu'un trait est terminé et enregistré, les personnes appairées avec vous via un plugin de synchronisation comme PlayerSync ou Lightless reçoivent le vêtement modifié en quelques secondes, tant que le Studio reste ouvert. Les traits en cours restent privés. Si vous avez verrouillé des pièces, elles réapparaissent quelques secondes après que vous arrêtez de travailler pour que le vêtement entier puisse être partagé ; survoler ou peindre les masque de nouveau.

##### Déplacer, tourner et redimensionner

**Déplacer**, **Tourner** et **Redimensionner** agissent sur une pièce choisie du vêtement au lieu de peindre. Cliquez sur une pièce de votre personnage ou dans la vue du modèle, ou choisissez-la dans la liste des pièces, puis faites glisser les poignées en son centre. Maintenez **Alt** pour déplacer la caméra pendant que vous travaillez sur votre personnage. Chaque changement est enregistré quand vous relâchez, et **Annuler le déplacement** revient sur le dernier.

- **Pièces entières ou Polygones.** Par défaut, vous déplacez des pièces entières. Passez sur **Polygones** pour travailler sur quelques polygones à la place : cliquez sur l'un d'eux pour le sélectionner, Maj-clic pour en ajouter ou en retirer un, et **Agrandir** ou **Réduire** la sélection d'un anneau à la fois. Choisir une pièce dans la liste sélectionne tous ses polygones, et **Effacer** vide la sélection.
- **Déplacer les pièces adjacentes** laisse le tissu autour de ce que vous déplacez le suivre, d'autant moins qu'il est loin, pour que le vêtement se plie au lieu de se déchirer. **Atténuation** règle la portée. Avec des polygones, l'atténuation est mesurée le long de la surface, donc seul le tissu rattaché à votre sélection suit : une pièce séparée qui se trouve simplement à côté ne bouge pas. Avec **Déplacer les pièces adjacentes** désactivé, seule votre sélection bouge, même si les points qu'elle partage exactement avec une voisine bougent quand même pour que les coutures restent fermées.
- **Miroir gauche/droite** déplace, tourne ou redimensionne en même temps, en miroir, le tissu correspondant de l'autre côté du corps. Une pièce qui est son propre miroir, comme un soutien-gorge entier ou un ras-du-cou, bouge d'un seul bloc et reste centrée ; les déplacements et rotations latéraux sont alors ignorés. Les points sans correspondant bougent quand même de leur côté, et un seul **Annuler le déplacement** annule les deux côtés.
- **La peau et les pièces verrouillées ne bougent jamais.** Déverrouillez une pièce sous un pinceau pour pouvoir la déplacer.
- Une pièce déplacée suit toujours les os pour lesquels elle a été faite, donc une pièce éloignée d'eux peut se plier bizarrement dans les poses.

##### Taille de corps

**Taille de corps** réajuste un vêtement conçu pour un corps sur un autre : une autre taille du même mod de corps (Neolithe XS vers L), ou un tout autre mod de corps (Neolithe vers Rue, TBSE vers TBSE-X).

**Les deux mods de corps doivent être installés dans Penumbra** : celui pour lequel le vêtement a été fait et celui sur lequel vous le réajustez. Ils n'ont pas besoin d'être activés. Proteus mesure de combien déplacer chaque point du vêtement en comparant les fichiers de modèle des deux corps, il lui faut donc les deux sur le disque. La taille pour laquelle le vêtement a été fait doit aussi être l'une des options de ce mod.

1. Ouvrez le modèle du vêtement dans le Studio et choisissez **Taille du corps**. La première fois, quelques conseils s'affichent ; ils reviennent quand ils changent.
2. Sous **Conçu pour**, choisissez le mod de corps pour lequel le vêtement a été fait, puis sa taille. Proteus devine la taille pour vous et indique à quel point il en est sûr.
3. Sous **Ajuster vers**, choisissez le mod de corps et une ou plusieurs tailles. Ils démarrent sur le corps que vous portez. Cochez plusieurs tailles pour les créer toutes en une seule fois.
4. Appuyez sur **Ajuster et prévisualiser** pour voir le résultat sur votre personnage. Quand cela vous convient, appuyez sur **Enregistrer comme nouvelle option** (ou **Enregistrer *N* tailles comme nouvelles options**). Rien n'est conservé avant que vous enregistriez.
5. **Enregistrer dans** choisit où cela va. **Ce mod** l'ajoute au mod du vêtement lui-même, par défaut dans le groupe qui contient déjà les tailles de l'auteur (**Enregistrer dans le groupe** permet d'en changer), mais la prochaine mise à jour de l'auteur le supprimera. **Un nouveau mod** le garde dans un mod à part qui survit aux mises à jour. Dans les deux cas, cela continue de fonctionner avec Proteus désactivé.

Les mods que Proteus crée, pour les réajustements et pour l'équipement vanilla modifié, sont rangés ensemble dans un dossier **Agrandissements Proteus** de la liste des mods de Penumbra.

Bon à savoir :

- **Utiliser la peau du nouveau corps** est activé par défaut : la peau propre au vêtement, pour chaque partie du corps redimensionnée, est remplacée par celle du nouveau corps, afin qu'elle s'accorde au reste de vous. Décochez pour redimensionner à la place la peau livrée avec le vêtement, ce qui conserve le galbe push-up ou le skindent de l'auteur.
- **Retirer la peau cachée**, activé par défaut, omet la nouvelle peau là où l'auteur a supprimé la sienne, généralement sous le tissu. Désactivez-le si un réajustement laisse des trous. Cela ne s'applique que lorsque **Utiliser la peau du nouveau corps** est activé.
- **Ajouter les piercings du nouveau corps** désactivé par défaut, apporte les piercings du nouveau corps avec sa peau, là où le vêtement montre cette peau. Les ongles sont toujours inclus. Ne s'applique que si **Utiliser la peau du nouveau corps** est activé.
- **Sortir le tissu du corps** fait ressortir tout le tissu enfoui, pas seulement celui que le réajustement a enfoui. Utilisez-le si le corps transparaît, par exemple avec des bas qui ont leur propre peau de jambes. Désactivé par défaut, car les auteurs cachent souvent du tissu sous la peau exprès. Pour les hauts du jeu lui-même, il est toujours actif tant que **Utiliser la peau du nouveau corps** est activé, comme dans l'ajustement automatique.
- **Garder la forme** déplace les pièces rigides (bagues, boucles, chaînes) d'un bloc au lieu de les plier. Les petites pièces fermées sont cochées d'office. Une pièce qui s'enfoncerait dans le nouveau corps se courbe avec lui à la place.
- **Les chaussures et les gants gardent leurs propres pieds et mains.** Avec **Garder ses propres pieds** (ou **mains**) coché, par défaut, le pied reste exactement tel que son auteur l'a fait, talon compris, et seule la jambe que la chaussure atteint est réajustée, à la taille des jambes (pour les gants, le bras à la taille du buste). Les pieds suivent d'eux-mêmes. Une chaussure qui n'atteint aucune jambe garde sa forme et seule sa peau est redessinée, enregistrée sous « *Corps* — skin ». Décochez pour réajuster toute la chaussure comme avant.
- **Les pièces qui couvrent plus d'une partie**, comme une robe longue sur les jambes, prennent une taille pour chaque partie sous **Autres parties du corps**. Choisissez **Aucune (laisser cette partie de côté)** pour en ignorer une.
- **Décochez une pièce** dans la liste des pièces pour la laisser exactement là où l'auteur l'a mise.
- **L'équipement que le jeu ne fournit pas pour votre race**, comme les pièces faites uniquement pour les hommes, peut quand même être réajusté : Proteus crée d'abord un modèle pour votre race à partir de celui qui existe. L'équipement fait uniquement pour votre race, comme l'ensemble Auri des Ao Ra, est réajusté dans la forme hyurois pour laquelle les corps sont faits, comme le jeu le déforme, puis réenregistré pour votre race.
- **Ajuster pour** une autre race du même sexe que le vêtement. Si le mod de corps vers lequel vous réajustez a un corps pour une autre race, comme **M Highlander** de TBSE-X, **Ajuster pour** le propose. Le choisir crée à partir de celui-ci le modèle propre à cette race, comme le jeu le déforme, le réajuste au corps de cette race et l'enregistre là où cette race le charge, quel que soit votre personnage. Ce modèle reste tel quel. Cela marche aussi dans l'autre sens : un mod qui n'a qu'un modèle Montagnard peut être réajusté pour Plainien, et toute race de ce sexe sans modèle propre pour la pièce porte alors aussi cet ajustement. Si le mod a déjà son propre modèle pour cette race, Proteus vous y renvoie.
- **L'équipement vanilla** peut être réajusté de la même façon. Il n'a pas de mod dans lequel enregistrer, donc l'enregistrement crée un nouveau mod avec le réajustement et une option **Original**.
- Retouchez ensuite avec les autres pinceaux, comme **Tirer dehors**, et utilisez **Déplacer** pour les pièces rigides. Réajustez depuis la taille de l'auteur la plus proche de celle voulue ; les tailles pour d'autres corps ci-dessous le font pour vous.
- **Entre deux mods de corps**, le vêtement reprend aussi les poids d'os du nouveau corps là où les deux corps diffèrent, pour qu'il bouge avec la physique du nouveau corps. Là où ils s'accordent, la pondération de l'auteur est conservée. Les chaînes de jupe et les autres os propres au vêtement ne sont jamais modifiés.
- **La peau des deux corps doit partager une disposition de texture**, ou en utiliser une que Proteus sait convertir (bibo, gen3 et gen2). Les corps masculins et féminins ne sont réajustés l'un sur l'autre que par **Ajuster pour**, ci-dessous.
- **L'autre sexe** peut aussi être choisi sous **Ajuster pour**. Un vêtement de torse ou de jambes d'homme Midlander peut être réajusté pour les femmes Midlander, et un vêtement de jambes de femme Midlander pour les hommes Midlander, quand le mod de corps vers lequel vous réajustez a des corps de ce sexe. Choisissez sous **Fait pour le corps** le mod de corps pour lequel le vêtement a été fait. Les corps sont appariés grâce aux cartes de corps bibo↔TBSE : seuls les corps de femme en disposition Bibo+ ou du jeu s'apparient aux corps d'homme en disposition TBSE ou du jeu. Les hauts de femme ne sont pas encore proposés pour les hommes : aplatir une poitrine sur un torse retourne trop de tissu. Les vêtements riggés sur un squelette physique de femme (YAS) sont refusés pour les hommes, tout comme un réajustement qui sort avec trop de faces retournées. Toute race sans modèle propre qui se rabat sur le modèle enregistré le porte aussi. Le réajustement automatique et le jeu de tailles ne traversent jamais les sexes.
- **Les clés de forme sont perdues** quand la peau est remplacée ou que le vêtement passe d'un mod de corps à un autre. Proteus vous le signale quand cela arrive.
- **Retirer le dernier réajustement** ressort du mod une taille enregistrée.
- Seul le niveau de détail le plus élevé est réajusté, donc l'ancienne taille peut se voir à très grande distance.

##### Toutes les tailles pour d'autres corps

Sous l'outil pour un seul vêtement, **Toutes les tailles pour d'autres corps** traite un mod entier d'un coup. Cochez les mods de corps voulus, appuyez sur **Ajouter les tailles**, et chaque taille de ces corps est réajustée depuis la taille de l'auteur la plus proche (les autres pièces de la tenue dans cette option depuis la même taille), enregistrée à côté des tailles de l'auteur sous les noms "Rue S", "YAB Buff L" et ainsi de suite.

- **Utiliser la version YAS d'un corps quand il en a une** donne à Rue, YAB et Tre leurs tailles Yet Another Skeleton. Une pièce déjà pondérée pour YAS reçoit toujours des tailles YAS.
- **Inclure les chaussures et les gants**, actif par défaut, fait aussi des tailles des chaussures et des gants du mod en gardant leurs propres pieds et mains : les chaussures reçoivent les tailles des jambes (« Rue WC », « YAB SC »), les gants une option par corps. Une chaussure ou un gant dans le même groupe de tailles que le buste ou les jambes de la tenue rejoint les tailles que cette pièce a faites, et un seul choix habille toute la tenue. Celui qui n'atteint aucune jambe ni aucun bras ne fait que redessiner sa peau, et seulement là où le nouveau corps dessine la peau autrement. Celui dont la taille se choisit dans un groupe avec une pièce qu'il ne peut pas suivre (des gants à côté de jambes, ou une chaussure et des gants sans torse) est laissé tel quel, avec une note.
- **Ajouter les piercings des nouveaux corps** désactivé par défaut, ajoute les piercings de chaque corps aux tailles qu'il crée. Les ongles sont toujours inclus. Les tailles déjà faites ne changent que si elles sont refaites.
- Les tailles que le mod possède déjà sont laissées telles quelles. Cochez **Refaire les tailles que Proteus a déjà ajoutées** pour refaire celles que Proteus a créées ; les tailles propres à l'auteur ne sont jamais touchées. Si vous l'avez lancé avec une version antérieure de Proteus, cochez-la une fois : ces tailles ont été faites depuis la première taille de l'auteur.
- Cela tourne en arrière-plan et peut prendre quelques minutes sur un gros mod. **Arrêter** conserve les tailles déjà enregistrées. Une fois terminé, Proteus liste ce qu'il a dû laisser de côté et pourquoi.
- Seuls les corps des races que le mod habille sont proposés, donc un mod féminin ne liste pas de corps masculins. Un corps doit être installé dans Penumbra pour recevoir ses tailles. L'équipement d'origine du jeu n'a pas de mod auquel ajouter des tailles, donc cette section n'apparaît pas pour lui.

##### Interrupteurs de pièces

**Basculer des pièces** sort une pièce de géométrie du modèle d'un mod et la place derrière un interrupteur : un nœud, un collier, une sangle que l'auteur a soudés dans un maillage toujours visible.

L'interrupteur est écrit dans le mod lui-même sous forme d'option Penumbra ordinaire, il apparaît donc dans les réglages de ce mod.

Les pièces du modèle sont listées avec leur nombre de triangles. Cliquez une pièce sur votre personnage ou dans la vue du modèle pour la cocher. Cochez les pièces qu'un interrupteur doit masquer, donnez-lui un nom, et appuyez sur **Créer un interrupteur à partir des pièces cochées**. Empilez-en autant que vous voulez, puis appuyez sur **Écrire les interrupteurs dans le mod**.

Bon à savoir :

- **Dix interrupteurs par objet.** C'est la limite du jeu, pas celle de Proteus. Si un auteur les a déjà tous utilisés, l'onglet le signale et refuse d'en ajouter.
- **Équipement et accessoires uniquement.** Il n'y a rien à quoi rattacher un interrupteur sur les autres types de modèles.
- **Une pièce que l'auteur fait déjà basculer peut aussi recevoir le vôtre.** Les deux se cumulent : la pièce ne s'affiche que quand les deux sont activés.
- **Les packs importés acceptent aussi les interrupteurs.** Un vêtement provenant d'un pack que vous avez importé via **Importer** reçoit son interrupteur de la même manière.
- **C'est réversible.** Les modèles d'origine sont conservés, donc **Annuler – restaurer les modèles d'origine** remet le mod exactement dans son état initial et supprime le groupe d'options.
- Si un objet possède plusieurs fichiers de modèle dont les pièces sont agencées différemment, Proteus ne modifie que ceux où l'interrupteur tombe juste et vous dit lesquels il a laissés de côté, plutôt que de deviner et de toucher la mauvaise géométrie.

#### Liaisons

Lie toute votre configuration Proteus — quels mods sont actifs, leurs priorités et options, et toutes leurs couleurs — à un design Glamourer. **Lier l'état de Proteus aux designs Glamourer** est activé par défaut.

Enregistrer un design capture l'état actuel avec lui : chaque mod présent sur votre personnage, pas seulement ceux de Proteus, plus toutes les couleurs Proteus. Appliquer ce design plus tard le restaure. Les couleurs et les réglages de couche sont restaurés sous forme de calque en direct, donc les fichiers du mod ne sont jamais réécrits. Chaque liaison liste les mods qu'elle restaure ; **Appliquer** en restaure une immédiatement sans passer par Glamourer, et **Délier** l'oublie sans toucher au design.

Tant qu'une liaison est active, les modifications faites dans l'éditeur de couleurs s'affichent immédiatement en aperçu mais ne sont **pas** enregistrées avant d'appuyer sur **Mettre à jour** à côté de ce design dans l'onglet Liaisons — ce qui replie tout ce qui est à l'écran dans ce design.

| Option | Rôle |
|--------|-------------|
| Suivre l'automatisation Glamourer | Restaure une liaison quand l'automatisation de Glamourer applique son design lors d'un changement de set d'équipement ou de job. Activé par défaut. Elle ne fait jamais que restaurer, jamais effacer. |
| Restaurer tous les mods du personnage | Restaure aussi les autres mods qui étaient actifs sur votre personnage quand le design a été enregistré, désactive ceux qui ne l'étaient pas, et élève les mods du design au-dessus de tout ce avec quoi ils entrent en conflit. Désactivé par défaut : seuls les mods Proteus sont restaurés. Les autres mods sont tenus par des réglages temporaires Penumbra, votre collection elle-même n'est donc pas modifiée, et le bandeau de Penumbra sur un mod tenu permet de lever ce maintien-là. |
| Vider les emplacements que le design ne définit pas | Quand vous appliquez un design lié, vide les emplacements d'équipement et de lunettes qu'il ne définit pas et désactive les packs importés qu'il n'a pas capturés, pour qu'aucun morceau du look précédent ne subsiste. Désactivé par défaut. Non utilisé pour l'automatisation, et les armes ainsi que l'emplacement qui héberge une seconde peau sont laissés tranquilles. |

Un design enregistré avant que les liaisons ne capturent tout le personnage est marqué **Mods Proteus uniquement**. Appliquez-le et appuyez sur **Mettre à jour** pour capturer le reste.

#### Créer

Crée un mod de calque simple sans quitter le jeu. Donnez-lui un nom, un auteur, et choisissez au moins une texture (diffuse, masque, normale ou index). Le matériau cible se remplit automatiquement à partir du corps que vous portez ; vous pouvez choisir un autre matériau équipé dans la liste déroulante ou saisir un chemin à la main. Proteus écrit un nouveau mod Penumbra et l'ouvre.

Les emplacements de texture que le matériau choisi ne peut pas utiliser sont grisés.

Une cible de peau de corps couvre aussi le même corps sur toutes les autres races qui partagent sa disposition de texture, donc le mod vous suit lors d'un changement de race. L'onglet indique combien de matériaux cela représente. Les corps féminins partagent une disposition et les corps masculins une autre ; les Lalafell et les Hrothgar masculins ont la leur et ne sont pas inclus.

#### Importer

Prend un pack de mod et le convertit en mod Proteus. Trois types sont pris en charge :

**Mods Penumbra classiques (`.pmp`)** — portez des morceaux d'un mod d'équipement normal sans utiliser d'emplacement, et profitez en plus des fonctions avancées de table de couleurs. Pour convertir un mod déjà installé dans Penumbra, choisissez le `meta.json` de son dossier au lieu d'un fichier de pack. Les packs à l'ancien format de Penumbra sont d'abord mis à niveau par Penumbra, puis convertis.

Cela reste un mod Penumbra ordinaire : Penumbra continue de décider s'il est actif et quelles options sont choisies. Ce qui change, c'est que ses pièces sont dessinées sur l'objet porteur de Proteus au lieu d'un véritable emplacement d'équipement, donc votre glamour n'est pas touché.

L'effet secondaire utile, c'est que **vous pouvez porter plusieurs de ses options à la fois**. Normalement, deux options d'un même groupe revendiquent le même chemin de modèle et le jeu ne peut en afficher qu'une seule ; un pack ne peut donc physiquement pas proposer « cette pièce *et* celle-là ». Après l'import, chaque pièce sélectionnée est ajoutée séparément.

- Les pièces arrivent **désactivées**. Cochez ensuite celles que vous voulez dans Penumbra ; rien n'est porté avant.
- Un pack qui est *déjà* un mod Proteus est installé exactement tel que son auteur l'a conçu. Rien n'est converti.
- La peau est retirée pendant l'import. C'est idéal pour les accessoires comme les bijoux, les piercings et les vestes. Si vous importez une chemise, elle n'ira que si votre emplacement de torse équipé a la même taille.

**Packs de calques Onion (`.omp`)** — portez ses couches comme des calques Proteus que vous pouvez recolorer, réempiler, faire briller, etc.

Un pack qui livre le même graphisme en plusieurs dispositions UV (bibo, gen3, vanilla) devient dans Penumbra un groupe à choix unique **Body UV**, préréglé sur la disposition correspondant au corps que vous portez, afin qu'une seule soit composée à la fois. L'opacité d'une couche est intégrée à l'image ; une couche dont le mode de fusion n'est pas Normal est ignorée, avec un message, car Proteus ne compose qu'en alpha-over. Les groupes d'options et les filtres de race propres à Onion ne sont pas importés.

**Tatouages lumineux Atramentum Luminis (`.ttmp2`)** — portez la lueur comme un calque Proteus que vous pouvez recolorer et atténuer, sans aucun mod de shader.

Les packs Atramentum Luminis cachent leur lueur dans le canal alpha d'une texture, et sans ce mod de shader installé, ils n'affichent absolument rien. Proteus extrait la lueur et la reconstruit en calque ordinaire : les panneaux marqués par l'artiste deviennent une seconde peau, et le graphisme lui-même pilote un matériau à lueur animée, si bien que le néon garde ses propres couleurs pixel par pixel. Le curseur **Lueur** dans Couleurs fait alors ce que vous attendez, et vous pouvez lier l'ensemble à un design comme n'importe quel autre calque.

- La texture de corps du pack est importée elle aussi, sous forme d'option distincte **Peau de l'auteur**, activée par défaut : elle porte les parties du tatouage qui ne brillent pas, et elle conserve votre propre teint plutôt que celui de l'auteur. Décochez-la dans Penumbra si vous ne voulez que la lueur.
- Proteus reconnaît bibo et gen3 directement. Pour tout autre corps, il peint sur celui que vous portez sans redimensionner, et le signale ; le sélecteur **Corps** permet de forcer autre chose si le pack a été fait pour un autre modèle.
- Il n'y a ni filtre de race ni filtre de sexe, donc le mod peint n'importe quel personnage sur un corps utilisant le même matériau. Désactivez-le dans Penumbra pour les personnages auxquels il n'était pas destiné.
- La lueur des yeux n'est pas importée pour l'instant, mais écrivez-moi si cela vous intéresse.


**Préréglages (`.ptp`)** — un look que quelqu'un a partagé pour un mod que tu as déjà.

Un préréglage n'est pas un mod, donc rien n'est installé : Proteus lit pour quel mod il a été fait, propose de l'ajouter à celui-là, et te le dit si tu ne l'as pas sous ce nom — tu choisis alors le bon mod toi-même. Il est enregistré, pas porté ; porte-le depuis la section Préréglages de ce mod dans Couleurs quand tu le veux.
#### Exporter

Enregistre un de vos mods Proteus en pack de mod Penumbra (`.pmp`) pour le partager. Choisissez le mod dans la liste, appuyez sur **Exporter**, et choisissez où le mettre — le nom de fichier est prérempli à partir du nom du mod, et la boîte de dialogue s'ouvre sur votre bureau la première fois, puis là où vous avez enregistré la dernière fois.

Le pack est une copie directe du dossier du mod, donc rien n'est perdu : options, tables de couleurs, masques, effets de lueur et couches d'équipement suivent tous, et le Proteus du destinataire les récupère dès que Penumbra les installe. Les mods désactivés peuvent aussi être exportés.

#### Paramètres

| Paramètre | Rôle |
|---------|-------------|
| Activé | Interrupteur principal. Désactivé, Proteus efface sa sortie, vous redessine sans elle, désactive le mod « Proteus » géré dans Penumbra et arrête les liaisons de design. Toutes les fonctions de Proteus se mettent en retrait. |
| Redessin automatique | Laisse Proteus suivre tout seul : il recompose après un changement de zone, un changement d'équipement ou un redessin, puis recharge votre personnage pour que vous voyiez le résultat. Désactivé, Proteus devient essentiellement manuel. Votre look reste en place, mais une modification n'apparaît pas tant que rien ne vous redessine. |
| Élever automatiquement la priorité du mod | Quand un autre mod est confirmé comme remplaçant une texture de peau dans laquelle Proteus compose, élève la priorité Penumbra de Proteus au-dessus de lui et le signale dans le chat. |
| Rechargement sur place | Rafraîchit les textures via Glamourer au lieu d'un redessin complet, ce qui évite le clignotement de disparition/réapparition. Activé par défaut. |
| Activer la compression | Compresse par blocs les textures cuites, réduisant leur taille sur le disque et en VRAM à environ un quart. Désactivé par défaut : c'est ce que Proteus fait de plus lent, et sur un PC ancien cela peut retenir votre look pendant une minute ou plus à chaque changement. Ne l'activez que si vous manquez de mémoire vidéo. Sur un PC trop lent pour elle, Proteus laisse les textures non compressées même si la case est cochée, et vous dit pourquoi. |
| Alpha net | Expérimental. Garde les sphere maps et la métallicité fonctionnelles en pose de groupe, au prix de bords plus durs sur les tissus transparents. |
| Cache de textures (Mo) | Quantité de données de texture décodées gardées en mémoire entre deux compositions. |
| Masquer les maillages de corps redondants | Ignore la peau que la seconde peau dessinerait deux fois : anneaux de renfort d'articulation qu'une partie voisine couvre déjà, et copies superflues d'une région. Activé par défaut, sûr sur n'importe quel corps. |
| Héberger sur des lunettes invisibles | Laisse la seconde peau occuper l'emplacement d'accessoire de visage pour garder vos bagues libres. |
| Atténuation de la teinte de peau | À quel point les calques résistent à être teintés par votre carnation. |
| Occlusion ambiante / Douceur de l'ombre / Skindenting | Force globale de l'ombre de contact et du creux de normale autour des bords de sangle. |
| Réagir à la lumière de la scène | Permet aux lignes de couleur marquées **S'estompe à la lumière** de s'atténuer à mesure que la lumière sur vous augmente. Désactivé, chaque lueur brille à pleine intensité partout. |
| Définir le niveau de lumière à la main / Niveau de lumière | Ignore la scène et utilise le curseur à la place. C'est le moyen le plus rapide de voir une lueur réservée à l'obscurité sans attendre le crépuscule, et le bon réglage pour la pose de groupe. |
| Lueur d'arrière-plan | La douce lueur de braise qui dérive derrière la fenêtre. Activée par défaut. |
| Réduire les animations | Garde la fenêtre immobile : les effets de survol s'appliquent d'un coup, et la lueur d'arrière-plan ainsi que la pulsation des mods actifs cessent de bouger. |

Quatre boutons ici méritent d'être connus :

- **Copier les journaux** — lance un rafraîchissement complet et enregistre dans un fichier texte sur votre bureau tout ce que Proteus journalise pendant ce temps, votre nom d'utilisateur Windows retiré. Joignez ce fichier quand vous signalez un problème.
- **Restaurer l'accessoire modifié** — force un redessin complet si une seconde peau reste coincée sur une bague ou un bracelet après une désactivation ou un échange.
- **Vider le cache de textures** — à utiliser quand une modification de texture n'apparaît pas, par exemple après avoir réexporté un calque à la même taille.
- **Textures d'effet lumineux** — ouvre le dossier dans lequel Proteus lit les cartes de défilement de lueur animée. Déposez-y des images et elles apparaîtront dans la liste Effet de chaque calque d'équipement. Survolez le bouton pour voir le chemin complet.

##### Cheveux

La section **Cheveux** adapte les mods de cheveux à votre personnage de deux façons : les cheveux faits pour une autre race sur la tête de votre race, et les cheveux sous les chapeaux. Comme le Studio, les deux modifient les fichiers du mod de cheveux lui-même, donc ils continuent de fonctionner avec Proteus désactivé et suivent le mod si vous l'exportez.

**Cheveux faits pour une autre race.** Beaucoup de mods de cheveux ne sont faits que pour une seule race, si bien que sur toute autre race, le jeu affiche sa propre coiffure à la place. Avec **Adapter les mods de coiffure faits pour une autre race** activé, Proteus plie la coiffure du mod sur la tête de votre race et l'ajoute à la même option du même mod, de sorte qu'elle suit les interrupteurs de ce mod.

- Il vérifie les mods de cheveux activés dans votre collection une fois après chaque mise à jour, quand vous activez le réglage, quand vous appuyez sur **Vérifier maintenant**, et chaque fois qu'un mod de cheveux est installé, activé ou modifié. La coiffure que vous mettez est adaptée elle aussi. Le chat indique les numéros des coiffures adaptées, pour que vous sachiez laquelle choisir ; Glamourer affiche ce numéro.
- La section affiche la vérification pendant qu'elle se déroule, avec **Annuler**, et liste chaque coiffure adaptée. Chacune a son propre bouton **Annuler**, et **Annuler tout ce que cette vérification a adapté** les rétablit toutes (cliquez deux fois). Une coiffure annulée n'est pas adaptée de nouveau d'elle-même.
- Uniquement pour votre propre race, à partir de races du même sexe. Une race que vous ne faites qu'essayer n'est pas adaptée : Proteus attend que vous l'ayez gardée 30 secondes.
- Les cheveux à physique gardent leur physique. Quand le squelette de votre race n'a pas les os avec lesquels une coiffure bouge, le portage apporte le squelette et la physique de la race d'origine pour cette coiffure.
- Une coiffure qui ne tomberait pas correctement sur votre tête est laissée telle quelle, et la liste en indique la raison.
- Pour couvrir toutes les races d'un coup, ouvrez un modèle de cheveux dans le **Studio** et utilisez **Transposer à toutes les races**.

**Chapeaux.** La plupart des cheveux moddés ne gèrent pas les chapeaux, si bien qu'un chapeau porté par-dessus passe au travers. Proteus plaque contre votre tête les cheveux qu'un chapeau recouvrirait.

- **Adapter les coiffures aux chapeaux** est activé par défaut. Proteus attend que vous portiez réellement un chapeau, puis adapte la coiffure en dessous et vous le signale dans le chat. Si vous ne portez jamais de chapeau, vos mods de cheveux ne sont jamais touchés. Décochez pour arrêter, et votre choix est conservé.
- **Masquer les queues de cheval** fait en plus disparaître, pendant qu'un chapeau est porté, les parties qu'aucun chapeau ne pourrait couvrir : queues de cheval, couettes, une longue chute dans le dos. Désactivé par défaut, car le masquage est tout ou rien et une partie mal jugée disparaît purement et simplement.
- Ou adaptez vous-même la coiffure que vous portez. La section indique combien de points seraient plaqués et de combien, et **Adapter** écrit le changement.
- **Annuler** rétablit la coiffure que vous portez. Un mod de cheveux livre généralement un modèle par race, donc **Annuler les coiffures de ce mod** rétablit toutes les coiffures que Proteus a modifiées dans ce mod. Les originaux sont conservés dans `Proteus/hatcompat-backup/` à l'intérieur du mod.
- Les cheveux dont l'auteur a déjà ajouté la gestion des chapeaux sont laissés tels quels, et les cheveux fournis avec le jeu fonctionnent déjà. L'exception, c'est une gestion des chapeaux qui masque bien plus de cheveux qu'un chapeau n'en couvre, généralement héritée de la coiffure à partir de laquelle elle a été construite. Proteus propose de la mesurer à nouveau et de la remplacer.
- **Remplacer la gestion des chapeaux fournie avec une coiffure** fait abandonner à Proteus l'adaptation faite à la main par l'auteur pour utiliser la sienne. Désactivé par défaut, car une adaptation faite à la main est généralement meilleure. La version de l'auteur est conservée et revient quand vous le désactivez.
- Certaines coiffures sont soudées en morceaux trop grands pour que le format de formes du jeu puisse les adresser. Ces points gardent leur forme, donc un chapeau peut encore passer au travers à ces endroits.

##### Ajustement au corps

Ajuste tout seul l'équipement de torse, de jambes, de mains et de pieds à votre corps, dès que vous le portez. Proteus détermine pour quel mod de corps et quelle taille l'équipement a été fait, le réajuste sur votre corps de la même façon que l'outil [Taille de corps](#taille-de-corps) du Studio, enregistre la nouvelle taille dans le mod de l'équipement lui-même et l'active. Le chat indique quand cela commence et quand c'est terminé.

1. Cochez **Ajuster l'équipement à mon corps quand je l'équipe**.
2. Choisissez une **Collection**. Chaque collection Penumbra a son propre choix, et cela démarre sur celle que vous utilisez actuellement.
3. Pour chaque partie — poitrine, jambes, mains et pieds — choisissez un mod de **Corps** et une **Taille**. Le premier corps choisi est mis sur toutes les parties qu'il possède, avec la taille que vous portez dedans (sinon sa première taille). Les mains et les pieds suivent la taille de la poitrine ou des jambes tant que vous n'en choisissez pas. Les corps Rue ne se combinent qu'avec Rue.
4. L'équipement que vous mettez à partir de maintenant est réajusté. Pour ce que vous portez déjà, appuyez sur **Ajuster ce que je porte maintenant**.

Bon à savoir :

- **Ajuster aussi l'équipement d'origine du jeu** traite aussi les pièces vanilla. Elles n'ont pas de mod dans lequel enregistrer, donc chacune reçoit son propre nouveau mod, nommé d'après l'objet et le corps, rangé dans **Agrandissements Proteus**.
- **Ajouter les piercings de mon corps à l'équipement ajusté** désactivé par défaut, ajoute les piercings de ton mod de corps à l'équipement ajusté. Les ongles sont toujours inclus. L'équipement déjà ajusté garde ce avec quoi il a été fait : annule puis ajuste à nouveau ce que tu portes pour le changer.
- **Vos choix dans Penumbra l'emportent.** Si vous choisissez vous-même l'option **Original** d'un mod ou une autre taille, Proteus n'y touche pas. Si un mod fournit déjà votre taille, cette option est activée au lieu d'en créer une nouvelle.
- Si Proteus ne peut pas déterminer pour quel corps une pièce a été faite, ou si la pièce n'a pas de peau de corps à lire, il la laisse telle quelle et le signale dans le chat. Vous pouvez toujours la réajuster à la main dans le Studio.
- **Annuler l'ajustement de ce que je porte** (maintenez Ctrl ou Maj et cliquez) retire les tailles que l'ajustement automatique a créées pour l'équipement que vous portez, supprime les mods qu'il a créés pour l'équipement vanilla, et remet les options que vous aviez avant. Ces pièces ne seront plus réajustées à moins que vous n'appuyiez sur **Ajuster ce que je porte maintenant**. Les tailles que vous avez créées vous-même dans le Studio restent.
- **Oublier le corps de cette collection** le désactive pour cette collection.

### Préréglages

Un **préréglage** est un look nommé pour **un seul mod** : les options cochées, toutes ses couleurs, et ses réglages de lueur et de calque. Les gros paquets — combinaisons, bas — embarquent une douzaine de groupes, et trouver une combinaison qui vaille le coup revient à cocher des cases jusqu'à ce que ça tombe juste. Un préréglage conserve cette combinaison.

Les préréglages occupent une section repliable **Préréglages** en bas de l'éditeur de couleurs d'un mod, sous Avancé. **+ Enregistrer…** conserve l'aspect actuel du mod sous un nom ; la liste déroulante à côté choisit lequel est porté. Repliée, l'en-tête indique toujours celui que tu portes.

- Les préréglages marqués `*` sont venus avec le mod. Ils sont en lecture seule : en modifier un t'en donne ta propre copie, si bien qu'une mise à jour du mod ne peut jamais écraser ce que tu as enregistré.
- Un `●` à côté du préréglage porté signifie que tu as changé quelque chose depuis l'enregistrement. **Mettre à jour** intègre ces changements ; ignore-le et le préréglage reste tel quel.
- **Aucun préréglage** rétablit les couleurs propres du mod. Tes options cochées ne bougent pas — retirer un look n'est pas une demande d'annuler tes propres réglages.
- Essayer des préréglages ne coûte rien. Seules les options cochées sont écrites dans Penumbra ; les couleurs et les réglages de calque ne font que se superposer tant qu'un préréglage est porté, et les fichiers du mod ne sont jamais touchés.

Partage-en un avec **Copier le code** (une chaîne à coller dans le chat) ou **Exporter…** (un fichier `.ptp`). En face, on utilise **Coller le code** ou **Importer…** ; un préréglage fait pour un autre mod le signale avant d'être ajouté.

Appliquer un préréglage pendant qu'une liaison de design est active se prévisualise dans cette liaison, comme toute autre modification — appuie sur **Mettre à jour** dans l'onglet Liaisons pour la conserver. Appliquer un design Glamourer retire les préréglages portés, puisque le design apporte ses propres couleurs ; les préréglages eux-mêmes restent enregistrés.

Si une mise à jour du mod renomme ou supprime une option, appliquer un ancien préréglage règle tout ce qui existe encore et te dit ce qu'il n'a pas pu faire.

### Éditeur de couleurs

Cliquez sur **Couleurs** à côté d'un mod pour ouvrir son éditeur de couleurs dans sa propre fenêtre. Il permet de teinter les calques, de contrôler la lueur et de définir les propriétés de matériau par région, sans éditer aucun fichier.

Chaque option de calque active reçoit son propre onglet en haut, dans l'ordre d'empilement. Glissez un onglet pour réempiler. Si le mod utilise des masques, un onglet **Masques** est épinglé en tête — les masques se rendent toujours par-dessus tout le reste.

#### Mode de rendu

Proteus déduit la façon dont chaque calque doit être rendu à partir des fonctions que vous utilisez réellement, et affiche le résultat sous forme d'un badge **Rendu en** :

- **Skin (peint)** — composé dans votre peau. Le mode par défaut.
- **Cloth** — une seconde peau utilisant des sphere maps, de la métallicité ou du spéculaire.
- **Lueur animée** — une seconde peau avec un effet de lueur défilante.

Vous n'avez pas à choisir : définir une sphere map bascule à lui seul en Cloth. S'il faut le forcer, ouvrez **Avancé** et épinglez un mode. **Rétablir les valeurs par défaut** y restaure les réglages d'origine du mod.

#### Avancé

Sous les lignes, **Avancé** regroupe les réglages qui s'appliquent à tout le mod plutôt qu'à une seule ligne :

| Paramètre | Rôle |
|---------|-------------|
| Forcer le mode de rendu | Épingle Skin / Cloth / Lueur animée au lieu de laisser les fonctions décider. **Revenir à auto** le libère. |
| Calquer sur gen2/vanilla | Peint aussi ce mod sur la peau vanilla (gen2) que vous portez, généralement une peau fournie avec une pièce d'équipement. Activé par défaut, et cela ne coûte rien quand vous n'avez aucune peau vanilla sur vous. bibo et gen3/Eve sont toujours peints. S'applique à tout le mod, et c'est un réglage global : les liaisons de design ne le capturent pas. |
| Superposer sur les hommes | Peint aussi les motifs féminins de ce mod sur les hommes (TBSE ou le corps masculin vanilla), transposés dans sa propre disposition grâce aux cartes de corps masculin, téléchargées la première fois qu'elles servent ; d'ici là, rien n'est peint sur lui. Les motifs faits pour les hommes ne sont jamais modifiés. Activé par défaut ; s'applique à tout le mod et c'est un réglage global : les liaisons de design ne le capturent pas. |
| Rétablir les valeurs par défaut | Rétablit les couleurs, la lueur et le mode de cette option aux réglages que Proteus a enregistrés en premier pour ce mod. Maintenez Ctrl pour l'armer. |

Si un mod n'a aucune option active, il n'y a pas de couleurs à montrer, mais **Avancé** apparaît quand même pour que **Calquer sur gen2/vanilla** reste accessible.

#### Effets et Géométrie

Deux autres sections repliables s'appliquent à tout le mod :

- **Effets** contient le sélecteur d'effet de lueur animée et **Skindent**, l'ombre de contact et le creux de normale sur les bords de sangle. Skindent est désactivé sauf si le pack le demande, car il traite la couverture comme du tissu pressé dans la peau, ce qui est faux pour un tatouage. **Pack** suit ce que le mod a demandé ; **Oui** et **Non** le remplacent.
- **Géométrie** remodèle le vêtement que Proteus construit à partir de votre corps, pour qu'il se lise comme du tissu et non comme de la peinture. Proteus construit une seconde peau en copiant votre corps, elle suit donc par défaut chaque creux :
  - **Tendre sur le décolleté** : le tissu entre les seins passe tout droit, comme le ferait du vrai tissu. Rien n'est tiré vers l'intérieur, il ne peut donc pas entailler le corps.
  - **Tendre sur les fesses** : la même chose en travers de l'assise.
  - **Tendre le tissu sur le nombril** : le tissu se tend à plat sur le nombril au lieu de s'y enfoncer.
  - **Lisser l'entrejambe** : lisse le pli à l'entrejambe. Cela modifie le corps en dessous, donc cela s'applique à tous les vêtements portés à la fois.
  - **Lisser le téton** : lisse le tissu par-dessus. Pas encore terminé : le corps en dessous garde sa forme, donc sur un vêtement près du corps il peut encore transparaître.

  En activer un seul fait rendre le mod en seconde peau.

#### Options de calque

Sous les lignes, l'onglet d'un calque peut afficher :

- **Ce calque est toute la peau** — pour un mod de peau converti, une image qui *est* la peau plutôt que quelque chose posé dessus. Sa carte de normales remplace celle de la peau au lieu de s'y ajouter, et l'atténuation de la teinte de peau tombe à 0 pour que votre propre carnation ressorte.
- **Cette image est asymétrique** — activez-le quand les deux côtés sont censés différer : un tatouage sur un seul bras, un maquillage non miroir. Un corps et un visage vanilla replient l'image en deux, donc sans cela un côté est recopié en miroir sur l'autre. Proteus ne peut pas le détecter pour vous.
- **Pointe renforcée** — sur un bas à pointe renforcée, tricote une pointe plus dense : un panneau plus sombre où les orteils transparaissent encore, s'estompant sur les bords. **Densité** règle son opacité. C'est enregistré dans le mod, donc les préréglages et les designs ne le capturent pas.

#### Lignes

L'éditeur affiche jusqu'à 16 lignes de table de couleurs. Les lignes correspondent aux régions définies par la texture d'index du mod (s'il en a une). La ligne 16 est la couleur de repli utilisée quand il n'y a pas de texture d'index. Les lignes que la texture d'index ne sélectionne jamais sont grisées.

Appuyez sur **Éclairer** sur n'importe quelle sous-ligne pour faire briller cette région sur votre personnage, afin de repérer quelle ligne contrôle quoi.

Chaque ligne a deux sous-lignes :
- **A** — s'applique là où le canal vert de la texture d'index vaut 255.
- **B** — s'applique là où le canal vert vaut 0. Les valeurs intermédiaires se fondent progressivement.

Pour chaque sous-ligne :
- **Diffuse** (nuancier) — teinte multiplicative appliquée au calque. Le blanc (`#FFFFFF`) montre les couleurs naturelles du calque. Toute autre couleur le teinte. Vous pouvez recolorer un bas en niveaux de gris en choisissant une couleur ici.
- **Lueur** (curseur 0–1) — l'intensité de la lueur du calque, avec sa propre couleur. La peau ne peut pas briller, donc régler ce paramètre bascule le calque en couche de tissu, exactement comme une sphere map.
- **Opacité** (curseur −100 à 100) — 0 est la valeur par défaut du mod. −100 est transparent. 100 est totalement opaque.
- **Sphere map / Métallicité / Rugosité / Spéculaire** — disponibles sur Cloth. En régler un seul bascule le calque en seconde peau.

Les lignes et sous-lignes peuvent être copiées et collées entre elles.

Les changements s'appliquent immédiatement à l'écran et sont recomposés environ une seconde après que vous arrêtez d'éditer. Ils sont enregistrés dans le `metadata.json` du mod — sauf si une liaison de design est active, auquel cas ils appartiennent à ce design jusqu'à ce que vous appuyiez sur **Mettre à jour** dans l'onglet Liaisons.

### Remerciements
Un immense merci à Sebby pour m'avoir appris à utiliser la correspondance d'images au pixel plutôt que la cuisson, et pour avoir publié les cartes cuites sous licence MIT via le loose texture compiler.

---
