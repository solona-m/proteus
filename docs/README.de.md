# Proteus

<!--i18n-->
[English](../README.md) · [日本語](README.ja.md) · **Deutsch** · [Français](README.fr.md) · [简体中文](README.zh.md) · [한국어](README.ko.md) · [Español](README.es.md) · [Русский](README.ru.md)
<!--/i18n-->

Proteus ist ein Dalamud-Plugin für FFXIV, das Overlay-Texturen zur Laufzeit auf die Haut und die Ausrüstung deines Charakters komponiert. Mod-Autoren liefern kleine PNG-Overlays zusammen mit ihren Penumbra-Mods aus; Proteus blendet sie bei jeder Optionsänderung in die Basistexturen ein, ohne die Originaldateien des Mods anzurühren. Proteus kann Proteus-fähige pmp-Dateien, Onion-Overlay-omp-Dateien und Atramentum-Luminis-Leuchttattoos importieren. Außerdem kann es die Modelle jedes installierten Mods bearbeiten, ob Proteus-Mod oder nicht: Kleidung umformen, Windbewegung malen, Teil-Schalter hinzufügen und Haar unter Hüte passen lassen.

Overlays können auf zwei Arten dargestellt werden: in die Haut gemalt, oder als **zweite Haut** — eine Kopie deines Körper-Meshs, die als Ausrüstung gezeichnet wird, sodass ein Overlay Sphere-Maps, Metallanteil und animiertes Leuchten nutzen kann, was Hautmaterialien nicht können.

- **Trage Mods, ohne einen Ausrüstungsplatz aufzugeben.** Eine zweite Haut muss als Gegenstand gezeichnet werden, aber Proteus versteckt sie auf etwas, das du gerade nicht nutzt — einer unsichtbaren Brille, einem Ring, den du nicht trägst, oder angehängt an deine getragenen Accessoires — sodass dein eigentliches Glamour unangetastet bleibt. Es gibt nichts einzurichten: Proteus sucht sich den Träger selbst und nimmt dir nie einen Gegenstand weg, den du trägst.
- **Forme die Kleidung jedes Mods um, direkt an deinem Charakter.** Male im Tab **Studio** auf ein getragenes Kleidungsstück, um es dort vom Körper wegzuziehen, wo Haut hindurchragt, es hineinzudrücken, es zu glätten, es über eine Falte zu spannen oder zu malen, wo der Wind es bewegt. Jede Änderung wird im Mod gespeichert und lässt sich rückgängig machen.
- **Füge jedem Teil jedes Mods Schalter hinzu, nicht nur Proteus-Mods.** Wenn ein Mod eine Schleife, ein Halsband oder einen Riemen in Geometrie einschweißt, die sein Autor nie optional gemacht hat, kann der Tab **Studio** dieses Stück herauslösen und ihm einen echten Schalter geben.
- **Lass gemoddetes Haar unter Hüte passen.** Proteus drückt das Haar, das ein Hut bedecken würde, flach an deinen Kopf, damit der Hut nicht mehr einfach hindurchgeht.


Wenn du Hilfe brauchst, sieh bitte zuerst in den [Leitfaden zur Fehlerbehebung](../TROUBLESHOOTING.md).
Komm danach auf https://discord.gg/solona und frag im Kanal #help. Das Plugin ist noch neu, aber ich behebe Fehler so schnell ich kann!

Wenn du selbst Mods für Proteus bauen willst, lies den [Leitfaden für Ersteller](../For%20Creators.md).

---

## Für Nutzer

### Installation

Füge unter /xlplugins im Reiter für experimentelle Repositories https://dl.solona.info/repo.json hinzu.
Speichern, dann findest du Proteus im Hauptfenster von /xlplugins.

> Bereits über `raw.githubusercontent.com/solona-m/plugins/main/repo.json` installiert? Das
> funktioniert weiterhin und wird es immer, aber die neue URL ist zuverlässiger und unterliegt
> nicht GitHubs Drosselung.

Installiere ein paar Overlay-Mods, die für Proteus gemacht sind, wähle deine Optionen, und dein Charakter aktualisiert sich.

### Statusfenster

