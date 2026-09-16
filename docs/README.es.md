# Proteus

<!--i18n-->
[English](../README.md) · [日本語](README.ja.md) · [Deutsch](README.de.md) · [Français](README.fr.md) · [简体中文](README.zh.md) · [한국어](README.ko.md) · **Español** · [Русский](README.ru.md)
<!--/i18n-->

Proteus es un plugin de Dalamud para FFXIV que compone texturas de superposición sobre la piel y el equipo de tu personaje en tiempo real. Los autores de mods distribuyen pequeñas superposiciones PNG junto a sus mods de Penumbra; Proteus las mezcla con las texturas base cada vez que cambias de opción, sin tocar los archivos originales del mod. Proteus puede importar archivos pmp compatibles con Proteus, archivos omp de superposiciones de Onion y tatuajes luminosos de Atramentum Luminis. También puede editar los modelos de cualquier mod que tengas instalado, sea de Proteus o no: remodelar la ropa, pintar el balanceo con el viento, añadir interruptores de piezas y adaptar el pelo bajo los sombreros.

Las superposiciones pueden representarse de dos maneras: pintadas en tu piel, o como una **segunda piel** — una copia de la malla de tu cuerpo dibujada como equipo, de modo que una superposición puede usar sphere maps, metalicidad y brillo animado, cosas que los materiales de piel no permiten.

- **Lleva mods sin renunciar a una ranura de equipo.** Una segunda piel tiene que dibujarse como un objeto, pero Proteus la esconde en algo que no estás usando — unas gafas invisibles, un anillo que no llevas puesto, o añadiéndola a los accesorios que sí llevas — así que tu glamour real queda intacto. No hay nada que configurar: elige el alojamiento por su cuenta y nunca te quita un objeto que llevas puesto.
- **Remodela la ropa de cualquier mod, directamente sobre tu personaje.** En la pestaña **Estudio**, pinta sobre una prenda que llevas puesta para apartarla de tu cuerpo donde asoma la piel, empujarla hacia dentro, suavizarla, estirarla sobre un pliegue o pintar dónde la mece el viento. Cada edición se guarda en el mod y se puede deshacer.
- **Añade interruptores a cualquier pieza de cualquier mod, no solo a los de Proteus.** Cuando un mod suelda un lazo, un collar o una correa dentro de una geometría que su autor nunca hizo opcional, la pestaña **Estudio** puede separar esa pieza y darle un interruptor de verdad.
- **Haz que el pelo con mods quepa bajo los sombreros.** Proteus aplasta contra tu cabeza el pelo que cubriría un sombrero, para que el sombrero deje de atravesarlo.


Si necesitas ayuda, consulta primero esta [guía de resolución de problemas](../TROUBLESHOOTING.md).
Después, únete a https://discord.gg/solona y pregunta en el canal #help. Esto es todavía muy nuevo, ¡pero corregiré los fallos lo antes posible!

Si eres creador y quieres hacer mods para Proteus, lee la [guía para creadores](../For%20Creators.md).

---

## Para usuarios

### Instalación

Añade este repositorio https://dl.solona.info/repo.json en la pestaña experimental de /xlplugins.
Guarda y busca Proteus en la ventana principal de /xlplugins.

> ¿Ya lo instalaste desde `raw.githubusercontent.com/solona-m/plugins/main/repo.json`? Eso sigue
> funcionando y siempre lo hará, pero la URL nueva es más fiable y no está sujeta a las
> limitaciones de GitHub.

Instala algunos mods de superposición hechos para Proteus, elige tus opciones y tu personaje se actualizará.

### Ventana de estado

Abre la ventana de estado con `/proteus`. Tiene siete pestañas, y el resultado de la última composición (texturas modificadas, mods usados, hace cuánto) aparece siempre en la parte inferior.

#### Mods

Lista todos los mods de Penumbra que contienen un archivo Proteus. Haz clic en un encabezado de columna para ordenar por él.

