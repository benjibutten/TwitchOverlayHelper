PETS
====

Varje pet är en egen mapp här inne. Alla pets ligger här, även de som följer med
appen – ändra dem precis som du vill, de skrivs aldrig över.

En pet-mapp innehåller:

  pet.json    namn, beskrivning, vad tittarna får skriva
  body.svg    hur peten ser ut (eller spritesheet.webp, se längst ned)

pet.json
--------

  {
    "id": "robo",                     // används i URL:er, bara a-z, 0-9, - och _
    "displayName": "Robo",            // namnet i appen och i listan
    "description": "Den klassiska roboten.",
    "aliases": ["robot"],             // fler ord tittarna kan skriva
    "emoji": ["🤖", "⚙️", "🔋"],       // pratbubblor peten slänger ur sig
    "bodyPath": "body.svg"            // valfritt, body.svg används ändå
  }

Tittaren får peten genom att skriva id:t, namnet eller något av aliasen i sin
inlösen. Standardpetsen har första tjing på sina namn, så en egen pet kan inte
kapa "robo".

body.svg
--------

En vanlig SVG i rutan 100×100, med marken vid y=100. Ritar du i Inkscape eller
Illustrator: spara som vanlig SVG, sätt viewBox till "0 0 100 100".

Klassnamnen är det som får peten att leva – overlayen animerar dem:

  .eye                    blinkar, och blundar när peten sover
  .glow                   pulsar
  .arm-left, .arm-right   svingar när peten går, .arm-right vinkar
  .leg-a, .leg-b          tar steg (en pet utan ben guppar fram i stället)
  .flame                  vajar som en låga
  .glitch                 flimrar

Färgen var(--accent) blir tittarens egen chattfärg, så peten matchar namnet.

Egna id:n i filen (gradienter, mönster) hålls isär mellan pets automatiskt –
två pets kan använda samma namn utan att krocka.

Spritesheet i stället för SVG
-----------------------------

En pet kläckt med Codex hatch-pet kan kopieras hit rakt av: lägg mappen här med
sin pet.json och spritesheet.webp. Version 1 har 9 rader × 8 rutor à 192×208;
version 2 ("spriteVersionNumber": 2 i pet.json) har 11 rader, där de två sista
är sexton blickriktningar som peten använder när den ser sig omkring eller
möter en annan pets blick. Tomma rutor i slutet av en rad hoppas över av sig
självt, så korta animationer spelas i rätt takt. Finns en spritesheet används
den före body.svg. "fps" i pet.json styr takten (1–30).

Efter en ändring
----------------

Klicka "Ladda om pets" i appen – overlayen i OBS uppdaterar sig direkt.

Tar du bort en pet-mapp stannar den borta. Vill du ha tillbaka standardpetsen:
ta bort hela pets-mappen och starta om appen, så skrivs de ut på nytt.
