# Proteus

<!--i18n-->
[English](../README.md) · [日本語](README.ja.md) · [Deutsch](README.de.md) · **Français** · [简体中文](README.zh.md) · [한국어](README.ko.md) · [Español](README.es.md) · [Русский](README.ru.md)
<!--/i18n-->

Proteus est un plugin Dalamud pour FFXIV qui compose des textures de calque sur la peau et l'équipement de votre personnage en temps réel. Les auteurs de mods livrent de petits calques PNG à côté de leurs mods Penumbra ; Proteus les fusionne avec les textures de base chaque fois que vous changez d'option, sans toucher aux fichiers d'origine du mod. Proteus peut importer des fichiers pmp compatibles Proteus, des fichiers de calques omp d'Onion et des tatouages lumineux Atramentum Luminis. Il peut aussi modifier les modèles de n'importe quel mod installé, Proteus ou non : remodeler des vêtements, peindre le balancement au vent, ajouter des interrupteurs de pièces et adapter les cheveux aux chapeaux.

Les calques peuvent être rendus de deux façons : peints dans votre peau, ou en **seconde peau** — une copie du maillage de votre corps dessinée comme un équipement, ce qui permet à un calque d'utiliser des sphere maps, de la métallicité et une lueur animée, choses impossibles avec les matériaux de peau.

- **Portez des mods sans sacrifier un emplacement d'équipement.** Une seconde peau doit forcément être dessinée comme un objet, mais Proteus la dissimule sur quelque chose que vous n'utilisez pas — des lunettes invisibles, une bague que vous ne portez pas, ou en l'ajoutant à vos accessoires équipés — de sorte que votre glamour réel n'est pas touché. Il n'y a rien à configurer : Proteus choisit l'hôte tout seul et ne prend jamais un objet que vous portez.
- **Remodelez les vêtements de n'importe quel mod, directement sur votre personnage.** Dans l'onglet **Studio**, peignez sur un vêtement que vous portez pour l'écarter de votre corps là où la peau passe au travers, le rentrer, le lisser, le tendre par-dessus un pli, ou peindre là où le vent le fait balancer. Chaque modification est enregistrée dans le mod et peut être annulée.
- **Ajoutez des interrupteurs à n'importe quelle pièce de n'importe quel mod, pas seulement ceux de Proteus.** Quand un mod soude un nœud, un collier ou une sangle dans une géométrie que son auteur n'a jamais rendue optionnelle, l'onglet **Studio** peut détacher cette pièce et lui donner un vrai interrupteur.
- **Adaptez les cheveux moddés aux chapeaux.** Proteus plaque contre votre tête les cheveux qu'un chapeau recouvrirait, pour que le chapeau cesse de passer au travers.


Si vous avez besoin d'aide, consultez d'abord ce [guide de dépannage](../TROUBLESHOOTING.md).
Rejoignez ensuite https://discord.gg/solona et posez votre question dans le salon #help. C'est encore tout neuf, mais je corrigerai les bugs dès que possible !

Si vous êtes créateur et souhaitez faire des mods pour Proteus, lisez le [guide du créateur](../For%20Creators.md).

---

## Pour les utilisateurs

### Installation

Ajoutez ce dépôt https://dl.solona.info/repo.json dans l'onglet expérimental de /xlplugins.
Enregistrez, puis cherchez Proteus dans la fenêtre principale de /xlplugins.

> Déjà installé depuis `raw.githubusercontent.com/solona-m/plugins/main/repo.json` ? Cela continue
> de fonctionner et fonctionnera toujours, mais la nouvelle URL est plus fiable et n'est pas soumise
> aux limitations de GitHub.

Installez quelques mods de calques faits pour Proteus, choisissez vos options, et votre personnage se met à jour.

### Fenêtre d'état

Ouvrez la fenêtre d'état avec `/proteus`. Elle comporte sept onglets, et le résultat de la dernière composition (textures modifiées, mods utilisés, il y a combien de temps) s'affiche toujours en bas.

#### Mods

Liste tous les mods Penumbra qui contiennent un fichier Proteus. Cliquez sur un en-tête de colonne pour trier selon celle-ci.