| Columna | Qué hace |
|--------|-------------|
| Act. | Activa o desactiva la composición de Proteus para ese mod. |
| Mod | El nombre visible del mod. Haz clic para saltar a ese mod en Penumbra. |
| Prio | Prioridad dentro de la pila de composición de Proteus. Los números más bajos van primero (capa inferior). Arrastra para cambiarla; Ctrl+clic para escribirla. |
| Preajuste | El aspecto guardado que lleva puesto este mod. Elige otro para cambiar sin abrir el editor de color. Muestra — en un mod que no tiene preajustes. |
| Colores | Abre el editor de colores de ese mod. |
| Skindent | Sombra de oclusión ambiental y hendidura de normales en los bordes de las correas de este mod. «Paquete» sigue lo que pidió el mod; Sí/No lo sobrescribe. |

Pulsa **Recomponer ahora** para forzar una recomposición manual. Proteus también recompone automáticamente cada vez que cambias una opción o un ajuste de Penumbra, cambias de equipo, o cambias de raza o de cuerpo.

#### Estudio

Edita los modelos de **cualquier** mod que tengas instalado, no solo los de Proteus. Puedes remodelar la ropa para que tu cuerpo deje de asomar a través de ella, pintar dónde la mece el viento, o separar una pieza tras su propio interruptor. Cada cambio se escribe en los archivos del propio mod, así que **sigue funcionando con Proteus apagado** y viaja con el mod si lo exportas.

Elige un mod y luego uno de sus modelos. La pestaña se abre en la pieza de torso que llevas puesta (o en las piernas si no hay ninguna), y los mods y modelos que llevas puestos aparecen primero, en verde. Hacer clic en una prenda de tu personaje abre su mod y su modelo.

Las herramientas están en un panel a la izquierda:

