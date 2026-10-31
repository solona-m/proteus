# Proteus

<!--i18n-->
[English](../README.md) · [日本語](README.ja.md) · [Deutsch](README.de.md) · [Français](README.fr.md) · [简体中文](README.zh.md) · [한국어](README.ko.md) · **Español** · [Русский](README.ru.md)
<!--/i18n-->

Proteus es un plugin de Dalamud para FFXIV que ofrece las siguientes funciones:

- **Compón texturas de superposición.** Añade superposiciones sobre la piel y el equipo de tu personaje en tiempo real, para que puedas activarlas y desactivarlas como mods normales. Las superposiciones se aplican a todas las UV compatibles, como bibo/gen3/gen2/vanilla.
- **Haz que el equipo se ajuste solo al ponértelo.** Elige tu cuerpo y tu talla una vez por colección de Penumbra, y el equipo de torso, piernas, manos y pies, y los accesorios que cubren el cuerpo, como una chaqueta en un collar, se ajustan a él en cuanto te los pones.
- **Estudio.** Ofrece herramientas al estilo de Blender para manipular mallas directamente sobre tu personaje: corregir clipping, añadir efectos de viento y adaptar por lotes el equipo a muchos cuerpos en segundos.
- **Lleva mods sin renunciar a una ranura de equipo.** Una segunda piel tiene que dibujarse como un objeto, pero Proteus la esconde en algo que no estás usando — unas gafas invisibles, un anillo que no llevas puesto, o añadiéndola a los accesorios que sí llevas — así que tu glamour real queda intacto. No hay nada que configurar: elige el alojamiento por su cuenta y nunca te quita un objeto que llevas puesto.
- **Añade interruptores a cualquier pieza de cualquier mod, no solo a los de Proteus.** Cuando un mod suelda un lazo, un collar o una correa dentro de una geometría que su autor nunca hizo opcional, la pestaña **Estudio** puede separar esa pieza y darle un interruptor de verdad.
- **Lleva mods de pelo hechos para otra raza.** Muchos mods de pelo están hechos para una sola raza, así que en cualquier otra el juego muestra su propio peinado en su lugar. Proteus adapta el peinado del mod a la cabeza de tu raza y lo añade al mismo mod, físicas incluidas, así que simplemente funciona.
- **Haz que el pelo con mods quepa bajo los sombreros.** Proteus aplasta contra tu cabeza el pelo que cubriría un sombrero, para que el sombrero deje de atravesarlo. Está activado por defecto y solo actúa mientras llevas un sombrero puesto.


Si necesitas ayuda, consulta primero esta [guía de resolución de problemas](../TROUBLESHOOTING.md).
Después, únete a https://discord.gg/solona y pregunta en el canal #help. Esto es todavía muy nuevo, ¡pero corregiré los fallos lo antes posible!

Si eres creador y quieres hacer mods para Proteus, lee la [guía para creadores](../For%20Creators.md).

Cómo se usó y cómo no se usó la IA para hacer Proteus (no se usa ninguna IA en tiempo de ejecución en tu juego): [declaración de IA](AI-DECLARATION.es.md)

---

## Para usuarios

### Instalación

Añade este repositorio https://dl.solona.info/repo.json en la pestaña experimental de /xlplugins.
Guarda y busca Proteus en la ventana principal de /xlplugins.

> ¿Ya lo instalaste desde `raw.githubusercontent.com/solona-m/plugins/main/repo.json`? Eso sigue
> funcionando y siempre lo hará, pero la URL nueva es más fiable y no está sujeta a las
> limitaciones de GitHub.

Instala algunos mods de superposición hechos para Proteus, elige tus opciones y tu personaje se actualizará.

### Comandos de chat

| Comando | Qué hace |
|---------|-------------|
| `/proteus` | Abre o cierra la ventana de estado. |
| `/proteus config` | Abre la ventana de estado en Ajustes. |
| `/proteus refresh` | Recompone ahora, igual que el botón **Actualizar**. Útil en una macro. |
| `/proteus refresh full` | Primero lo reconstruye todo y luego recompone, igual que Mayús+clic en **Actualizar**. Más lento que una recomposición normal. |
| `/proteus refresh silent` | Cualquiera de los anteriores sin el mensaje en el chat. `full` y `silent` se pueden combinar. |

### Ventana de estado

Abre la ventana de estado con `/proteus`. Tiene siete pestañas, y el resultado de la última composición (texturas modificadas, mods usados, hace cuánto) aparece siempre en la parte inferior.

Tras una actualización, una ventana **Novedades de Proteus** se abre una vez para mostrar qué ha cambiado. No vuelve a abrirse hasta que haya algo nuevo.

#### Mods

Lista todos los mods de Penumbra que contienen un archivo Proteus. Haz clic en un encabezado de columna para ordenar por él. Pasa el ratón por el nombre de un mod para ver la imagen que su autor incluyó con él.

| Columna | Qué hace |
|--------|-------------|
| Act. | Activa o desactiva la composición de Proteus para ese mod. |
| Mod | El nombre visible del mod. Haz clic para saltar a ese mod en Penumbra. |
| Prio | Prioridad dentro de la pila de composición de Proteus. Los números más bajos van primero (capa inferior). Arrastra para cambiarla; Ctrl+clic para escribirla. |
| Preajuste | El aspecto guardado que lleva puesto este mod. Elige otro para cambiar sin abrir el editor de color. Muestra — en un mod que no tiene preajustes. |
| Colores | Abre el editor de colores de ese mod. Skindent, la sombra de contacto y la hendidura en los bordes de las correas, se ajusta ahí, en **Efectos**. |

Pulsa **Actualizar** para forzar una recomposición manual, o Mayús+clic para reconstruirlo todo primero. Proteus también recompone automáticamente cada vez que cambias una opción o un ajuste de Penumbra, cambias de equipo, o cambias de raza o de cuerpo.

#### Estudio

Edita los modelos de **cualquier** mod que tengas instalado, no solo los de Proteus, incluidas las prendas de los paquetes que hayas importado. Puedes remodelar la ropa para que tu cuerpo deje de asomar a través de ella, mover o cambiar el tamaño de partes de ella, reajustarla a otro cuerpo, pintar dónde la mece el viento, o separar una pieza tras su propio interruptor. Cada cambio se escribe en los archivos del propio mod, así que **sigue funcionando con Proteus apagado** y viaja con el mod si lo exportas.

Elige un mod y luego uno de sus modelos. La pestaña se abre en la pieza de torso que llevas puesta (o en las piernas si no hay ninguna), y los mods y modelos que llevas puestos aparecen primero, en verde. Hacer clic en una prenda de tu personaje abre su mod y su modelo, sea vanilla o con mods. Todas las listas de la pestaña se pueden buscar: escribe parte de un nombre para acotarla. Cuando un objeto trae un modelo distinto para cada raza, cada uno lleva el nombre de su raza para que puedas distinguirlos.