| Colonne | Rôle |
|--------|-------------|
| Act. | Active ou désactive la composition Proteus pour ce mod. |
| Mod | Le nom affiché du mod. Cliquez dessus pour ouvrir le mod dans Penumbra. |
| Prio | Priorité dans la pile de composition de Proteus. Les nombres les plus bas passent en premier (couche du bas). Glissez pour changer ; Ctrl-clic pour saisir une valeur. |
| Préréglage | Le look enregistré que porte ce mod. Choisis-en un autre pour changer sans ouvrir l'éditeur de couleurs. Affiche — pour un mod sans préréglages. |
| Couleurs | Ouvre l'éditeur de couleurs de ce mod. |
| Skindent | Ombre d'occlusion ambiante et creux de normale sur les bords de sangle de ce mod. « Pack » suit ce que le mod a demandé ; Oui/Non le remplace. |

Cliquez sur **Recomposer maintenant** pour forcer une recomposition manuelle. Proteus recompose aussi automatiquement dès que vous changez une option ou un réglage Penumbra, changez d'équipement, ou changez de race ou de corps.

#### Studio

Modifie les modèles de **n'importe quel** mod installé, pas seulement ceux de Proteus. Vous pouvez remodeler un vêtement pour que votre corps cesse de passer au travers, peindre là où le vent le fait balancer, ou détacher une pièce derrière son propre interrupteur. Chaque changement est écrit dans les fichiers du mod lui-même, il **continue donc de fonctionner avec Proteus désactivé** et suit le mod si vous l'exportez.

Choisissez un mod, puis l'un de ses modèles. L'onglet s'ouvre sur la pièce de torse que vous portez (ou sur vos jambes s'il n'y en a pas), et les mods et modèles que vous portez sont listés en premier, en vert. Cliquer sur un vêtement de votre personnage ouvre son mod et son modèle.

Les outils se trouvent dans un panneau à gauche :