| Herramienta | Qué hace |
|------|-------------|
| Alternar piezas | Elige piezas del modelo y les da un interruptor. Consulta [Interruptores de piezas](#interruptores-de-piezas) más abajo. |
| Tirar afuera | Empuja la superficie hacia fuera, de modo que un cuerpo que asoma a través de una prenda vuelve a quedar cubierto. La tela se aleja en línea recta de la piel que tiene debajo. |
| Empujar adentro | El mismo pincel a la inversa, para ropa que queda demasiado separada del cuerpo. |
| Suavizar | Alisa la superficie: bultos que la ropa ya traía, o un tirón que quedó áspero. Como el relax de 3ds Max, encoge, así que las curvas se aplanan y la tela puede hundirse hacia el cuerpo. |
| Puente | Pinta sobre un hueco, como la hendidura entre los glúteos o un pliegue, para estirar la tela recta por encima en lugar de que siga al cuerpo hacia dentro. Solo levanta. |
| Viento | Pinta cuánto mece el viento del juego la prenda, mostrado en rojo. Mantén Ctrl, o pon **Cantidad** a 0 %, para borrar. |

##### Uso del pincel

- **Pintas directamente sobre tu personaje**, y una prenda muestra cada trazo mientras lo pintas. Mantén **Alt** para mover la cámara. Marca **Mostrar modelo** para pintar en cambio sobre el modelo en la ventana: arrastra desde el fondo para girarlo, Mayús+arrastrar para moverlo, rueda para acercar.
- **Tamaño del pincel** baja hasta 1 mm. `[` y `]` lo cambian, y Mayús da pasos más finos. El efecto es más fuerte en el centro y se desvanece hasta nada en el borde. **Fuerza** es cuánto se mueve la superficie por cada instante de pintura. Lo normal es que baste con poco: la ropa solo tiene que separarse del cuerpo una fracción de milímetro, y siempre puedes volver a pintar el mismo sitio.
- **Espejo izquierda/derecha** pinta los dos lados a la vez.
- **La piel nunca se mueve.** Para que un pincel no toque ninguna otra cosa, bloquéala: Mayús+clic en la pieza sobre tu personaje o el modelo, o desmárcala en la lista de piezas. Las costuras soldadas a una pieza bloqueada también se mantienen.
- **También funciona con el pelo.** El pelo, la cara, las orejas y la cola se redibujan al soltar, en lugar de mostrar el trazo mientras pintas.
- **Aplicar a otras tallas** copia la edición a los demás archivos del mod para la misma prenda, emparejados según dónde quedan sobre ella.

##### Guardado y deshacer

- Cada trazo se guarda en el mod un momento después de soltar. El primer guardado copia el modelo original en `Proteus/meshvolume-backup/` dentro del mod.
- **Deshacer trazo** (o Ctrl+Z) retira el último trazo. **Empezar de nuevo** descarta todos los trazos de este modelo.
- **Deshacer lo guardado** (mantén Ctrl o Mayús y haz clic) devuelve cada modelo que el pincel cambió en este mod exactamente a como lo hizo su autor.
- Los interruptores de piezas, el ajuste a sombreros y el pincel guardan cada uno su propia copia de seguridad. Si más de uno ha cambiado el mismo modelo, deshaz primero el más reciente. Proteus te avisa si lo intentas en otro orden.
- Los deslizadores de cuerpo de un modelo no siempre pueden seguir una edición. Cuando algunos puntos no pudieron moverse con ella, Proteus dice cuántos, y activar ese deslizador puede hacer que la ropa vuelva a atravesarse en algunos sitios.
- La mayoría de las mallas con mods no tienen canal de viento. El primer trazo de viento que se guarda añade uno, y ajusta los materiales de la prenda para que el viento pueda moverla. Los materiales que vienen del juego y no del mod no se pueden ajustar.

##### Interruptores de piezas

**Alternar piezas** saca una pieza de geometría del modelo de un mod y la pone tras un interruptor: un lazo, un collar, una correa que el autor soldó a una malla siempre visible.

El interruptor se escribe dentro del propio mod como una opción normal de Penumbra, así que aparece en los ajustes de ese mod.

Las piezas del modelo se listan con su número de triángulos. Haz clic en una pieza sobre tu personaje o en la vista del modelo para marcarla. Marca las piezas que debe ocultar un interruptor, dale un nombre y pulsa **Crear un interruptor con las piezas marcadas**. Prepara todos los que quieras y luego pulsa **Escribir los interruptores en el mod**.

Cosas que conviene saber:

- **Diez interruptores por objeto.** Es el límite del juego, no de Proteus. Si un autor ya los ha gastado todos, la pestaña lo indica y no te deja añadir más.
- **Solo equipo y accesorios.** En los demás tipos de modelo no hay nada a lo que enganchar un interruptor.
- **Una pieza que el autor ya hace opcional también admite el tuyo.** Los dos se acumulan: la pieza solo se muestra cuando ambos están activados.
- **Es reversible.** Se conservan los modelos originales, así que **Deshacer: restaurar los modelos originales** deja el mod exactamente como estaba y elimina el grupo de opciones.
- Si un objeto tiene varios archivos de modelo con las piezas dispuestas de forma distinta, Proteus edita solo aquellos en los que el interruptor encaja correctamente y te dice cuáles dejó en paz, en vez de adivinar y tocar la geometría equivocada.

#### Vínculos

Ata toda tu configuración de Proteus — qué mods están activos, sus prioridades y opciones, y todos sus colores — a un diseño de Glamourer. Marca **Vincular el estado de Proteus a los diseños de Glamourer** para activarlo.

Guardar un diseño captura con él el estado actual de Proteus. Aplicar ese diseño más tarde lo restaura. Los colores y los ajustes de capa se restauran como una superposición en vivo, así que los archivos del propio mod nunca se reescriben.

Mientras un vínculo está activo, los cambios en el editor de colores se previsualizan al instante pero **no** se guardan hasta que pulsas **Actualizar**, lo que pliega todo lo que hay en pantalla de vuelta a ese diseño.

#### Crear

Crea un mod de superposición básico sin salir del juego. Dale un nombre, un autor y elige al menos una textura (difusa, máscara, normales o índice). El material objetivo se rellena solo a partir del cuerpo que llevas puesto; puedes elegir otro material equipado en la lista desplegable o escribir una ruta a mano. Proteus escribe un nuevo mod de Penumbra y lo abre.

Las ranuras de textura que el material elegido no puede usar aparecen atenuadas.

#### Importar

Toma un paquete de mod y lo convierte en un mod de Proteus. Se admiten tres tipos:

**Mods normales de Penumbra (`.pmp`)** — lleva partes de un mod de equipo corriente sin ocupar una ranura, y consigue además las funciones avanzadas de tabla de colores.

Sigue siendo un mod de Penumbra corriente: Penumbra sigue decidiendo si está activo y qué opciones están seleccionadas. Lo que cambia es que sus piezas se dibujan sobre el objeto portador de Proteus en lugar de sobre una ranura de equipo real, así que tu glamour queda intacto.

El efecto secundario útil es que **puedes llevar varias de sus opciones a la vez**. Normalmente, dos opciones del mismo grupo reclaman la misma ruta de modelo y el juego solo puede mostrar una, así que un paquete no puede ofrecer físicamente «esta pieza *y* aquella». Tras importarlo, cada pieza seleccionada se añade por separado.

- Las piezas llegan **desactivadas**. Marca después en Penumbra las que quieras; hasta entonces no se lleva nada puesto.
- Un paquete que *ya* es un mod de Proteus se instala exactamente como lo construyó su autor. No se convierte nada.
- La piel se elimina durante la importación. Esto es ideal para accesorios como joyas, piercings y chaquetas. Si importas una camisa, solo encajará si tu ranura de torso equipada es de la misma talla.

**Paquetes de superposición de Onion (`.omp`)** — lleva sus capas como superposiciones de Proteus que puedes recolorear, reordenar, hacer brillar, etc.

Un paquete que incluye el mismo arte en varias disposiciones UV (bibo, gen3, vanilla) se convierte en un grupo de selección única **Body UV** en Penumbra, preajustado a la disposición que corresponde al cuerpo que llevas, de modo que solo se compone una a la vez. La opacidad de una capa va horneada en la imagen; una capa con un modo de fusión distinto de Normal se omite y se indica, porque Proteus solo compone alpha-over. Los grupos de opciones y los filtros de raza propios de Onion no se importan.

**Tatuajes luminosos de Atramentum Luminis (`.ttmp2`)** — lleva el brillo como una superposición de Proteus que puedes recolorear y atenuar, sin ningún mod de shader.

Los paquetes de Atramentum Luminis esconden su brillo en el canal alfa de una textura, y sin ese mod de shader instalado no representan absolutamente nada. Proteus extrae el brillo y lo reconstruye como una superposición normal: los paneles que el artista marcó se convierten en una segunda piel, y el propio arte alimenta un material de brillo animado, así que el neón conserva sus colores píxel a píxel. El control **Brillo** en Colores hace entonces lo que esperas, y puedes vincular todo el conjunto a un diseño como cualquier otra superposición.

- La textura de cuerpo del paquete también entra, como una opción aparte llamada **Piel del autor**, activada por defecto: lleva las partes del tatuaje que no brillan, y conserva tu propio tono de piel en lugar del del autor. Desmárcala en Penumbra si solo quieres el brillo.
- Proteus reconoce bibo y gen3 directamente. Para cualquier otro cuerpo, pinta sobre el que llevas puesto sin redimensionar, y lo indica; el selector **Cuerpo** lo sobrescribe si el paquete se hizo para otra cosa.
- No hay filtro de raza ni de sexo, así que el mod pinta cualquier personaje que tenga un cuerpo con el mismo material. Desactívalo en Penumbra para los personajes para los que no fue pintado.
- El brillo de ojos no se importa hoy por hoy, pero escríbeme si te interesa.


**Preajustes (`.ptp`)** — un aspecto que alguien compartió para un mod que ya tienes.

Un preajuste no es un mod, así que no se instala nada: Proteus lee para qué mod se hizo, se ofrece a añadirlo a ese, y lo dice si no lo tienes con ese nombre — entonces eliges tú el mod correcto. Se guarda, no se pone; llévalo desde la sección Preajustes de ese mod en Color cuando quieras.
#### Exportar

Guarda uno de tus mods de Proteus como paquete de mod de Penumbra (`.pmp`) para compartirlo. Elige el mod en la lista desplegable, pulsa **Exportar** y elige dónde ponerlo: el nombre de archivo se rellena a partir del nombre del mod, y el diálogo se abre en tu escritorio la primera vez y después donde guardaste la última vez.

El paquete es una copia directa de la carpeta del mod, así que no se pierde nada: opciones, tablas de colores, máscaras, efectos de brillo y capas de equipo vienen todos, y el Proteus de quien lo reciba los detecta en cuanto Penumbra los instale. También se pueden exportar mods desactivados.

#### Ajustes

| Ajuste | Qué hace |
|---------|-------------|
| Activado | Interruptor maestro. Al apagarlo, Proteus borra su salida y te redibuja sin ella. |
| Redibujado automático | Deja que Proteus se mantenga al día por su cuenta: recompone tras cambiar de zona, cambiar de equipo y los redibujados, y luego recarga tu personaje para que veas el resultado. Al apagarlo, Proteus pasa a ser casi del todo manual. Tu aspecto se mantiene, pero una edición no se verá hasta que algo te redibuje. |
| Subir prioridad del mod automáticamente | Cuando se confirma que otro mod está sobrescribiendo una textura de piel en la que Proteus compone, sube la prioridad de Proteus en Penumbra por encima de ese mod y lo indica en el chat. |
| Recarga in situ | Refresca las texturas a través de Glamourer en lugar de un redibujado completo, evitando el parpadeo de desaparecer y reaparecer. Activado por defecto. |
| Activar compresión | Comprime por bloques las texturas horneadas, reduciéndolas a cerca de un cuarto de su tamaño en disco y en VRAM. Activado por defecto. |
| Alfa nítido | Experimental. Mantiene funcionando las sphere maps y la metalicidad en pose de grupo, a cambio de bordes más duros en las telas transparentes. |
| Caché de texturas (MB) | Cuántos datos de textura decodificados mantener en memoria entre composiciones. |
| Ocultar mallas de cuerpo redundantes | Omite la piel que la segunda piel dibujaría dos veces: anillos de refuerzo de articulaciones que la parte vecina ya cubre, y copias sobrantes de una región. Activado por defecto, seguro en cualquier cuerpo. |
| Alojar en gafas invisibles | Deja que la segunda piel viaje en la ranura de accesorio facial para que tus anillos sigan libres. |
| Atenuación del tono de piel | Con cuánta fuerza resisten las superposiciones ser teñidas por tu tono de piel. |
| Oclusión ambiental / Suavidad de la sombra / Skindenting | Intensidad global de la sombra de contacto y de la hendidura de normales alrededor de los bordes de las correas. |
| Reaccionar a la luz de la escena | Deja que las filas de color marcadas con **Se desvanece con luz** se atenúen a medida que aumenta la luz sobre ti. Al apagarlo, todos los brillos arden a pleno en todas partes. |
| Fijar el nivel de luz a mano / Nivel de luz | Ignora la escena y usa el deslizador en su lugar. Es la forma más rápida de ver un brillo solo nocturno sin esperar al anochecer, y el ajuste adecuado para la pose de grupo. |

Tres botones de aquí merecen mención:

- **Restaurar accesorio modificado** — fuerza un redibujado completo si alguna vez una segunda piel se queda atascada en un anillo o una pulsera tras desactivarla o cambiarla.
- **Vaciar caché de texturas** — úsalo cuando una edición de textura no aparece, por ejemplo si has vuelto a exportar una superposición con el mismo tamaño.
- **Texturas de efecto de brillo** — abre la carpeta de la que Proteus lee los mapas de desplazamiento del brillo animado. Deja imágenes ahí y aparecerán en la lista Efecto de cada superposición de equipo. Pasa el ratón por el botón para ver la ruta completa.

##### Sombreros

La mayoría del pelo con mods no tiene soporte para sombreros, así que un sombrero puesto encima lo atraviesa. La sección **Sombreros** aplasta contra tu cabeza el pelo que cubriría un sombrero. Igual que el Estudio, edita los archivos del propio mod de pelo, así que el ajuste sigue funcionando con Proteus apagado y viaja con el mod si lo exportas.

- **Adaptar los peinados a los sombreros** está desactivado por defecto. Actívalo y Proteus revisa cada peinado cuando te lo pones y lo adapta, avisándote en el chat la primera vez.
- O adapta tú mismo el peinado que llevas puesto. La sección dice cuántos puntos se aplastarían y cuánto, y **Adaptar** escribe el cambio.
- **Deshacer** devuelve el peinado que llevas puesto. Un mod de pelo suele traer un modelo por raza, así que **Deshacer los peinados de este mod** devuelve todos los peinados que Proteus cambió en ese mod. Los originales se guardan en `Proteus/hatcompat-backup/` dentro del mod.
- El pelo cuyo autor ya añadió soporte para sombreros se deja en paz, y el pelo que viene con el juego ya funciona. La excepción es un soporte para sombreros que oculta mucho más pelo del que cubre un sombrero, normalmente heredado del peinado a partir del cual se construyó. Proteus se ofrece a medirlo de nuevo y reemplazarlo.
- Algunos peinados están soldados en piezas demasiado grandes para que el formato de formas del juego pueda abarcarlas. Esos puntos conservan su forma, así que un sombrero puede seguir atravesándolos ahí.

### Preajustes

Un **preajuste** es un aspecto con nombre para **un solo mod**: qué opciones tiene marcadas, todos sus colores y su brillo y ajustes de capa. Los paquetes complejos — monos, medias — traen una docena de grupos, y dar con una combinación que merezca la pena significa toquetear casillas hasta que algo encaje. Un preajuste conserva esa combinación.

Los preajustes están en una sección plegable **Preajustes** al final del editor de color de un mod, debajo de Avanzado. **+ Guardar…** conserva con un nombre el aspecto actual del mod; la lista desplegable de al lado elige cuál se lleva puesto. Plegada, la cabecera sigue diciendo cuál llevas.

- Los preajustes marcados con `*` vinieron con el mod. Son de solo lectura: al editar uno obtienes tu propia copia, así que una actualización del mod nunca puede sobrescribir lo que guardaste.
- Un `●` junto al preajuste puesto significa que has cambiado algo desde que se guardó. **Actualizar** incorpora esos cambios; si lo ignoras, el preajuste se queda como estaba.
- **Sin preajuste** devuelve los colores propios del mod. Tus opciones marcadas se quedan como están: quitar un aspecto no es pedir deshacer tus propios cambios.
- Probar preajustes es gratis. Solo las opciones marcadas se escriben en Penumbra; los colores y ajustes de capa van por encima mientras se lleva un preajuste, y los archivos del mod nunca se tocan.

Comparte uno con **Copiar código** (una cadena para pegar en el chat) o **Exportar…** (un archivo `.ptp`). Al otro lado se usa **Pegar código** o **Importar…**; un preajuste hecho para otro mod lo advierte antes de añadirse.

Aplicar un preajuste con un vínculo de diseño activo hace vista previa sobre ese vínculo, como cualquier otra edición: pulsa **Actualizar vínculo** para conservarlo. Aplicar un diseño de Glamourer quita los preajustes puestos, ya que el diseño lleva sus propios colores; los preajustes en sí siguen guardados.

Si una actualización del mod renombra o quita una opción, aplicar un preajuste antiguo pone todo lo que aún existe y te dice qué no pudo.

### Editor de colores

Haz clic en **Colores** junto a un mod para abrir su editor de colores en su propia ventana. Permite teñir superposiciones, controlar el brillo y ajustar propiedades de material por región sin editar ningún archivo.

Cada opción de superposición activa tiene su propia pestaña arriba, ordenadas según cómo se apilan. Arrastra una pestaña para reordenarlas. Si el mod usa máscaras, se fija arriba una pestaña **Máscaras**: las máscaras siempre se representan por encima de todo lo demás.

#### Modo de representación

Proteus deduce cómo debe representarse cada superposición a partir de las funciones que realmente usas, y muestra el resultado como una insignia **Se representa como**:

- **Skin (pintado)** — compuesto en tu piel. El valor por defecto.
- **Cloth** — una segunda piel que usa sphere maps, metalicidad o especular.
- **Brillo animado** — una segunda piel con un efecto de brillo desplazable.

No tienes que elegir: poner una sphere map ya lo convierte en Cloth por sí solo. Si necesitas forzarlo, abre **Avanzado** y fija un modo. **Restaurar valores predeterminados** devuelve ahí los ajustes que el mod trae de fábrica.

#### Avanzado

Bajo las filas, **Avanzado** guarda los ajustes que se aplican a todo el mod en lugar de a una sola fila:

| Ajuste | Qué hace |
|---------|-------------|
| Forzar modo de representación | Fija Skin / Cloth / Brillo animado en vez de dejar que decidan las funciones. **Volver a automático** lo libera. |
| Cuerpos | Sobre qué tipos de cuerpo está horneado este mod: **Todos los cuerpos** (cuerpo hermano bibo↔gen3/Eve, más el vanilla gen2), **bibo+gen3** (solo el cuerpo hermano, el valor por defecto) o **Ninguno**. Se aplica a todo el mod y es un ajuste global: los vínculos de diseño no lo capturan. |
| Restaurar valores predeterminados | Devuelve los colores, el brillo y el modo de esta opción a los ajustes que Proteus registró por primera vez para el mod. Mantén Ctrl para armarlo. |

Si un mod no tiene ninguna opción activa no hay colores que mostrar, pero **Avanzado** sigue apareciendo para que **Cuerpos** siga estando a mano.

#### Filas

El editor muestra hasta 16 filas de tabla de colores. Las filas corresponden a regiones definidas por la textura de índice del mod (si tiene una). La fila 16 es el color de reserva que se usa cuando no hay textura de índice. Las filas que la textura de índice nunca selecciona aparecen atenuadas.

Pulsa **Iluminar** en cualquier subfila para encender esa región en tu personaje y ver así qué fila controla qué.

Cada fila tiene dos subfilas:
- **A** — se aplica donde el canal verde de la textura de índice vale 255.
- **B** — se aplica donde el canal verde vale 0. Los valores intermedios se mezclan de forma suave.

Para cada subfila:
- **Difusa** (muestra de color) — tinte multiplicativo aplicado a la superposición. El blanco (`#FFFFFF`) muestra los colores naturales de la superposición. Cualquier otro color la tiñe. Puedes recolorear una media gris lisa eligiendo un color aquí.
- **Brillo** (deslizador 0–1) — con cuánta fuerza brilla la superposición, con su propio color. La piel no puede brillar, así que ajustar esto cambia la superposición a una capa de tela, igual que hace una sphere map.
- **Opacidad** (deslizador de −100 a 100) — 0 es el valor por defecto del mod. −100 es transparente. 100 es totalmente opaco.
- **Sphere map / Metalicidad / Rugosidad / Especular** — disponibles en Cloth. Ajustar cualquiera de ellos cambia la superposición a una segunda piel.

Las filas y subfilas se pueden copiar y pegar entre sí.

Los cambios se aplican en pantalla al instante y se recomponen alrededor de un segundo después de que dejes de editar. Se guardan en el `metadata.json` del mod, salvo que haya un vínculo de diseño activo, en cuyo caso pertenecen a ese diseño hasta que pulses **Actualizar**.

### Agradecimientos
Muchísimas gracias a Sebby por enseñarme a usar el mapeo de imágenes basado en píxeles en lugar de hornear, y por publicar los mapas horneados bajo licencia MIT a través del loose texture compiler.

---