Las herramientas están a la izquierda y los ajustes de la herramienta elegida a su lado, con las piezas del modelo tras un botón en la parte superior de los ajustes: al pulsarlo, la lista de piezas se abre como una columna a su lado y empuja el modelo a la derecha para que siga a la vista. Marca **Mostrar modelo** para tener una tercera columna con el modelo a la derecha.

El **equipo vanilla** que llevas puesto también aparece, marcado como *(vanilla)*. Se abre tal cual con cualquier herramienta, y no se guarda nada hasta que lo cambias: tu primer cambio lo copia a un mod propio con el nombre del objeto, *<Objeto> — Proteus edit*, y los cambios posteriores también van ahí. Si borras ese mod en Penumbra, no vuelve; abrir la pieza de nuevo no lo recrea. Con **Talla del cuerpo** elegida, guardar un reajuste crea en su lugar un mod propio, ver [Talla de cuerpo](#talla-de-cuerpo).

Las herramientas:

| Herramienta | Qué hace |
|------|-------------|
| Alternar piezas | Elige piezas del modelo y les da un interruptor. Consulta [Interruptores de piezas](#interruptores-de-piezas) más abajo. |
| Tirar afuera | Empuja la superficie hacia fuera, de modo que un cuerpo que asoma a través de una prenda vuelve a quedar cubierto. La tela se aleja en línea recta de la piel que tiene debajo. |
| Empujar adentro | El mismo pincel a la inversa, para ropa que queda demasiado separada del cuerpo. |
| Suavizar | Alisa la superficie: bultos que la ropa ya traía, o un tirón que quedó áspero. Como el relax de 3ds Max, encoge, así que las curvas se aplanan y la tela puede hundirse hacia el cuerpo. |
| Alisar | Elimina bultos y ondulaciones **sin** encoger: a diferencia de Suavizar, las curvas conservan su forma y la tela se mantiene separada del cuerpo. **Fuerza** fija lo rápido que actúa. |
| Agarrar | Presiona sobre la tela y arrastra: la tela alrededor del pincel sigue al ratón como si fuera goma, con más fuerza en el centro y sin borde duro. El tamaño del pincel fija cuánta tela acompaña, y Ctrl+Z deshace un agarre. |
| Puente | Pinta sobre un hueco, como la hendidura entre los glúteos o un pliegue, para estirar la tela recta por encima en lugar de que siga al cuerpo hacia dentro. Solo levanta. |
| Viento | Pinta cuánto mece el viento del juego la prenda, mostrado en rojo. Mantén Ctrl, o pon **Cantidad** a 0 %, para borrar. |
| Mover | Arrastra una pieza, o unos pocos polígonos, con flechas: una flecha la mueve a lo largo de ese eje, un cuadrado por ese plano. Ver [Mover, girar y escalar](#mover-girar-y-escalar) más abajo. |
| Girar | Gira una pieza alrededor de su centro: arrastra un anillo de color para girarla sobre ese eje, o el anillo exterior para girarla respecto a tu vista. |
| Escalar | Agranda, encoge o estira una pieza alrededor de su centro: arrastra un tirador de color para estirarla a lo largo de ese eje, o presiona sobre la pieza y arrastra a la derecha para agrandarla, a la izquierda para encogerla. |
| Talla de cuerpo | Reajusta la prenda a otra talla de cuerpo, o a otro mod de cuerpo. Ver [Talla de cuerpo](#talla-de-cuerpo) más abajo. |

##### Uso del pincel

- **Pintas directamente sobre tu personaje**, y una prenda muestra cada trazo mientras lo pintas. Mantén **Alt** para mover la cámara. Marca **Mostrar modelo** para pintar en cambio sobre el modelo en la ventana: arrastra desde el fondo para girarlo, Mayús+arrastrar para moverlo, rueda para acercar.
- **Tamaño del pincel** baja hasta 1 mm. `[` y `]` lo cambian, y Mayús da pasos más finos. El efecto es más fuerte en el centro y se desvanece hasta nada en el borde; el alcance del pincel se dibuja como un arcoíris sobre la tela para que puedas verlo. Los pinceles trabajan al mismo ritmo con cualquier tasa de fotogramas. Para **Tirar afuera** y **Empujar adentro**, **Fuerza** es cuánto mueve la superficie el centro del pincel por cada cuarto de segundo que lo mantienes; para los demás pinceles es lo rápido que actúan. Lo normal es que baste con poco: la ropa solo tiene que separarse del cuerpo una fracción de milímetro, y siempre puedes volver a pintar el mismo sitio.
- **Límite** es lo máximo que los pinceles pueden alejar cualquier punto de donde lo puso el autor del mod. La pintura se detiene en el límite, así que un pincel mantenido no puede inflar la prenda. Súbelo para un cambio mayor.
- **Espejo izquierda/derecha** pinta los dos lados a la vez. Mover, Girar y Escalar también se reflejan.
- **La piel nunca se mueve.** Para que un pincel no toque ninguna otra cosa, bloquéala: Mayús+clic en la pieza sobre tu personaje o el modelo, o desmárcala en la lista de piezas. Una pieza bloqueada además se oculta, en el modelo y en tu personaje, así que los clics la atraviesan hasta lo que hay debajo: bloquea una falda para llegar a la ropa interior que cubre. Las costuras soldadas a una pieza bloqueada también se mantienen.
- **También funciona con el pelo.** El pelo, la cara, las orejas y la cola se redibujan al soltar, en lugar de mostrar el trazo mientras pintas.
- **Aplicar a otras tallas** copia la edición a los demás archivos del mod para la misma prenda, emparejados según dónde quedan sobre ella.

##### Guardado y deshacer

- Cada trazo se guarda en el mod un momento después de soltar. El primer guardado copia el modelo original en `Proteus/meshvolume-backup/` dentro del mod.
- **Deshacer trazo** (o Ctrl+Z) retira el último trazo. **Empezar de nuevo** descarta todos los trazos de este modelo.
- **Deshacer lo guardado** (mantén Ctrl o Mayús y haz clic) devuelve cada modelo que el pincel cambió en este mod exactamente a como lo hizo su autor.
- Los interruptores de piezas, el ajuste a sombreros y el pincel guardan cada uno su propia copia de seguridad. Si más de uno ha cambiado el mismo modelo, deshaz primero el más reciente. Proteus te avisa si lo intentas en otro orden.
- Los deslizadores de cuerpo de un modelo no siempre pueden seguir una edición. Cuando algunos puntos no pudieron moverse con ella, Proteus dice cuántos, y activar ese deslizador puede hacer que la ropa vuelva a atravesarse en algunos sitios.
- La mayoría de las mallas con mods no tienen canal de viento. El primer trazo de viento que se guarda añade uno, y ajusta los materiales de la prenda para que el viento pueda moverla. Los materiales que vienen del juego y no del mod no se pueden ajustar.
- **Tus amigos con un plugin de sincronización ven tus ediciones.** En cuanto un trazo se asienta y se guarda, quienes están emparejados contigo mediante un plugin de sincronización como PlayerSync o Lightless reciben la prenda editada en unos segundos, mientras el Estudio sigue abierto. Los trazos en curso siguen siendo privados. Si has bloqueado piezas, reaparecen unos segundos después de que dejes de trabajar para que pueda compartirse la prenda entera; pasar el ratón o pintar las vuelve a ocultar.

##### Mover, girar y escalar

**Mover**, **Girar** y **Escalar** trabajan sobre una pieza elegida de la prenda en lugar de pintar. Haz clic en una pieza de tu personaje o en la vista del modelo, o elígela en la lista de piezas, y luego arrastra los tiradores que hay en su centro. Mantén **Alt** para mover la cámara mientras trabajas sobre tu personaje. Cada cambio se guarda al soltar, y **Deshacer movimiento** retira el último.

- **Piezas enteras o Polígonos.** Por defecto mueves piezas enteras. Cambia a **Polígonos** para trabajar sobre unos pocos polígonos: haz clic en uno para seleccionarlo, Mayús-clic para añadir o quitar uno, y **Ampliar** o **Reducir** la selección un anillo cada vez. Elegir una pieza en la lista selecciona todos sus polígonos, y **Limpiar** vacía la selección.
- **Mover las piezas contiguas** deja que la tela de alrededor acompañe a lo que mueves, menos cuanto más lejos está, para que la prenda se doble en lugar de rasgarse. **Caída** fija hasta dónde llega. Con polígonos, la caída se mide a lo largo de la superficie, así que solo acompaña la tela unida a tu selección: una pieza aparte que simplemente queda cerca no se mueve. Con **Mover las piezas contiguas** desactivado solo se mueve tu selección, aunque los puntos que comparte exactamente con una vecina se mueven igualmente para que las costuras sigan cerradas.
- **Espejo izquierda/derecha** mueve, gira o escala a la vez, de forma reflejada, la tela correspondiente del otro lado del cuerpo. Una pieza que es su propio reflejo, como un sujetador entero o una gargantilla, se mueve como una sola pieza y se queda centrada, así que se omiten los movimientos y giros laterales. Los puntos sin pareja en el otro lado se mueven igualmente en su lado, y un solo **Deshacer movimiento** deshace ambos lados.
- **La piel y las piezas bloqueadas nunca se mueven.** Desbloquea una pieza bajo un pincel para poder moverla.
- Una pieza movida sigue respondiendo a los huesos para los que se hizo, así que una pieza alejada de ellos puede doblarse de forma extraña en las poses.

##### Talla de cuerpo

**Talla de cuerpo** reajusta una prenda hecha para un cuerpo sobre otro: otra talla del mismo mod de cuerpo (Neolithe XS a L), o un mod de cuerpo distinto (Neolithe a Rue, TBSE a TBSE-X).

**Los dos mods de cuerpo tienen que estar instalados en Penumbra**: aquel para el que se hizo la prenda y aquel sobre el que la reajustas. No hace falta que estén activados. Proteus mide cuánto hay que desplazar cada punto de la prenda comparando los archivos de modelo de ambos cuerpos, así que los necesita los dos en disco. La talla para la que se hizo la prenda también tiene que ser una de las opciones de ese mod.

1. Abre el modelo de la prenda en el Estudio y elige **Talla del cuerpo**. La primera vez aparecen unos consejos; vuelven cuando cambian.
2. En **Hecho para**, elige el mod de cuerpo para el que se hizo la prenda y luego su talla. Proteus adivina la talla por ti y dice cuán seguro está.
3. En **Ajustar a**, elige el mod de cuerpo y una o más tallas. Empiezan en el cuerpo que llevas puesto. Marca varias tallas para hacerlas todas de una vez.
4. Pulsa **Ajustar y previsualizar** para ver el resultado sobre tu personaje. Cuando esté bien, pulsa **Guardar como opción nueva** (o **Guardar *N* tallas como opciones nuevas**). No se conserva nada hasta que guardas.
5. **Guardar en** elige adónde va. **Este mod** lo añade al propio mod de la prenda, por defecto en el grupo que ya contiene las tallas del autor (**Guardar en el grupo** lo cambia), pero la próxima actualización del autor lo quitará. **Un mod nuevo** lo guarda en un mod propio que sobrevive a las actualizaciones. En ambos casos sigue funcionando con Proteus apagado.

Los mods que crea Proteus, para los reajustes y para el equipo vanilla editado, se agrupan en una carpeta **Ampliaciones de Proteus** en la lista de mods de Penumbra.

Conviene saber:

- **Usar la piel del cuerpo nuevo** está activado por defecto: la piel propia de la prenda, para cada parte del cuerpo que se redimensiona, se sustituye por la del cuerpo nuevo, de modo que case con el resto de ti. Desmárcalo para redimensionar en su lugar la piel que trae la prenda, lo que conserva el realce o el skindent que le dio el autor.
- **Quitar la piel oculta**, activado por defecto, omite la piel nueva allí donde el autor borró la suya, normalmente bajo la tela. Desactívalo si un reajuste deja agujeros. Solo se aplica mientras **Usar la piel del cuerpo nuevo** está activado.
- **Añadir los piercings del nuevo cuerpo** desactivado por defecto, trae los piercings del nuevo cuerpo con su piel, donde la prenda muestra esa piel. Las uñas siempre se incluyen. Solo se aplica mientras **Usar la piel del cuerpo nuevo** está activado.
- **Sacar la tela del cuerpo** empuja hacia fuera toda la tela enterrada, no solo la que enterró el reajuste. Úsalo si el cuerpo se transparenta, por ejemplo con unas medias que traen su propia piel de piernas. Desactivado por defecto, porque los autores a menudo esconden tela bajo la piel a propósito. En las prendas superiores propias del juego siempre está activado mientras **Usar la piel del cuerpo nuevo** está activado, como en el reajuste automático.
- **Mantener forma** mueve enteras las piezas rígidas (anillos, hebillas, cadenas) en lugar de doblarlas. Las piezas pequeñas y cerradas empiezan marcadas. Una pieza que se hundiría en el nuevo cuerpo se dobla con él en su lugar.
- **Los zapatos y guantes conservan sus propios pies y manos.** Con **Conservar sus propios pies** (o **manos**) marcado, como viene por defecto, el pie se queda exactamente como lo hizo su autor, tacón incluido, y solo se reajusta la pierna que alcanza el zapato, con la talla de las piernas (en los guantes, el brazo con la del pecho). Los pies van solos. Un zapato que no llega a ninguna pierna conserva su forma y solo se redibuja su piel, guardado como «*Cuerpo* — skin». Desmárcalo para reajustar el zapato entero como antes.
- **Las piezas que cubren más de una parte**, como un vestido largo sobre las piernas, toman una talla para cada parte en **Otras partes del cuerpo**. Elige **Ninguna (dejar fuera esta parte)** para omitir una. Cada una de esas partes también puede ir a su propio **Ajustar al cuerpo**, siempre que la parte propia de la prenda también salga del cuerpo para el que se hizo.
- **Desmarca una pieza** en la lista de piezas para dejarla exactamente donde la puso el autor.
- **El equipo que el juego no trae para tu raza**, como las piezas hechas solo para hombres, también se puede reajustar: Proteus crea primero un modelo para tu raza a partir del que existe. El equipo hecho solo para tu raza, como el conjunto Auri de los Au Ra, se reajusta en la forma midlander para la que están hechos los cuerpos, como lo deforma el juego, y se guarda de nuevo para tu raza.
- **Ajustar para** otra raza del mismo sexo que la prenda. Si el mod de cuerpo al que reajustas tiene un cuerpo para otra raza, como **M Highlander** de TBSE-X, **Ajustar para** lo muestra. Al elegirlo, Proteus crea a partir de este el modelo propio de esa raza, como lo deforma el juego, lo reajusta al cuerpo de esa raza y lo guarda donde esa raza lo carga, juegues con quien juegues. Este modelo queda como está. También funciona al revés: un mod con solo un modelo Highlander se puede reajustar para Midlander, y toda raza de ese sexo sin modelo propio para la prenda llevará también ese ajuste. Si el mod ya tiene su propio modelo para esa raza, Proteus lo avisa y ofrece abrirlo; si reajustas igualmente, el ajuste guardado ocupa su lugar mientras esté activado. El reajuste automático al equipar sigue conservando el modelo del autor para esa raza; se sustituye desde Studio.
- **El equipo vanilla** se reajusta de la misma forma. No tiene un mod en el que guardarse, así que al guardar se crea un mod nuevo con el reajuste y una opción **Original**.
- Retócalo después con los demás pinceles, como **Tirar afuera**, y usa **Mover** para las piezas rígidas. Reajusta desde la talla del autor más cercana a la que quieres; las tallas para otros cuerpos de abajo lo hacen por ti.
- **Entre mods de cuerpo**, la prenda toma además los pesos de hueso del cuerpo nuevo allí donde los dos cuerpos difieren, para que se mueva con la física del cuerpo nuevo. Donde coinciden, se conserva el pesado del autor. Las cadenas de falda y los demás huesos propios de la prenda no se tocan nunca.
- **La piel de los dos cuerpos tiene que compartir una disposición de texturas**, o usar una que Proteus sepa convertir (bibo, gen3 y gen2). Los cuerpos masculinos y femeninos solo se reajustan entre sí con **Ajustar para**, más abajo.
- **El otro sexo** también se puede elegir en **Ajustar para**. Una prenda de torso o piernas Midlander puede reajustarse para Midlander del otro sexo cuando el mod de cuerpo al que reajustas tiene cuerpos de ese sexo. Elige en **Cuerpo original** el mod de cuerpo para el que se hizo la prenda. Los cuerpos se emparejan con los mapas de cuerpo bibo↔TBSE, así que solo los cuerpos de mujer con la distribución de Bibo+ o del juego se emparejan con cuerpos de hombre con la distribución de TBSE o del juego. Un reajuste nunca se rechaza por caras dobladas: cuántas se doblaron se muestra a su lado en el color de aviso, porque una cara doblada se ve como un agujero en el juego, así que revisa esas zonas antes de compartirlo. Toda raza sin modelo propio que recae en el modelo guardado también lo lleva. El reajuste automático nunca cruza de sexo; el conjunto de tallas sí, para un mod de mujer (ver abajo). El equipo del propio juego cambia de sexo solo en las piernas de mujer: pueden reajustarse para hombres Midlander desde el **Cuerpo vanilla**. El juego tiene su propio modelo de hombre para la mayoría de los objetos, así que el panel avisa de que el reajuste guardado lo sustituye mientras su nuevo mod esté activado y ofrece abrir el modelo de hombre del juego en su lugar.
- **Los huesos que le faltan al esqueleto de la raza** no detienen **Ajustar para**. Una prenda montada sobre un esqueleto de físicas de mujer (YAS) puede reajustarse para hombres, sobre IVCS o el esqueleto del juego: cada hueso que el esqueleto de la raza no tiene pasa al más cercano que sí tiene, como los huesos de glúteos de YAS a los de IVCS. El panel enumera cada hueso movido; esas partes se mueven entonces con ese hueso en lugar de por su cuenta. Los huesos que solo tiene el esqueleto personalizado de tu colección se conservan, y el panel avisa de que quien no tenga ese esqueleto verá esas partes descolocadas.
- **Las claves de forma se pierden** cuando se sustituye la piel o la prenda pasa de un mod de cuerpo a otro. Proteus te avisa cuando ocurre.
- **Quitar el último reajuste** saca del mod una talla guardada.
- Solo se reajusta el nivel de detalle más alto, así que la talla antigua puede verse a gran distancia.

##### Todas las tallas para otros cuerpos

Debajo de la herramienta para una sola prenda, **Todas las tallas para otros cuerpos** trabaja sobre un mod entero de una vez. Marca los mods de cuerpo que quieras, pulsa **Añadir las tallas**, y cada talla de esos cuerpos se reajusta desde la talla del autor más cercana a ella (las demás piezas del conjunto en esa opción, desde la misma talla) y se guarda junto a las tallas propias del autor como «Rue S», «YAB Buff L», etc.

- **Usar la versión YAS de un cuerpo cuando la tenga** da a Rue, YAB y Tre sus tallas de Yet Another Skeleton. Una pieza que ya tiene pesos de YAS recibe siempre tallas YAS.
- **Hecho para** es para un mod que Proteus no puede identificar por sí mismo, como uno hecho para una versión antigua de un cuerpo (por ejemplo, Bibo+ antes de Dawntrail): elige el cuerpo para el que se hizo, y las tallas que Proteus no pudo ubicar se leen en ese cuerpo. Las tallas que Proteus identifica por sí mismo se leen igual, diga lo que diga. Déjalo en **Detectarlo** salvo que la lista de lo omitido diga que no se pudo saber para qué cuerpo se hizo una pieza.
- **Incluir zapatos y guantes**, activado por defecto, también da tallas a los zapatos y guantes del mod, conservando sus propios pies y manos: los zapatos reciben las tallas de las piernas («Rue WC», «YAB SC»), los guantes una opción por cuerpo. Un zapato o guante en el mismo grupo de tallas que el pecho o las piernas del conjunto se suma a las tallas que hizo esa pieza, así que una sola elección viste todo el conjunto. Uno que no llega a ninguna pierna ni brazo solo redibuja su piel, y solo donde el cuerpo nuevo dibuja la piel de otra forma. Uno que comparte grupo con una pieza a la que no puede seguir (guantes junto a piernas, o zapatos y guantes sin torso) se deja como está, con una nota.
- **Añadir los piercings de los nuevos cuerpos** desactivado por defecto, añade los piercings de cada cuerpo a las tallas que hace. Las uñas siempre se incluyen. Las tallas ya hechas solo cambian cuando se vuelven a hacer.
- Las tallas que el mod ya tiene no se tocan. Marca **Rehacer las tallas que Proteus añadió antes** para volver a hacer las que hizo Proteus; las tallas propias del autor no se tocan nunca. Si lo usaste con una versión anterior de Proteus, márcalo una vez: aquellas tallas se hicieron desde la primera talla del autor.
- Se ejecuta en segundo plano y puede tardar unos minutos en un mod grande. **Detener** conserva las tallas ya guardadas. Al terminar, enumera lo que tuvo que dejar fuera y por qué.
- **TBSE (para hombres)** y **TBSE-X (para hombres)** se ofrecen para un mod de mujer. Cada pieza de torso y piernas de mujer recibe una versión de hombre, reajustada para hombres Midlander con los mapas de cuerpo bibo↔TBSE (se descargan la primera vez que hacen falta) desde la talla del autor cuyo cuerpo, doblado a forma de hombre, queda más cerca del suyo. Se guardan en un grupo propio, **Cuerpo de hombre**, con Original por defecto, así que las tallas de mujer quedan como están; toda raza de hombre sin modelo propio para la pieza también las lleva. Sus zapatos y guantes de mujer también reciben versión de hombre, reajustados por sus piernas o brazos; uno sin piel de la que leer un cuerpo se lee sobre el cuerpo de **Hecho para**, si no sobre el cuerpo en el que se apoya su tela, y si no sobre el del propio juego; **Revisar en el juego** avisa cuando fue el del juego o cuando su tela queda lejos del cuerpo leído. Una pieza que el autor ya hizo para hombres conserva la suya —un modelo de hombre que solo es el del juego (o una copia suya que TexTools empaqueta para cada raza junto al modelo de mujer del autor) no cuenta— y no se usan tallas hechas sobre cuerpos gen3. Si las opciones del autor son variantes de una talla, como SFW y NSFW, los hombres reciben una versión, y **Revisar en el juego** dice de cuál se hizo; también lista las caras dobladas y los huesos movidos de cada versión. Una versión que empareja menos del 90 % del cuerpo se deja fuera. Las piernas hechas sobre un cuerpo desnudo, cuya entrepierna no tiene sitio en las piernas SFW de TBSE, se reajustan en su lugar a las piernas Gen A de TBSE, y se siguen guardando en TBSE. Estas casillas se recuerdan aparte de TBSE y TBSE-X para mods de hombre.
- Solo se ofrecen cuerpos para las razas que viste el mod. Un cuerpo tiene que estar instalado en Penumbra para recibir sus tallas. El equipo propio del juego no tiene un mod al que añadir tallas, así que esto no aparece para él.

##### Interruptores de piezas

**Alternar piezas** saca una pieza de geometría del modelo de un mod y la pone tras un interruptor: un lazo, un collar, una correa que el autor soldó a una malla siempre visible.

El interruptor se escribe dentro del propio mod como una opción normal de Penumbra, así que aparece en los ajustes de ese mod.

Las piezas del modelo se listan con su número de triángulos. Haz clic en una pieza sobre tu personaje o en la vista del modelo para marcarla. Marca las piezas que debe ocultar un interruptor, dale un nombre y pulsa **Crear un interruptor con las piezas marcadas**. Prepara todos los que quieras y luego pulsa **Escribir los interruptores en el mod**.

Cosas que conviene saber:

- **Diez interruptores por objeto.** Es el límite del juego, no de Proteus. Si un autor ya los ha gastado todos, la pestaña lo indica y no te deja añadir más.
- **Solo equipo y accesorios.** En los demás tipos de modelo no hay nada a lo que enganchar un interruptor.
- **Una pieza que el autor ya hace opcional también admite el tuyo.** Los dos se acumulan: la pieza solo se muestra cuando ambos están activados.
- **Los paquetes importados también admiten interruptores.** Una prenda de un paquete que hayas traído con **Importar** recibe su interruptor igual que las demás.
- **Es reversible.** Se conservan los modelos originales, así que **Deshacer: restaurar los modelos originales** deja el mod exactamente como estaba y elimina el grupo de opciones.
- Si un objeto tiene varios archivos de modelo con las piezas dispuestas de forma distinta, Proteus edita solo aquellos en los que el interruptor encaja correctamente y te dice cuáles dejó en paz, en vez de adivinar y tocar la geometría equivocada.

#### Vínculos

Ata toda tu configuración de Proteus — qué mods están activos, sus prioridades y opciones, y todos sus colores — a un diseño de Glamourer. **Vincular el estado de Proteus a los diseños de Glamourer** está activado por defecto.

Guardar un diseño captura con él el estado actual: todos los mods de tu personaje, no solo los de Proteus, más todos los colores de Proteus. Aplicar ese diseño más tarde lo restaura. Los colores y los ajustes de capa se restauran como una superposición en vivo, así que los archivos del propio mod nunca se reescriben. Cada vínculo enumera los mods que restaura; **Aplicar** restaura uno al momento sin pasar por Glamourer, y **Desvincular** lo olvida sin tocar el diseño.

Mientras un vínculo está activo, los cambios en el editor de colores se previsualizan al instante pero **no** se guardan hasta que pulsas **Actualizar** junto a ese diseño en la pestaña Vínculos, lo que pliega todo lo que hay en pantalla de vuelta a ese diseño.

| Opción | Qué hace |
|--------|-------------|
| Seguir la automatización de Glamourer | Restaura un vínculo cuando la automatización de Glamourer aplica su diseño al cambiar de equipo guardado o de trabajo. Activado por defecto. Solo restaura, nunca borra. |
| Restaurar todos los mods del personaje | Restaura también los demás mods que estaban activos en tu personaje cuando se guardó el diseño, apaga los que no lo estaban, y sube los mods del diseño por encima de todo aquello con lo que entren en conflicto. Desactivado por defecto: solo se restauran los mods de Proteus. Los demás mods se sujetan con ajustes temporales de Penumbra, así que tu colección no se modifica, y el aviso de Penumbra sobre un mod sujeto te deja soltar esa sujeción concreta. |
| Vaciar las ranuras que el diseño no fija | Al aplicar un diseño vinculado, vacía las ranuras de equipo y de gafas que no fija y apaga los paquetes importados que no capturó, para que no queden restos del look anterior. Desactivado por defecto. No se usa en la automatización, y las armas y la ranura que aloja una segunda piel se dejan en paz. |

Un diseño guardado antes de que los vínculos capturaran el personaje entero se marca como **Solo mods de Proteus**. Aplícalo y pulsa **Actualizar** para capturar el resto.

#### Crear

Crea un mod de superposición básico sin salir del juego. Dale un nombre, un autor y elige al menos una textura (difusa, máscara, normales o índice). El material objetivo se rellena solo a partir del cuerpo que llevas puesto; puedes elegir otro material equipado en la lista desplegable o escribir una ruta a mano. Proteus escribe un nuevo mod de Penumbra y lo abre.

Las ranuras de textura que el material elegido no puede usar aparecen atenuadas.

Un objetivo de piel de cuerpo cubre también el mismo cuerpo en todas las demás razas que comparten su disposición de texturas, así que el mod te sigue si cambias de raza. La pestaña muestra cuántos materiales suma eso. Los cuerpos de mujer comparten una disposición y los de hombre otra; los Lalafell y los Hrothgar masculinos tienen la suya propia y no se incluyen.

#### Importar

Toma un paquete de mod y lo convierte en un mod de Proteus. Se admiten tres tipos:

**Mods normales de Penumbra (`.pmp`)** — lleva partes de un mod de equipo corriente sin ocupar una ranura, y consigue además las funciones avanzadas de tabla de colores. Para convertir un mod que ya tengas instalado en Penumbra, elige el `meta.json` de su carpeta en lugar de un archivo de paquete. Los paquetes en el formato antiguo de Penumbra los actualiza primero Penumbra y luego se convierten.

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
| Activado | Interruptor maestro. Al apagarlo, Proteus borra su salida, te redibuja sin ella, desactiva el mod «Proteus» gestionado en Penumbra y detiene los vínculos de diseño. Todas las funciones de Proteus se retiran. |
| Redibujado automático | Deja que Proteus se mantenga al día por su cuenta: recompone tras cambiar de zona, cambiar de equipo y los redibujados, y luego recarga tu personaje para que veas el resultado. Al apagarlo, Proteus pasa a ser casi del todo manual. Tu aspecto se mantiene, pero una edición no se verá hasta que algo te redibuje. |
| Subir prioridad del mod automáticamente | Cuando se confirma que otro mod está sobrescribiendo una textura de piel en la que Proteus compone, sube la prioridad de Proteus en Penumbra por encima de ese mod y lo indica en el chat. |
| Recarga in situ | Refresca las texturas a través de Glamourer en lugar de un redibujado completo, evitando el parpadeo de desaparecer y reaparecer. Activado por defecto. |
| Activar compresión | Comprime por bloques las texturas horneadas, reduciéndolas a cerca de un cuarto de su tamaño en disco y en VRAM. Desactivado por defecto: es lo más lento que hace Proteus, y en un PC antiguo puede retener tu look un minuto o más por cada cambio. Actívalo solo si vas justo de memoria de vídeo. En un PC demasiado lento para ella, Proteus deja las texturas sin comprimir aunque esté marcada, y te dice por qué. |
| Alfa nítido | Experimental. Mantiene funcionando las sphere maps y la metalicidad en pose de grupo, a cambio de bordes más duros en las telas transparentes. |
| Caché de texturas (MB) | Cuántos datos de textura decodificados mantener en memoria entre composiciones. |
| Ocultar mallas de cuerpo redundantes | Omite la piel que la segunda piel dibujaría dos veces: anillos de refuerzo de articulaciones que la parte vecina ya cubre, y copias sobrantes de una región. Activado por defecto, seguro en cualquier cuerpo. |
| Alojar en gafas invisibles | Deja que la segunda piel viaje en la ranura de accesorio facial para que tus anillos sigan libres. |
| Atenuación del tono de piel | Con cuánta fuerza resisten las superposiciones ser teñidas por tu tono de piel. |
| Oclusión ambiental / Suavidad de la sombra / Skindenting | Intensidad global de la sombra de contacto y de la hendidura de normales alrededor de los bordes de las correas. |
| Reaccionar a la luz de la escena | Deja que las filas de color marcadas con **Se desvanece con luz** se atenúen a medida que aumenta la luz sobre ti. Al apagarlo, todos los brillos arden a pleno en todas partes. |
| Fijar el nivel de luz a mano / Nivel de luz | Ignora la escena y usa el deslizador en su lugar. Es la forma más rápida de ver un brillo solo nocturno sin esperar al anochecer, y el ajuste adecuado para la pose de grupo. |
| Resplandor de fondo | El suave resplandor de brasas que flota detrás de la ventana. Activado por defecto. |
| Reducir el movimiento | Mantiene la ventana quieta: los efectos al pasar el ratón se aplican de golpe, y el resplandor de fondo y el pulso de los mods activos dejan de moverse. |

Cuatro botones de aquí merecen mención:

- **Copiar registros** — hace un refresco completo y guarda todo lo que Proteus registra mientras tanto en un archivo de texto en tu escritorio, con tu nombre de usuario de Windows eliminado. Adjunta ese archivo cuando informes de un problema.
- **Restaurar accesorio modificado** — fuerza un redibujado completo si alguna vez una segunda piel se queda atascada en un anillo o una pulsera tras desactivarla o cambiarla.
- **Vaciar caché de texturas** — úsalo cuando una edición de textura no aparece, por ejemplo si has vuelto a exportar una superposición con el mismo tamaño.
- **Texturas de efecto de brillo** — abre la carpeta de la que Proteus lee los mapas de desplazamiento del brillo animado. Deja imágenes ahí y aparecerán en la lista Efecto de cada superposición de equipo. Pasa el ratón por el botón para ver la ruta completa.

##### Pelo

La sección **Pelo** adapta los mods de pelo a ti de dos maneras: lleva el pelo hecho para otra raza a la cabeza de tu raza, y hace que el pelo quepa bajo los sombreros. Igual que el Estudio, ambas editan los archivos del propio mod de pelo, así que siguen funcionando con Proteus apagado y viajan con el mod si lo exportas.

**Pelo hecho para otra raza.** Muchos mods de pelo están hechos para una sola raza, así que en cualquier otra raza el juego muestra su propio peinado en su lugar. Con **Adaptar los mods de pelo hechos para otra raza** activado, Proteus adapta el peinado del mod a la cabeza de tu raza y lo añade a la misma opción del mismo mod, de modo que sigue los propios interruptores de ese mod.

- Revisa los mods de pelo activados en tu colección una vez después de actualizar, al activar el ajuste, al pulsar **Comprobar ahora** y cada vez que se instala, se activa o se cambia un mod de pelo. El peinado que te pones también se adapta. El chat indica los números de los peinados adaptados, para que sepas cuál elegir; Glamourer muestra el número.
- La sección muestra la comprobación mientras avanza, con **Cancelar**, y lista cada peinado que adaptó. Cada uno tiene su propio **Deshacer**, y **Deshacer todo lo que ajustó esta comprobación** los revierte todos (haz clic dos veces). Un peinado deshecho no vuelve a adaptarse por sí solo.
- Solo para tu propia raza, desde razas del mismo sexo. Una raza que solo te estás probando no se adapta: Proteus espera a que la hayas mantenido 30 segundos.
- El pelo con físicas conserva sus físicas. Cuando al esqueleto de tu raza le faltan los huesos con los que se mueve un peinado, la adaptación trae consigo el esqueleto y las físicas de la raza de origen para ese peinado.
- Un peinado que no quedaría bien en tu cabeza (se hunde en ella o se separa de ella) se ajusta igualmente, y Proteus dice cuál, para que lo revises en el juego.
- Un peinado que el doblado deja en parte del revés se ajusta igualmente, y Proteus dice cuál, para que lo revises en el juego.
- Para cubrir todas las razas a la vez, abre un modelo de pelo en el **Estudio** y usa **Portar a todas las razas**.

**Sombreros.** La mayoría del pelo con mods no tiene soporte para sombreros, así que un sombrero puesto encima lo atraviesa. Proteus aplasta contra tu cabeza el pelo que cubriría un sombrero.

- **Adaptar los peinados a los sombreros** está activado por defecto. Proteus espera a que lleves un sombrero de verdad, entonces adapta el peinado debajo y te lo dice en el chat. Si nunca llevas sombrero, tus mods de pelo no se tocan nunca. Desmárcalo para detenerlo, y tu elección se mantiene.
- **Ocultar las coletas** hace además que desaparezcan, mientras llevas un sombrero, las partes que ningún sombrero podría cubrir: coletas, coletas laterales, una caída larga por la espalda. Desactivado por defecto, porque ocultar es todo o nada y una parte mal juzgada sencillamente desaparece.
- O adapta tú mismo el peinado que llevas puesto. La sección dice cuántos puntos se aplastarían y cuánto, y **Adaptar** escribe el cambio.
- **Deshacer** devuelve el peinado que llevas puesto. Un mod de pelo suele traer un modelo por raza, así que **Deshacer los peinados de este mod** devuelve todos los peinados que Proteus cambió en ese mod. Los originales se guardan en `Proteus/hatcompat-backup/` dentro del mod.
- El pelo cuyo autor ya añadió soporte para sombreros se deja en paz, y el pelo que viene con el juego ya funciona. La excepción es un soporte para sombreros que oculta mucho más pelo del que cubre un sombrero, normalmente heredado del peinado a partir del cual se construyó. Proteus se ofrece a medirlo de nuevo y reemplazarlo.
- **Reemplazar el soporte para sombreros que trae un peinado** hace que Proteus descarte el ajuste a sombreros hecho a mano por el autor y use el suyo propio. Desactivado por defecto, ya que un ajuste hecho a mano suele ser mejor. La versión del autor se conserva y vuelve cuando lo desactivas.
- Algunos peinados están soldados en piezas demasiado grandes para que el formato de formas del juego pueda abarcarlas. Esos puntos conservan su forma, así que un sombrero puede seguir atravesándolos ahí.

##### Ajuste al cuerpo

Ajusta por sí solo el equipo de torso, piernas, manos y pies a tu cuerpo, en cuanto te lo pones. Proteus averigua para qué mod de cuerpo y talla se hizo el equipo, lo reajusta a tu cuerpo igual que la herramienta [Talla de cuerpo](#talla-de-cuerpo) del Estudio, guarda la talla nueva en el propio mod del equipo y la activa. El chat avisa cuando empieza y cuando termina.

1. Marca **Ajustar el equipo a mi cuerpo al ponérmelo**.
2. Elige una **Colección**. Cada colección de Penumbra tiene su propia elección, y empieza en la que estás usando ahora.
3. Para cada parte —pecho, piernas, manos y pies— elige un mod de **Cuerpo** y una **Talla**. El primer cuerpo que elijas se pone en todas las partes que tenga, con la talla que llevas en él (o, si no, su primera talla). Las manos y los pies siguen la talla del pecho o de las piernas salvo que elijas una. Cada parte conserva el cuerpo que elijas para ella, pero la ropa no se ajusta entre un cuerpo Rue y otro: solo en el lado de su propia parte. **Olvidar el cuerpo de esta colección** y una elección ponen un mismo cuerpo en todas las partes.
4. El equipo que te pongas a partir de ahora se reajusta. Para lo que ya llevas puesto, pulsa **Ajustar lo que llevo puesto**.

Conviene saber:

- **Ajustar también el equipo original del juego** reajusta además las piezas vanilla. No tienen un mod en el que guardarse, así que cada una recibe un mod nuevo propio, con el nombre del objeto y del cuerpo, dentro de **Ampliaciones de Proteus**.
- **Añadir los piercings de mi cuerpo al equipo ajustado** desactivado por defecto, añade los piercings de tu mod de cuerpo al equipo ajustado. Las uñas siempre se incluyen. El equipo ya ajustado conserva lo que tenía: deshaz y vuelve a ajustar lo que llevas puesto para cambiarlo.
- **Tus elecciones en Penumbra mandan.** Si eliges tú mismo la opción **Original** de un mod u otra talla, Proteus lo deja en paz. Si un mod ya trae tu talla, se activa esa opción en lugar de crear una nueva.
- Si Proteus no sabe para qué cuerpo se hizo una pieza, o la pieza no tiene piel de cuerpo que leer, la deja como está y lo dice en el chat. Aún puedes reajustarla a mano en el Estudio.
- **Deshacer el ajuste de lo que llevo puesto** (mantén Ctrl o Mayús y haz clic) quita las tallas que el ajuste automático creó para el equipo que llevas, borra los mods que creó para el equipo vanilla y devuelve las opciones que tenías antes. Esas piezas no se volverán a reajustar salvo que pulses **Ajustar lo que llevo puesto**. Las tallas que hiciste tú en el Estudio se quedan.
- **Olvidar el cuerpo de esta colección** lo desactiva para esa colección.

### Preajustes

Un **preajuste** es un aspecto con nombre para **un solo mod**: qué opciones tiene marcadas, todos sus colores y su brillo y ajustes de capa. Los paquetes complejos — monos, medias — traen una docena de grupos, y dar con una combinación que merezca la pena significa toquetear casillas hasta que algo encaje. Un preajuste conserva esa combinación.

Los preajustes están en una sección plegable **Preajustes** al final del editor de color de un mod, debajo de Avanzado. **+ Guardar…** conserva con un nombre el aspecto actual del mod; la lista desplegable de al lado elige cuál se lleva puesto. Plegada, la cabecera sigue diciendo cuál llevas.

- Los preajustes marcados con `*` vinieron con el mod. Son de solo lectura: al editar uno obtienes tu propia copia, así que una actualización del mod nunca puede sobrescribir lo que guardaste.
- Un `●` junto al preajuste puesto significa que has cambiado algo desde que se guardó. **Actualizar** incorpora esos cambios; si lo ignoras, el preajuste se queda como estaba.
- **Sin preajuste** devuelve los colores propios del mod. Tus opciones marcadas se quedan como están: quitar un aspecto no es pedir deshacer tus propios cambios.
- Probar preajustes es gratis. Solo las opciones marcadas se escriben en Penumbra; los colores y ajustes de capa van por encima mientras se lleva un preajuste, y los archivos del mod nunca se tocan.

Comparte uno con **Copiar código** (una cadena para pegar en el chat) o **Exportar…** (un archivo `.ptp`). Al otro lado se usa **Pegar código** o **Importar…**; un preajuste hecho para otro mod lo advierte antes de añadirse.

Aplicar un preajuste con un vínculo de diseño activo hace vista previa sobre ese vínculo, como cualquier otra edición: pulsa **Actualizar** en la pestaña Vínculos para conservarlo. Aplicar un diseño de Glamourer quita los preajustes puestos, ya que el diseño lleva sus propios colores; los preajustes en sí siguen guardados.

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
| Superponer en gen2/vanilla | Pinta este mod también sobre la piel vanilla (gen2) que lleves puesta, normalmente la piel que viene con una pieza de equipo. Activado por defecto, y no cuesta nada cuando no llevas piel vanilla encima. bibo y gen3/Eve se pintan siempre. Se aplica a todo el mod y es un ajuste global: los vínculos de diseño no lo capturan. |
| Superponer en hombres | Pinta también el arte femenino de este mod en hombres (TBSE o el cuerpo masculino vanilla), trasladado a su propia distribución mediante los mapas de cuerpo masculino, que se descargan la primera vez que hacen falta; hasta entonces no se pinta nada sobre él. El arte hecho para hombres nunca se modifica. Activado por defecto; se aplica a todo el mod y es un ajuste global: los vínculos de diseño no lo capturan. |
| Restaurar valores predeterminados | Devuelve los colores, el brillo y el modo de esta opción a los ajustes que Proteus registró por primera vez para el mod. Mantén Ctrl para armarlo. |

Si un mod no tiene ninguna opción activa no hay colores que mostrar, pero **Avanzado** sigue apareciendo para que **Superponer en gen2/vanilla** siga estando a mano.

#### Efectos y Geometría

Otras dos secciones plegables se aplican a todo el mod:

- **Efectos** contiene el selector de efecto de brillo animado y **Skindent**, la sombra de contacto y la hendidura de normales en los bordes de las correas. Skindent está apagado salvo que el paquete lo pida, porque trata la cobertura como tela apretada contra la piel, lo cual es falso para un tatuaje. **Paquete** sigue lo que pidió el mod; **Sí** y **No** lo sobrescriben.
- **Geometría** remodela la prenda que Proteus construye a partir de tu cuerpo, para que se lea como tela y no como pintura. Proteus construye una segunda piel copiando tu cuerpo, así que por defecto sigue cada hueco:
  - **Tender sobre el escote**: la tela entre los pechos pasa recta por encima, como hace la tela de verdad. Nada se tira hacia dentro, así que no puede clavarse en el cuerpo.
  - **Tender sobre los glúteos**: lo mismo a lo ancho del asiento.
  - **Tensar la tela sobre el ombligo**: la tela queda plana sobre el ombligo en lugar de hundirse en él.
  - **Suavizar entre las piernas**: suaviza el pliegue de la entrepierna. Esto cambia el cuerpo de debajo, así que se aplica a todas las prendas que lleves a la vez.
  - **Suavizar el pezón**: suaviza la tela por encima. Aún sin terminar: el cuerpo de debajo conserva su forma, así que en una prenda ajustada todavía puede marcarse.

  Activar cualquiera de ellos hace que el mod se represente como segunda piel.

#### Opciones de capa

Bajo las filas, la pestaña de una capa puede mostrar:

- **Esta capa es toda la piel** — para un mod de piel convertido, arte que *es* la piel en lugar de algo puesto encima. Su mapa de normales sustituye al de la piel en vez de sumarse a él, y la atenuación del tono de piel baja a 0 para que asome tu propio tono.
- **Este arte es asimétrico** — actívalo cuando los dos lados deban ser distintos: un tatuaje en un solo brazo, un maquillaje sin espejo. Un cuerpo y una cara vanilla pliegan el arte por la mitad, así que sin esto un lado se refleja sobre el otro. Proteus no puede detectarlo por ti.
- **Puntera reforzada** — en una media con puntera reforzada, teje una puntera más densa: un panel más oscuro por el que los dedos siguen transparentándose, que se difumina en el borde. **Densidad** fija cuánto tapa. Se guarda en el mod, así que los preajustes y los diseños no lo capturan.

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

Los cambios se aplican en pantalla al instante y se recomponen alrededor de un segundo después de que dejes de editar. Se guardan en el `metadata.json` del mod, salvo que haya un vínculo de diseño activo, en cuyo caso pertenecen a ese diseño hasta que pulses **Actualizar** en la pestaña Vínculos.

### Agradecimientos
Muchísimas gracias a Sebby por enseñarme a usar el mapeo de imágenes basado en píxeles en lugar de hornear, y por publicar los mapas horneados bajo licencia MIT a través del loose texture compiler.

---