Öffne das Statusfenster mit `/proteus`. Es hat sieben Tabs, und das Ergebnis der letzten Komposition (gepatchte Texturen, genutzte Mods, wie lange es her ist) steht immer am unteren Rand.

#### Mods

Listet jeden Penumbra-Mod auf, der eine Proteus-Beidatei enthält. Klicke auf eine Spaltenüberschrift, um danach zu sortieren.

| Spalte | Was sie tut |
|--------|-------------|
| An | Aktiviert oder deaktiviert die Proteus-Komposition für diesen Mod. |
| Mod | Der Anzeigename des Mods. Klicke ihn an, um in Penumbra zu diesem Mod zu springen. |
| Prio | Priorität innerhalb des Kompositionsstapels von Proteus. Niedrigere Zahlen kommen zuerst (untere Ebene). Ziehen zum Ändern, Strg-Klick zum Eintippen. |
| Preset | Der gespeicherte Look, den dieser Mod gerade trägt. Wähle einen anderen, um ohne den Farbeditor umzuschalten. Zeigt — bei einem Mod ohne Presets. |
| Farben | Öffnet den Farbeditor für diesen Mod. |
| Skindent | Ambient-Occlusion-Schatten und Normal-Vertiefung an den Riemenkanten dieses Mods. „Paket“ folgt dem, was der Mod verlangt hat; An/Aus überschreibt es. |

Klicke **Jetzt neu komponieren**, um eine Neukomposition von Hand zu erzwingen. Proteus komponiert außerdem automatisch neu, sobald du eine Penumbra-Option oder Mod-Einstellung änderst, die Ausrüstung wechselst oder Volk bzw. Körper änderst.

#### Studio

Bearbeitet die Modelle **jedes** installierten Mods, nicht nur von Proteus-Mods. Du kannst Kleidung umformen, damit dein Körper nicht mehr hindurchragt, malen, wo der Wind sie bewegt, oder ein Stück herauslösen und hinter einen eigenen An/Aus-Schalter stellen. Jede Änderung wird in die eigenen Dateien des Mods geschrieben, sie **funktioniert also weiter, wenn Proteus aus ist**, und reist mit dem Mod mit, wenn du ihn exportierst.

Wähle einen Mod, dann eines seiner Modelle. Der Tab öffnet sich auf dem Brustteil, das du trägst (oder auf deinen Beinen, wenn es keines gibt), und Mods und Modelle, die du trägst, stehen grün ganz oben in der Liste. Ein Klick auf ein Kleidungsstück an deinem Charakter öffnet dessen Mod und Modell.

Die Werkzeuge liegen in einer Leiste links:

| Werkzeug | Was es tut |
|------|-------------|
| Teile umschalten | Wählt Stücke des Modells aus und gibt ihnen einen An/Aus-Schalter. Siehe [Teil-Schalter](#teil-schalter) weiter unten. |
| Herausziehen | Drückt die Oberfläche nach außen, sodass ein Körper, der durch ein Kleidungsstück ragt, wieder bedeckt ist. Stoff bewegt sich dabei gerade von der Haut darunter weg. |
| Hineindrücken | Derselbe Pinsel umgekehrt, für Kleidung, die zu weit vom Körper absteht. |
| Glätten | Glättet die Oberfläche: Beulen, die die Kleidung mitbringt, oder einen Zug, der unsauber geworden ist. Wie Relax in 3ds Max schrumpft es, Kurven flachen also ab, und der Stoff kann zum Körper hin einsinken. |
| Überbrücken | Male quer über eine Vertiefung, etwa die Falte zwischen den Backen oder eine Kerbe, um den Stoff gerade darüber zu spannen, statt ihn dem Körper hinein folgen zu lassen. Es hebt nur an. |
| Wind | Malt, wie stark der Wind des Spiels das Kleidungsstück bewegt, rot dargestellt. Halte Strg gedrückt oder setze **Menge** auf 0 %, um zu radieren. |

##### Malen

- **Du malst direkt auf deinen Charakter**, und ein Kleidungsstück zeigt jeden Strich schon beim Malen. Halte **Alt**, um die Kamera zu bewegen. Setze den Haken bei **Modellansicht zeigen**, um stattdessen auf dem Modell im Fenster zu malen: Zieh vom Hintergrund aus, um es zu drehen, Shift-Ziehen verschiebt es, Scrollen zoomt.
- **Pinselgröße** geht bis hinunter auf 1 mm. `[` und `]` ändern sie, und Shift gibt feinere Schritte. Die Wirkung ist in der Mitte am stärksten und läuft zum Rand auf null aus. **Stärke** ist, wie weit sich die Oberfläche pro Malmoment bewegt. Klein ist meist richtig: Kleidung muss den Körper nur um Bruchteile eines Millimeters freihalten, und du kannst dieselbe Stelle jederzeit erneut übermalen.
- **Links/rechts spiegeln** malt beide Seiten zugleich.
- **Haut bewegt sich nie.** Um einen Pinsel von etwas anderem fernzuhalten, sperre es: Shift-Klick auf das Teil an deinem Charakter oder am Modell, oder entferne den Haken in der Teileliste. Nähte, die mit einem gesperrten Teil verschweißt sind, halten ebenfalls.
- **Haar funktioniert auch.** Haar, Gesicht, Ohren und Schweif werden beim Loslassen neu gezeichnet, statt den Strich schon beim Malen zu zeigen.
- **Auf andere Größen anwenden** überträgt die Änderung auf die anderen Dateien des Mods für dasselbe Kleidungsstück, zugeordnet nach ihrer Lage daran.

##### Speichern und Rückgängig

- Jeder Strich wird kurz nach dem Loslassen in den Mod gespeichert. Beim ersten Speichern wird das Originalmodell nach `Proteus/meshvolume-backup/` im Mod kopiert.
- **Strich zurücknehmen** (oder Strg+Z) nimmt den letzten Strich zurück. **Neu anfangen** verwirft jeden Strich auf diesem Modell.
- **Gespeichertes zurücknehmen** (Strg oder Shift gedrückt halten und klicken) stellt jedes Modell, das der Pinsel in diesem Mod verändert hat, genau so wieder her, wie sein Autor es gemacht hat.
- Teil-Schalter, Hut-Anpassung und Pinsel führen jeweils ihre eigene Sicherung. Haben mehrere davon dasselbe Modell verändert, nimm die jüngste Änderung zuerst zurück. Proteus sagt dir Bescheid, wenn du es in anderer Reihenfolge versuchst.
- Die Körper-Regler eines Modells können einer Änderung nicht immer folgen. Ließen sich manche Punkte nicht mitbewegen, sagt Proteus, wie viele, und das Einschalten dieses Reglers kann das Durchdringen stellenweise zurückbringen.
- Die meisten gemoddeten Meshes haben keinen Windkanal. Der erste gespeicherte Windstrich fügt einen hinzu und stellt die Materialien des Kleidungsstücks so ein, dass der Wind es bewegen kann. Materialien, die aus dem Spiel statt aus dem Mod stammen, lassen sich nicht einstellen.

##### Teil-Schalter

**Teile umschalten** nimmt ein Stück Geometrie aus dem Modell eines Mods und stellt es hinter einen An/Aus-Schalter: eine Schleife, ein Halsband, einen Riemen, den der Autor in ein immer sichtbares Mesh geschweißt hat.

Der Schalter wird als gewöhnliche Penumbra-Option in den Mod selbst geschrieben, taucht also in dessen eigenen Einstellungen auf.

Die Teile des Modells werden mit ihren Dreieckszahlen aufgelistet. Klicke ein Stück an deinem Charakter oder in der Modellansicht an, um es zu markieren. Markiere die Teile, die ein Schalter ausblenden soll, gib ihm einen Namen und drücke **Schalter aus den markierten Teilen erstellen**. Reihe so viele auf, wie du willst, und drücke dann **Schalter in den Mod schreiben**.

Wissenswertes:

- **Zehn Schalter pro Gegenstand.** Das ist das Limit des Spiels, nicht das von Proteus. Hat ein Autor sie schon alle verbraucht, sagt der Tab das und lässt dich keine weiteren anlegen.
- **Nur Ausrüstung und Accessoires.** Bei anderen Modelltypen gibt es nichts, woran ein Schalter hängen könnte.
- **Ein Teil, das der Autor bereits schaltet, kann auch deinen Schalter bekommen.** Die beiden stapeln sich: Das Teil erscheint nur, wenn beide an sind.
- **Es ist umkehrbar.** Die Originalmodelle werden aufbewahrt, also setzt **Rückgängig – Originalmodelle wiederherstellen** den Mod exakt in seinen alten Zustand zurück und entfernt die Optionsgruppe.
- Hat ein Gegenstand mehrere Modelldateien, deren Teile unterschiedlich angeordnet sind, bearbeitet Proteus nur die, bei denen der Schalter korrekt greift, und sagt dir, welche es in Ruhe gelassen hat, statt zu raten und die falsche Geometrie zu treffen.

#### Bindungen

Bindet deine gesamte Proteus-Einrichtung — welche Mods an sind, ihre Prioritäten und Optionen sowie alle ihre Farben — an ein Glamourer-Design. Setze den Haken bei **Proteus-Zustand an Glamourer-Designs binden**, um es einzuschalten.

Beim Speichern eines Designs wird der aktuelle Proteus-Zustand dazu erfasst. Wendest du das Design später an, wird er wiederhergestellt. Farben und Ebeneneinstellungen werden als Live-Overlay wiederhergestellt, sodass die Dateien des Mods selbst nie überschrieben werden.

Solange eine Bindung aktiv ist, werden Änderungen im Farbeditor sofort in der Vorschau sichtbar, aber **nicht** gespeichert, bis du **Aktualisieren** drückst — das faltet alles, was gerade auf dem Bildschirm steht, zurück in dieses Design.

#### Erstellen

Erstellt einen einfachen Overlay-Mod, ohne das Spiel zu verlassen. Gib ihm einen Namen und einen Autor und wähle mindestens eine Textur (Diffuse, Maske, Normal oder Index). Das Materialziel wird automatisch aus dem Körper gefüllt, den du gerade trägst; du kannst aus der Auswahlliste ein anderes getragenes Material wählen oder einen Pfad von Hand eintippen. Proteus schreibt einen neuen Penumbra-Mod und öffnet ihn.

Texturplätze, die das gewählte Material gar nicht nutzen kann, sind ausgegraut.

#### Import

Nimmt ein Mod-Paket und wandelt es in einen Proteus-Mod um. Drei Typen werden unterstützt:

**Normale Penumbra-Mods (`.pmp`)** — trage Teile eines gewöhnlichen Ausrüstungs-Mods, ohne einen Ausrüstungsplatz zu belegen, und bekomme obendrein die erweiterten Farbtabellen-Funktionen.

Es bleibt ein gewöhnlicher Penumbra-Mod: Penumbra bestimmt weiterhin, ob er an ist und welche seiner Optionen gewählt sind. Was sich ändert, ist, dass seine Teile auf dem Trägergegenstand von Proteus statt auf einem echten Ausrüstungsplatz gezeichnet werden, sodass dein Glamour unangetastet bleibt.

Der nützliche Nebeneffekt ist, dass du **mehrere seiner Optionen gleichzeitig tragen kannst**. Normalerweise beanspruchen zwei Optionen derselben Gruppe denselben Modellpfad, und das Spiel kann nur eine anzeigen — ein Paket kann also gar nicht „dieses Teil *und* jenes Teil“ anbieten. Nach dem Import wird jedes gewählte Teil einzeln hinzugefügt.

- Teile kommen **ausgeschaltet** an. Setze danach in Penumbra den Haken bei denen, die du willst; bis dahin wird nichts getragen.
- Ein Paket, das *bereits* ein Proteus-Mod ist, wird genau so installiert, wie sein Autor es gebaut hat. Nichts wird umgewandelt.
- Haut wird beim Import entfernt. Das ist ideal für Accessoires wie Schmuck, Piercings und Jacken. Importierst du ein Hemd, passt es nur, wenn dein getragener Brustplatz dieselbe Größe hat.

**Onion-Overlay-Pakete (`.omp`)** — trage seine Ebenen als Proteus-Overlays, die du umfärben, neu stapeln, zum Leuchten bringen kannst und so weiter.

Ein Paket, das dieselbe Grafik in mehreren UV-Layouts (bibo, gen3, Vanilla) mitbringt, wird in Penumbra zu einer Einfachauswahl-Gruppe **Body UV**, voreingestellt auf das Layout des Körpers, den du trägst, sodass immer nur eines komponiert wird. Die Deckkraft einer Ebene ist ins Bild eingebrannt; eine Ebene mit einem anderen Mischmodus als Normal wird übersprungen, mit entsprechendem Hinweis, denn Proteus komponiert ausschließlich Alpha-over. Onions eigene Optionsgruppen und Volksfilter werden nicht importiert.

**Atramentum-Luminis-Leuchttattoos (`.ttmp2`)** — trage das Leuchten als Proteus-Overlay, das du umfärben und dimmen kannst, ganz ohne Shader-Mod.

Atramentum-Luminis-Pakete verstecken ihr Leuchten im Alphakanal einer Textur, und ohne diesen Shader-Mod stellen sie überhaupt nichts dar. Proteus liest das Leuchten heraus und baut es als gewöhnliches Overlay neu auf: Die vom Künstler markierten Flächen werden zur zweiten Haut, und die Grafik selbst treibt ein Material mit animiertem Leuchten an, sodass das Neon seine Farben pixelgenau behält. Der Regler **Leuchten** in den Farben tut dann genau das, was du erwartest, und du kannst das Ganze wie jedes andere Overlay an ein Design binden.

- Die Körpertextur des Pakets kommt ebenfalls mit, als eigene Option **Haut des Autors**, und ist standardmäßig an — sie trägt die Teile eines Tattoos, die nicht leuchten, und behält deinen eigenen Hautton statt dem des Autors. Nimm den Haken in Penumbra weg, wenn du nur das Leuchten willst.
- Proteus erkennt bibo und gen3 direkt. Bei jedem anderen Körper malt es ohne Größenanpassung auf den, den du trägst, und sagt das auch; die Auswahl **Körper** überschreibt das, falls das Paket für etwas anderes gemacht wurde.
- Es gibt keinen Volks- oder Geschlechtsfilter, der Mod bemalt also jeden Charakter mit demselben Material. Schalte ihn in Penumbra für Charaktere aus, für die er nicht gemalt wurde.
- Augenleuchten wird derzeit nicht importiert, aber schreib mir, wenn du daran Interesse hast.


**Presets (`.ptp`)** — ein Look, den jemand für einen Mod geteilt hat, den du schon hast.

Ein Preset ist kein Mod, also wird nichts installiert: Proteus liest, für welchen Mod es gemacht wurde, bietet an, es diesem hinzuzufügen, und sagt Bescheid, wenn du ihn unter dem Namen nicht hast — dann wählst du den richtigen Mod selbst. Es wird gespeichert, nicht getragen; trage es aus dem Presets-Abschnitt dieses Mods unter Farben, wenn du es haben willst.
#### Export

Speichert einen deiner Proteus-Mods als Penumbra-Modpaket (`.pmp`) zum Teilen. Wähle den Mod aus der Liste, drücke **Exportieren** und such einen Ort aus — der Dateiname wird aus dem Mod-Namen gefüllt, und der Dialog öffnet sich beim ersten Mal auf deinem Desktop und danach dort, wo du zuletzt gespeichert hast.

Das Paket ist eine direkte Kopie des Mod-Ordners, es geht also nichts verloren: Optionen, Farbtabellen, Masken, Leuchteffekte und Ausrüstungsebenen kommen alle mit, und das Proteus des Empfängers erkennt sie, sobald Penumbra sie installiert. Auch deaktivierte Mods lassen sich exportieren.

#### Einstellungen

| Einstellung | Was sie tut |
|---------|-------------|
| Aktiviert | Hauptschalter. Aus löscht die Ausgabe von Proteus und zeichnet dich ohne sie neu. |
| Automatisches Neuzeichnen | Lässt Proteus von selbst Schritt halten: Es komponiert nach Zonenwechseln, Ausrüstungswechseln und Neuzeichnen neu und lädt dann deinen Charakter neu, damit du das Ergebnis siehst. Aus macht Proteus weitgehend manuell. Dein Look bleibt erhalten, aber eine Änderung wird erst sichtbar, wenn dich etwas neu zeichnet. |
| Mod-Priorität automatisch anheben | Überschreibt nachweislich ein anderer Mod eine Hauttextur, in die Proteus hineinkomponiert, hebt es die Penumbra-Priorität von Proteus darüber an und sagt das im Chat. |
| Neuladen an Ort und Stelle | Frischt Texturen über Glamourer auf statt mit einem vollen Neuzeichnen und vermeidet so das Flackern durch Verschwinden/Erscheinen. Standardmäßig an. |
| Komprimierung aktivieren | Blockkomprimiert die gebackenen Texturen und schrumpft sie auf etwa ein Viertel ihrer Größe auf der Platte und im VRAM. Standardmäßig an. |
| Hartes Alpha | Experimentell. Hält Sphere-Maps und Metallanteil in der Gruppenpose funktionsfähig, um den Preis härterer Kanten an durchscheinenden Stoffen. |
| Texturcache (MB) | Wie viele dekodierte Texturdaten zwischen zwei Kompositionen im Speicher gehalten werden. |
| Redundante Körper-Meshes ausblenden | Lässt Haut weg, die die zweite Haut sonst doppelt zeichnen würde — Gelenkverstärkungsringe, die ein Nachbarteil ohnehin abdeckt, und zweite Kopien einer Region. Standardmäßig an, auf jedem Körper sicher. |
| Auf unsichtbarer Brille hosten | Lässt die zweite Haut auf dem Gesichtsaccessoire-Platz reiten, damit deine Ringe frei bleiben. |
| Hautton-Unterdrückung | Wie stark sich Overlays dagegen wehren, von deinem Hautton eingefärbt zu werden. |
| Ambient Occlusion / Schattenweichheit / Skindenting | Globale Stärke des Kontaktschattens und der Normal-Vertiefung rund um Riemenkanten. |
| Auf das Licht der Szene reagieren | Lässt Farbzeilen mit **Verblasst im Licht** dunkler werden, je heller das Licht auf dir wird. Aus leuchtet jedes Leuchten überall mit voller Helligkeit. |
| Lichtstärke von Hand festlegen / Lichtstärke | Ignoriert die Szene und nutzt stattdessen den Regler. Das ist der schnellste Weg, ein reines Dunkelleuchten zu sehen, ohne auf die Dämmerung zu warten, und die richtige Einstellung für die Gruppenpose. |

Drei Schaltflächen hier sind erwähnenswert:

- **Geändertes Accessoire wiederherstellen** — erzwingt ein volles Neuzeichnen, falls eine zweite Haut nach dem Deaktivieren oder Tauschen einmal auf einem Ring oder Armband hängen bleibt.
- **Texturcache leeren** — nutzen, wenn eine Texturänderung nicht auftaucht, z. B. weil du ein Overlay in derselben Größe neu exportiert hast.
- **Leuchteffekt-Texturen** — öffnet den Ordner, aus dem Proteus die Scroll-Maps für animiertes Leuchten liest. Lege Bilder hinein, und sie erscheinen in der Effekt-Auswahlliste jedes Ausrüstungs-Overlays. Fahre über die Schaltfläche, um den vollen Pfad zu sehen.

##### Hüte

Die meisten Haar-Mods unterstützen keine Hüte, sodass ein darüber getragener Hut einfach hindurchgeht. Der Abschnitt **Hüte** drückt das Haar, das ein Hut bedecken würde, flach an deinen Kopf. Wie das Studio bearbeitet er die eigenen Dateien des Haar-Mods, die Anpassung funktioniert also weiter, wenn Proteus aus ist, und reist mit dem Mod mit, wenn du ihn exportierst.

- **Frisuren unter Hüte passen lassen** ist standardmäßig aus. Schalte es ein, und Proteus prüft jede Frisur beim Aufsetzen und passt sie an; beim ersten Mal sagt es dir das im Chat.
- Oder passe die Frisur, die du trägst, selbst an. Der Abschnitt sagt, wie viele Punkte angedrückt würden und wie weit, und **Anpassen** schreibt die Änderung.
- **Rückgängig** stellt die Frisur wieder her, die du trägst. Ein Haar-Mod liefert meist ein Modell pro Volk, also stellt **Alle Frisuren dieses Mods zurücksetzen** jede Frisur wieder her, die Proteus in diesem Mod geändert hat. Die Originale werden in `Proteus/hatcompat-backup/` im Mod aufbewahrt.
- Haar, dessen Autor bereits Hut-Unterstützung eingebaut hat, bleibt unangetastet, und Haar aus dem Spiel funktioniert ohnehin. Die Ausnahme ist Hut-Unterstützung, die viel mehr Haar verbirgt, als ein Hut bedeckt, meist übernommen von der Frisur, auf der sie aufbaut. Proteus bietet an, das neu zu vermessen und zu ersetzen.
- Manche Frisuren sind zu Stücken verschweißt, die zu groß sind, als dass das Formformat des Spiels sie ansprechen könnte. Diese Punkte behalten ihre Form, ein Hut kann dort also weiterhin durchschneiden.

### Presets

Ein **Preset** ist ein benannter Look für **einen Mod**: welche seiner Optionen angehakt sind, all seine Farben und seine Leucht- und Ebeneneinstellungen. Aufwendige Pakete — Bodysuits, Strümpfe — bringen ein Dutzend Gruppen mit, und eine sehenswerte Kombination zu finden heißt, so lange an Kästchen herumzuklicken, bis es passt. Ein Preset bewahrt diese Kombination.

Presets liegen in einem aufklappbaren Abschnitt **Presets** unten im Farbeditor eines Mods, unterhalb von Erweitert. Mit **+ Speichern…** hältst du fest, wie der Mod gerade aussieht; das Auswahlfeld daneben bestimmt, welches Preset getragen wird. Zugeklappt nennt die Überschrift weiterhin, was du gerade trägst.

- Mit `*` markierte Presets kamen mit dem Mod. Sie sind schreibgeschützt — beim Bearbeiten bekommst du stattdessen deine eigene Kopie, sodass ein Mod-Update nie etwas überschreibt, was du gespeichert hast.
- Ein `●` neben dem getragenen Preset heißt, dass du seit dem Speichern etwas geändert hast. **Aktualisieren** übernimmt diese Änderungen; ignorierst du es, bleibt das Preset wie es war.
- **Kein Preset** stellt die eigenen Farben des Mods wieder her. Deine Optionshaken bleiben unangetastet — einen Look abzulegen ist keine Aufforderung, dein eigenes Umschalten rückgängig zu machen.
- Presets auszuprobieren kostet nichts. Nur die Optionshaken werden nach Penumbra geschrieben; die Farben und Ebeneneinstellungen liegen nur obenauf, solange ein Preset getragen wird, und die Dateien des Mods werden nie angefasst.

Teile eines mit **Code kopieren** (eine Zeichenkette zum Einfügen in den Chat) oder **Exportieren…** (eine `.ptp`-Datei). Die Gegenseite nutzt **Code einfügen** oder **Importieren…**; ein Preset für einen anderen Mod sagt das, bevor es hinzugefügt wird.

Ein Preset anzuwenden, während eine Design-Bindung aktiv ist, wirkt wie jede andere Bearbeitung als Vorschau auf diese Bindung — drücke **Bindung aktualisieren**, um es zu behalten. Ein angewendetes Glamourer-Design löst alle Presets ab, da das Design eigene Farben mitbringt; die Presets selbst bleiben gespeichert.

Benennt oder entfernt ein Mod-Update eine Option, setzt ein altes Preset alles, was es noch gibt, und sagt dir, was nicht ging.

### Farbeditor

Klicke **Farben** neben einem Mod, um seinen Farbeditor in einem eigenen Fenster zu öffnen. Damit kannst du Overlays einfärben, das Leuchten steuern und Materialeigenschaften je Region setzen, ohne irgendeine Datei zu bearbeiten.

Jede aktive Overlay-Option bekommt oben ihren eigenen Tab, geordnet nach ihrer Stapelung. Ziehe einen Tab, um umzustapeln. Nutzt der Mod Masken, ist ein Tab **Masken** ganz oben angeheftet — Masken werden immer über allem anderen dargestellt.

#### Darstellungsmodus

Proteus leitet aus den tatsächlich genutzten Funktionen ab, wie jedes Overlay dargestellt werden soll, und zeigt das Ergebnis als Plakette **Dargestellt als**:

- **Skin (gemalt)** — in deine Haut komponiert. Der Standard.
- **Cloth** — eine zweite Haut mit Sphere-Maps, Metallanteil oder Glanzlicht.
- **Animiertes Leuchten** — eine zweite Haut mit einem scrollenden Leuchteffekt.

Du musst nichts auswählen — eine gesetzte Sphere-Map macht daraus von selbst Cloth. Willst du es erzwingen, öffne **Erweitert** und hefte einen Modus an. **Auf Standard zurücksetzen** stellt dort die vom Mod hinterlegten Einstellungen wieder her.

#### Erweitert

Unter den Zeilen hält **Erweitert** die Einstellungen bereit, die für den ganzen Mod gelten statt für eine einzelne Zeile:

| Einstellung | Was sie tut |
|---------|-------------|
| Darstellungsmodus erzwingen | Heftet Skin / Cloth / Animiertes Leuchten an, statt die Funktionen entscheiden zu lassen. **Zurück zu automatisch** löst das wieder. |
| Körper | Auf welche Körpertypen dieser Mod gebacken ist — **Alle Körper** (Geschwisterkörper bibo↔gen3/Eve, dazu Vanilla gen2), **bibo+gen3** (nur der Geschwisterkörper — der Standard) oder **Aus**. Gilt für den ganzen Mod und ist eine globale Einstellung: Design-Bindungen erfassen sie nicht. |
| Auf Standard zurücksetzen | Setzt Farben, Leuchten und Modus dieser Option auf die Einstellungen zurück, die Proteus für den Mod zuerst erfasst hat. Halte Strg, um es scharf zu schalten. |

Hat ein Mod keine aktive Option, gibt es keine Farben anzuzeigen, aber **Erweitert** erscheint trotzdem, damit **Körper** erreichbar bleibt.

#### Zeilen

Der Editor zeigt bis zu 16 Farbtabellen-Zeilen. Zeilen entsprechen Regionen, die die Index-Textur des Mods definiert (falls er eine hat). Zeile 16 ist die Ausweichfarbe, die genutzt wird, wenn es keine Index-Textur gibt. Zeilen, die die Index-Textur nie auswählt, werden gedimmt.

Drücke **Leuchten** in einer beliebigen Unterzeile, um diese Region auf deinem Charakter aufleuchten zu lassen, damit du siehst, welche Zeile was steuert.

Jede Zeile hat zwei Unterzeilen:
- **A** — gilt dort, wo der Grünkanal der Index-Textur 255 ist.
- **B** — gilt dort, wo der Grünkanal 0 ist. Werte dazwischen blenden weich ineinander.

Für jede Unterzeile:
- **Diffuse** (Farbfeld) — multiplikative Tönung, die auf das Overlay gelegt wird. Weiß (`#FFFFFF`) zeigt die natürlichen Farben des Overlays. Jede andere Farbe tönt es. Ein schlichter Graustufen-Strumpf lässt sich hier einfach umfärben.
- **Leuchten** (Regler 0–1) — wie stark das Overlay leuchtet, mit eigener Farbe. Haut kann nicht leuchten, also schaltet dieser Wert das Overlay auf eine Stoffebene um, genau wie eine Sphere-Map es tut.
- **Deckkraft** (Regler −100 bis 100) — 0 ist der Standard des Mods. −100 ist durchsichtig. 100 ist vollständig deckend.
- **Sphere-Map / Metallanteil / Rauheit / Glanzlicht** — bei Cloth verfügbar. Sobald du eines davon setzt, schaltet das Overlay auf eine zweite Haut um.

Zeilen und Unterzeilen lassen sich untereinander kopieren und einfügen.

Änderungen greifen sofort auf dem Bildschirm und werden etwa eine Sekunde nach dem letzten Bearbeiten neu komponiert. Sie werden in der `metadata.json` des Mods gespeichert — es sei denn, eine Design-Bindung ist aktiv, dann gehören sie zu diesem Design, bis du **Aktualisieren** drückst.

### Danksagungen
Ganz herzlichen Dank an Sebby dafür, mir beigebracht zu haben, wie man pixelbasiertes Bild-Mapping statt Backen einsetzt, und dafür, die gebackenen Maps über den Loose Texture Compiler unter der MIT-Lizenz veröffentlicht zu haben.

---
