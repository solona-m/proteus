# Proteus

<!--i18n-->
[English](../README.md) · [日本語](README.ja.md) · [Deutsch](README.de.md) · [Français](README.fr.md) · [简体中文](README.zh.md) · [한국어](README.ko.md) · **Español** · [Русский](README.ru.md)
<!--/i18n-->

Proteus es un plugin de Dalamud para FFXIV que compone texturas de superposición sobre la piel y el equipo de tu personaje en tiempo real. Los autores de mods distribuyen pequeñas superposiciones PNG junto a sus mods de Penumbra; Proteus las mezcla con las texturas base cada vez que cambias de opción, sin tocar los archivos originales del mod. Proteus puede importar archivos pmp compatibles con Proteus, archivos omp de superposiciones de Onion y tatuajes luminosos de Atramentum Luminis.

Las superposiciones pueden representarse de dos maneras: pintadas en tu piel, o como una **segunda piel** — una copia de la malla de tu cuerpo dibujada como equipo, de modo que una superposición puede usar sphere maps, metalicidad y brillo animado, cosas que los materiales de piel no permiten.

- **Lleva mods sin renunciar a una ranura de equipo.** Una segunda piel tiene que dibujarse como un objeto, pero Proteus la esconde en algo que no estás usando — unas gafas invisibles, un anillo que no llevas puesto, o añadiéndola a los accesorios que sí llevas — así que tu glamour real queda intacto. No hay nada que configurar: elige el alojamiento por su cuenta y nunca te quita un objeto que llevas puesto.
- **Añade interruptores a cualquier pieza de cualquier mod, no solo a los de Proteus.** Cuando un mod suelda un lazo, un collar o una correa dentro de una geometría que su autor nunca hizo opcional, la pestaña **Interruptores** puede separar esa pieza y darle un interruptor de verdad.


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
| Colores | Abre el editor de colores de ese mod. |
| Skindent | Sombra de oclusión ambiental y hendidura de normales en los bordes de las correas de este mod. «Paquete» sigue lo que pidió el mod; Sí/No lo sobrescribe. |

Pulsa **Recomponer ahora** para forzar una recomposición manual. Proteus también recompone automáticamente cada vez que cambias una opción o un ajuste de Penumbra, cambias de equipo, o cambias de raza o de cuerpo.

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

#### Exportar

Guarda uno de tus mods de Proteus como paquete de mod de Penumbra (`.pmp`) para compartirlo. Elige el mod en la lista desplegable, pulsa **Exportar** y elige dónde ponerlo: el nombre de archivo se rellena a partir del nombre del mod, y el diálogo se abre en tu escritorio la primera vez y después donde guardaste la última vez.

El paquete es una copia directa de la carpeta del mod, así que no se pierde nada: opciones, tablas de colores, máscaras, efectos de brillo y capas de equipo vienen todos, y el Proteus de quien lo reciba los detecta en cuanto Penumbra los instale. También se pueden exportar mods desactivados.

#### Interruptores

Saca una pieza de geometría del modelo de un mod y la pone tras un interruptor: un lazo, un collar, una correa que el autor soldó a una malla siempre visible. Esto funciona con **cualquier** mod que tengas instalado, no solo con los de Proteus.

El interruptor se escribe dentro del propio mod como una opción normal de Penumbra, así que aparece en los ajustes de ese mod y **sigue funcionando con Proteus apagado**.

Elige un mod y luego uno de sus modelos. Las piezas de ese modelo se listan con su número de triángulos y se muestran en un visor al lado: haz clic en una pieza para activarla o desactivarla, arrastra para girar el modelo, Mayús+arrastrar para moverlo, rueda para acercar. Marca las piezas que debe ocultar un interruptor, dale un nombre y pulsa **Crear un interruptor con las piezas marcadas**. Prepara todos los que quieras y luego pulsa **Escribir los interruptores en el mod**.

Cosas que conviene saber:

- **Diez interruptores por objeto.** Es el límite del juego, no de Proteus. Si un autor ya los ha gastado todos, la pestaña lo indica y no te deja añadir más.
- **Solo equipo y accesorios.** En los demás tipos de modelo no hay nada a lo que enganchar un interruptor.
- **Las piezas que el autor ya hizo opcionales no admiten un segundo interruptor**, y la pestaña las marca.
- **Es reversible.** Se conservan los modelos originales, así que **Deshacer: restaurar los modelos originales** deja el mod exactamente como estaba y elimina el grupo de opciones.
- Si un objeto tiene varios archivos de modelo con las piezas dispuestas de forma distinta, Proteus edita solo aquellos en los que el interruptor encaja correctamente y te dice cuáles dejó en paz, en vez de adivinar y tocar la geometría equivocada.

#### Ajustes

| Ajuste | Qué hace |
|---------|-------------|
| Activado | Interruptor maestro. Al apagarlo, Proteus borra su salida y te redibuja sin ella. |
| Desactivar redibujado automático | Impide que Proteus refresque tu personaje después de una composición. |
| Recarga in situ | Refresca las texturas a través de Glamourer en lugar de un redibujado completo, evitando el parpadeo de desaparecer y reaparecer. Activado por defecto. |
| Activar compresión | Comprime por bloques las texturas horneadas, reduciéndolas a cerca de un cuarto de su tamaño en disco y en VRAM. Activado por defecto. |
| Alfa nítido | Experimental. Mantiene funcionando las sphere maps y la metalicidad en pose de grupo, a cambio de bordes más duros en las telas transparentes. |
| Alojar en gafas invisibles | Deja que la segunda piel viaje en la ranura de accesorio facial para que tus anillos sigan libres. |
| Alojar en el Emperor's New Ring | Alojamiento de reserva cuando nada de lo que llevas puede cargar con la segunda piel. Nunca toma un anillo que ya llevas puesto. |
| Atenuación del tono de piel | Con cuánta fuerza resisten las superposiciones ser teñidas por tu tono de piel. |
| Oclusión ambiental / Suavidad de la sombra / Skindenting | Intensidad global de la sombra de contacto y de la hendidura de normales alrededor de los bordes de las correas. |
| Caché de texturas (MB) | Cuántos datos de textura decodificados mantener en memoria entre composiciones. |
| Ocultar mallas de conexión | Omite los anillos de refuerzo de articulaciones de un cuerpo en la segunda piel. Solo hace falta para Neolithe. |

Tres botones de aquí merecen mención:

- **Restaurar accesorio modificado** — fuerza un redibujado completo si alguna vez una segunda piel se queda atascada en un anillo o una pulsera tras desactivarla o cambiarla.
- **Vaciar caché de texturas** — úsalo cuando una edición de textura no aparece, por ejemplo si has vuelto a exportar una superposición con el mismo tamaño.
- **Texturas de efecto de brillo** — abre la carpeta de la que Proteus lee los mapas de desplazamiento del brillo animado. Deja imágenes ahí y aparecerán en la lista Efecto de cada superposición de equipo. Pasa el ratón por el botón para ver la ruta completa.

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
