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
Esta es una traducción; el original en inglés es el texto de referencia: [English](../AI-DECLARATION.md)

Este formato se basa en
[AI-DECLARATION.md](https://ai-declaration.md/en/0.1.2). Léelo para
entender los niveles indicados arriba.

## Notas

Ante todo, quiero dejar claro que la IA no tiene cabida en el arte, es incapaz de
hacer arte, y tratar de usarla para desplazar a los artistas no entiende por qué
hacemos arte en primer lugar. Mi opinión favorita al respecto se puede ver aquí:
https://www.youtube.com/live/RXyWtp8tuYY?si=GUcD40pQut0sIiLJ

En segundo lugar, Proteus no usa IA en tiempo de ejecución cuando lo usas. Los
algoritmos son similares o idénticos a los que podrías encontrar en Blender, pero
en algunos casos aprovechan circunstancias propias de FF, como las UV compatibles,
para cumplir sus tareas de maneras que un programa de modelado 3D no puede. Pero,
al fin y al cabo, es solo un programa informático clásico que ejecuta algoritmos
estáticos que se pueden repetir de forma consistente, ajustar y analizar.

No hago «vibe coding». En el momento de escribir esto, tengo 26 años de
experiencia profesional en desarrollo, principalmente en c#, el lenguaje que se usa
en este plugin. Me despidieron porque al principio me negué a usar la IA de
cualquier forma. La realidad, quizá desafortunada, es que el 100 % del software que
has usado en los últimos dos años ha incorporado asistencia de IA de alguna forma.
Este documento pretende ofrecer transparencia sobre el nivel exacto de
participación en cada proceso. Conozco mi oficio y confío en el código. Eres
libre de no usarlo si te niegas a usar código generado por IA, pero, para ser
coherente, también tendrías que dejar de usar internet o cualquier software
independiente que se haya actualizado después de 2024.

## Arquitectura

Ninguna participación. El 100 % de los diseños y algoritmos los hice yo.

## Diseño

Ninguna participación. A medida que evolucionó, se añadió una pequeña interfaz
para mostrar los colorsets, que ya sé que parece diseñada por un desarrollador de
backend, porque lo fue.

## Implementación

Assist. Si se puede usar un ordenador para escribir código repetitivo, generar
pruebas rutinarias, documentación y scripts de despliegue estándar de una forma que
me deje volver a crear arte, está sirviendo al bien común.

## Pruebas

Pair. El código de pruebas que no se distribuye lo implementó Claude.

## Documentación

Assist. Se generó un primer borrador de las guías de usuario con un prompt
detallado y un análisis del código, y después se editó a mano. Esto me permite
ofrecer la documentación en varios idiomas.

## Revisión

Hint. De hecho, esta es el área en la que la IA aporta más valor al usuario final.
Sobre todo al adentrarme en este nuevo terreno, se usó para revisar el código en
varias etapas y contribuyó en gran medida a la calidad del producto final,
detectando problemas en la limitada interacción con otros plugins, así como en las
complejas funciones de manipulación de imágenes. Todos se consideraron con
cuidado y, en general, se corrigieron a mano, salvo que la corrección fuera
trivial.

## Despliegue

Assist. Usar github, y git en general, es algo nuevo para mí. Los scripts de despliegue
fueron una mezcla de cosas tomadas de otros proyectos (teniendo en cuenta la
licencia) y adaptadas por Claude para este proyecto.