| Outil | Rôle |
|------|-------------|
| Basculer des pièces | Sélectionne des pièces du modèle et leur donne un interrupteur. Voir [Interrupteurs de pièces](#interrupteurs-de-pièces) plus bas. |
| Tirer dehors | Pousse la surface vers l'extérieur, pour qu'un corps qui passe au travers d'un vêtement soit de nouveau couvert. Le tissu s'éloigne en ligne droite de la peau qui se trouve dessous. |
| Pousser dedans | Le même pinceau à l'envers, pour un vêtement qui se tient trop loin du corps. |
| Lisser | Lisse la surface : les bosses d'origine du vêtement, ou une traction qui a donné un résultat irrégulier. Comme le relax de 3ds Max, il rétrécit : les courbes s'aplatissent et le tissu peut s'enfoncer vers le corps. |
| Pont | Peignez par-dessus un creux, comme le sillon entre les fesses ou un pli, pour tendre le tissu tout droit par-dessus au lieu de suivre le corps jusqu'au fond. Il ne fait jamais que soulever. |
| Vent | Peint à quel point le vent du jeu fait balancer le vêtement, affiché en rouge. Maintenez Ctrl, ou réglez **Quantité** sur 0 %, pour effacer. |

##### Pinceau

- **Vous peignez directement sur votre personnage**, et un vêtement montre chaque trait pendant que vous le peignez. Maintenez **Alt** pour déplacer la caméra. Cochez **Afficher le modèle** pour peindre plutôt sur le modèle dans la fenêtre : glissez depuis le fond pour le tourner, Maj+glisser pour le déplacer, molette pour zoomer.
- **Taille du pinceau** descend jusqu'à 1 mm. `[` et `]` la changent, et Maj donne des pas plus fins. L'effet est le plus fort au centre et s'estompe jusqu'à rien sur le bord. **Force** est la distance dont la surface bouge par instant de peinture. Une petite valeur est généralement la bonne : un vêtement n'a besoin de dégager le corps que d'une fraction de millimètre, et vous pouvez toujours repeindre au même endroit.
- **Miroir gauche/droite** peint les deux côtés à la fois.
- **La peau ne bouge jamais.** Pour tenir un pinceau à l'écart de quoi que ce soit d'autre, verrouillez la pièce : Maj+clic dessus sur votre personnage ou sur le modèle, ou décochez-la dans la liste des pièces. Les coutures soudées à une pièce verrouillée tiennent aussi.
- **Les cheveux fonctionnent aussi.** Les cheveux, le visage, les oreilles et la queue se redessinent quand vous relâchez, au lieu de montrer le trait pendant que vous peignez.
- **Appliquer aux autres tailles** copie la modification sur les autres fichiers du mod pour le même vêtement, en les faisant correspondre selon leur position sur celui-ci.

##### Enregistrement et annulation

- Chaque trait est enregistré dans le mod un instant après que vous relâchez. Le premier enregistrement copie le modèle d'origine dans `Proteus/meshvolume-backup/` à l'intérieur du mod.
- **Annuler le trait** (ou Ctrl+Z) retire le dernier trait. **Tout reprendre** abandonne tous les traits sur ce modèle.
- **Annuler les modifications** (maintenez Ctrl ou Maj et cliquez) remet chaque modèle que le pinceau a changé dans ce mod exactement tel que son auteur l'a fait.
- Les interrupteurs de pièces, l'adaptation aux chapeaux et le pinceau gardent chacun leur propre sauvegarde. Si plusieurs d'entre eux ont modifié le même modèle, annulez d'abord le plus récent. Proteus vous prévient si vous essayez un autre ordre.
- Les curseurs de corps d'un modèle ne peuvent pas toujours suivre une modification. Quand certains points n'ont pas pu bouger avec elle, Proteus indique combien, et activer ce curseur peut faire revenir le passage au travers par endroits.
- La plupart des maillages moddés n'ont pas de canal de vent. Le premier trait de vent enregistré en ajoute un, et règle les matériaux du vêtement pour que le vent puisse le faire bouger. Les matériaux qui viennent du jeu plutôt que du mod ne peuvent pas être réglés.

##### Interrupteurs de pièces

**Basculer des pièces** sort une pièce de géométrie du modèle d'un mod et la place derrière un interrupteur : un nœud, un collier, une sangle que l'auteur a soudés dans un maillage toujours visible.

L'interrupteur est écrit dans le mod lui-même sous forme d'option Penumbra ordinaire, il apparaît donc dans les réglages de ce mod.

Les pièces du modèle sont listées avec leur nombre de triangles. Cliquez une pièce sur votre personnage ou dans la vue du modèle pour la cocher. Cochez les pièces qu'un interrupteur doit masquer, donnez-lui un nom, et appuyez sur **Créer un interrupteur à partir des pièces cochées**. Empilez-en autant que vous voulez, puis appuyez sur **Écrire les interrupteurs dans le mod**.

Bon à savoir :

- **Dix interrupteurs par objet.** C'est la limite du jeu, pas celle de Proteus. Si un auteur les a déjà tous utilisés, l'onglet le signale et refuse d'en ajouter.
- **Équipement et accessoires uniquement.** Il n'y a rien à quoi rattacher un interrupteur sur les autres types de modèles.
- **Une pièce que l'auteur fait déjà basculer peut aussi recevoir le vôtre.** Les deux se cumulent : la pièce ne s'affiche que quand les deux sont activés.
- **C'est réversible.** Les modèles d'origine sont conservés, donc **Annuler – restaurer les modèles d'origine** remet le mod exactement dans son état initial et supprime le groupe d'options.
- Si un objet possède plusieurs fichiers de modèle dont les pièces sont agencées différemment, Proteus ne modifie que ceux où l'interrupteur tombe juste et vous dit lesquels il a laissés de côté, plutôt que de deviner et de toucher la mauvaise géométrie.

#### Liaisons

Lie toute votre configuration Proteus — quels mods sont actifs, leurs priorités et options, et toutes leurs couleurs — à un design Glamourer. Cochez **Lier l'état de Proteus aux designs Glamourer** pour l'activer.

Enregistrer un design capture l'état actuel de Proteus avec lui. Appliquer ce design plus tard le restaure. Les couleurs et les réglages de couche sont restaurés sous forme de calque en direct, donc les fichiers du mod ne sont jamais réécrits.

Tant qu'une liaison est active, les modifications faites dans l'éditeur de couleurs s'affichent immédiatement en aperçu mais ne sont **pas** enregistrées avant d'appuyer sur **Mettre à jour** — ce qui replie tout ce qui est à l'écran dans ce design.

#### Créer

Crée un mod de calque simple sans quitter le jeu. Donnez-lui un nom, un auteur, et choisissez au moins une texture (diffuse, masque, normale ou index). Le matériau cible se remplit automatiquement à partir du corps que vous portez ; vous pouvez choisir un autre matériau équipé dans la liste déroulante ou saisir un chemin à la main. Proteus écrit un nouveau mod Penumbra et l'ouvre.

Les emplacements de texture que le matériau choisi ne peut pas utiliser sont grisés.

#### Importer

Prend un pack de mod et le convertit en mod Proteus. Trois types sont pris en charge :

**Mods Penumbra classiques (`.pmp`)** — portez des morceaux d'un mod d'équipement normal sans utiliser d'emplacement, et profitez en plus des fonctions avancées de table de couleurs.

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
| Activé | Interrupteur principal. Désactivé, Proteus efface sa sortie et vous redessine sans elle. |
| Redessin automatique | Laisse Proteus suivre tout seul : il recompose après un changement de zone, un changement d'équipement ou un redessin, puis recharge votre personnage pour que vous voyiez le résultat. Désactivé, Proteus devient essentiellement manuel. Votre look reste en place, mais une modification n'apparaît pas tant que rien ne vous redessine. |
| Élever automatiquement la priorité du mod | Quand un autre mod est confirmé comme remplaçant une texture de peau dans laquelle Proteus compose, élève la priorité Penumbra de Proteus au-dessus de lui et le signale dans le chat. |
| Rechargement sur place | Rafraîchit les textures via Glamourer au lieu d'un redessin complet, ce qui évite le clignotement de disparition/réapparition. Activé par défaut. |
| Activer la compression | Compresse par blocs les textures cuites, réduisant leur taille sur le disque et en VRAM à environ un quart. Activé par défaut. |
| Alpha net | Expérimental. Garde les sphere maps et la métallicité fonctionnelles en pose de groupe, au prix de bords plus durs sur les tissus transparents. |
| Cache de textures (Mo) | Quantité de données de texture décodées gardées en mémoire entre deux compositions. |
| Masquer les maillages de corps redondants | Ignore la peau que la seconde peau dessinerait deux fois : anneaux de renfort d'articulation qu'une partie voisine couvre déjà, et copies superflues d'une région. Activé par défaut, sûr sur n'importe quel corps. |
| Héberger sur des lunettes invisibles | Laisse la seconde peau occuper l'emplacement d'accessoire de visage pour garder vos bagues libres. |
| Atténuation de la teinte de peau | À quel point les calques résistent à être teintés par votre carnation. |
| Occlusion ambiante / Douceur de l'ombre / Skindenting | Force globale de l'ombre de contact et du creux de normale autour des bords de sangle. |
| Réagir à la lumière de la scène | Permet aux lignes de couleur marquées **S'estompe à la lumière** de s'atténuer à mesure que la lumière sur vous augmente. Désactivé, chaque lueur brille à pleine intensité partout. |
| Définir le niveau de lumière à la main / Niveau de lumière | Ignore la scène et utilise le curseur à la place. C'est le moyen le plus rapide de voir une lueur réservée à l'obscurité sans attendre le crépuscule, et le bon réglage pour la pose de groupe. |

Trois boutons ici méritent d'être connus :

- **Restaurer l'accessoire modifié** — force un redessin complet si une seconde peau reste coincée sur une bague ou un bracelet après une désactivation ou un échange.
- **Vider le cache de textures** — à utiliser quand une modification de texture n'apparaît pas, par exemple après avoir réexporté un calque à la même taille.
- **Textures d'effet lumineux** — ouvre le dossier dans lequel Proteus lit les cartes de défilement de lueur animée. Déposez-y des images et elles apparaîtront dans la liste Effet de chaque calque d'équipement. Survolez le bouton pour voir le chemin complet.

##### Chapeaux

La plupart des cheveux moddés ne gèrent pas les chapeaux, si bien qu'un chapeau porté par-dessus passe au travers. La section **Chapeaux** plaque contre votre tête les cheveux qu'un chapeau recouvrirait. Comme le Studio, elle modifie les fichiers du mod de cheveux lui-même, donc l'adaptation continue de fonctionner avec Proteus désactivé et suit le mod si vous l'exportez.

- **Adapter les coiffures aux chapeaux** est désactivé par défaut. Activez-le et Proteus vérifie chaque coiffure quand vous la mettez et l'adapte, en vous le signalant dans le chat la première fois.
- Ou adaptez vous-même la coiffure que vous portez. La section indique combien de points seraient plaqués et de combien, et **Adapter** écrit le changement.
- **Annuler** rétablit la coiffure que vous portez. Un mod de cheveux livre généralement un modèle par race, donc **Annuler les coiffures de ce mod** rétablit toutes les coiffures que Proteus a modifiées dans ce mod. Les originaux sont conservés dans `Proteus/hatcompat-backup/` à l'intérieur du mod.
- Les cheveux dont l'auteur a déjà ajouté la gestion des chapeaux sont laissés tels quels, et les cheveux fournis avec le jeu fonctionnent déjà. L'exception, c'est une gestion des chapeaux qui masque bien plus de cheveux qu'un chapeau n'en couvre, généralement héritée de la coiffure à partir de laquelle elle a été construite. Proteus propose de la mesurer à nouveau et de la remplacer.
- Certaines coiffures sont soudées en morceaux trop grands pour que le format de formes du jeu puisse les adresser. Ces points gardent leur forme, donc un chapeau peut encore passer au travers à ces endroits.

### Préréglages

Un **préréglage** est un look nommé pour **un seul mod** : les options cochées, toutes ses couleurs, et ses réglages de lueur et de calque. Les gros paquets — combinaisons, bas — embarquent une douzaine de groupes, et trouver une combinaison qui vaille le coup revient à cocher des cases jusqu'à ce que ça tombe juste. Un préréglage conserve cette combinaison.

Les préréglages occupent une section repliable **Préréglages** en bas de l'éditeur de couleurs d'un mod, sous Avancé. **+ Enregistrer…** conserve l'aspect actuel du mod sous un nom ; la liste déroulante à côté choisit lequel est porté. Repliée, l'en-tête indique toujours celui que tu portes.

- Les préréglages marqués `*` sont venus avec le mod. Ils sont en lecture seule : en modifier un t'en donne ta propre copie, si bien qu'une mise à jour du mod ne peut jamais écraser ce que tu as enregistré.
- Un `●` à côté du préréglage porté signifie que tu as changé quelque chose depuis l'enregistrement. **Mettre à jour** intègre ces changements ; ignore-le et le préréglage reste tel quel.
- **Aucun préréglage** rétablit les couleurs propres du mod. Tes options cochées ne bougent pas — retirer un look n'est pas une demande d'annuler tes propres réglages.
- Essayer des préréglages ne coûte rien. Seules les options cochées sont écrites dans Penumbra ; les couleurs et les réglages de calque ne font que se superposer tant qu'un préréglage est porté, et les fichiers du mod ne sont jamais touchés.

Partage-en un avec **Copier le code** (une chaîne à coller dans le chat) ou **Exporter…** (un fichier `.ptp`). En face, on utilise **Coller le code** ou **Importer…** ; un préréglage fait pour un autre mod le signale avant d'être ajouté.

Appliquer un préréglage pendant qu'une liaison de design est active se prévisualise dans cette liaison, comme toute autre modification — appuie sur **Mettre à jour la liaison** pour la conserver. Appliquer un design Glamourer retire les préréglages portés, puisque le design apporte ses propres couleurs ; les préréglages eux-mêmes restent enregistrés.

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
| Corps | Sur quels types de corps ce mod est cuit — **Tous les corps** (corps frère bibo↔gen3/Eve, plus vanilla gen2), **bibo+gen3** (le corps frère uniquement — par défaut), ou **Aucun**. S'applique à tout le mod, et c'est un réglage global : les liaisons de design ne le capturent pas. |
| Rétablir les valeurs par défaut | Rétablit les couleurs, la lueur et le mode de cette option aux réglages que Proteus a enregistrés en premier pour ce mod. Maintenez Ctrl pour l'armer. |

Si un mod n'a aucune option active, il n'y a pas de couleurs à montrer, mais **Avancé** apparaît quand même pour que **Corps** reste accessible.

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

Les changements s'appliquent immédiatement à l'écran et sont recomposés environ une seconde après que vous arrêtez d'éditer. Ils sont enregistrés dans le `metadata.json` du mod — sauf si une liaison de design est active, auquel cas ils appartiennent à ce design jusqu'à ce que vous appuyiez sur **Mettre à jour**.

### Remerciements
Un immense merci à Sebby pour m'avoir appris à utiliser la correspondance d'images au pixel plutôt que la cuisson, et pour avoir publié les cartes cuites sous licence MIT via le loose texture compiler.

---
