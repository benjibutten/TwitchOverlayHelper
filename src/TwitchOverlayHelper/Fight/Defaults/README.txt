FAJTEN
======

Här ligger karaktärerna och arenorna till väntskärmen med fajt. Allt här är
ditt – även det som följer med appen. Ändra, byt namn eller ta bort, det skrivs
aldrig över.

  fighters\<id>\fighter.json + sprite.webp     en karaktär
  arenas\<id>\arena.json   + arena.webp        en arena

Klicka "Ladda om" under fliken Fajt efter en ändring. Väntskärmen i OBS
uppdaterar sig direkt.

En karaktär
-----------

  {
    "id": "silver",              // bara a-z, 0-9, - och _
    "displayName": "Silver",     // namnet vid livsmätaren
    "description": "…",
    "spritePath": "sprite.webp", // valfritt, sprite.webp används ändå
    "scale": 1.0                 // valfritt, 0.3–3, om figuren blev för stor/liten
  }

sprite.webp är en remsa med 8 rutor bredvid varandra, 640 × 560 px per ruta
(5120 × 560 totalt), genomskinlig bakgrund. Rutorna i ordning:

  1 stå   2 stå (andas)   3 gå   4 slag   5 spark   6 träffad   7 knockad   8 seger

Alla rutor vänder sig åt höger, med fötterna på samma linje 10 px ovanför
rutans underkant och överkroppen mitt i rutan. Den som står till höger ritas
spegelvänd av sig själv.

Klädslar
--------

En karaktär kan ha flera klädslar. Lägg dem i samma mapp:

  sprite.webp     klädsel 1, den vanliga
  sprite2.webp    klädsel 2
  sprite3.webp    klädsel 3 … upp till 9

Chatten väljer klädsel med en siffra direkt efter namnet: !p1 my2. Vill du ge
klädslarna namn, eller låta en kod peka på en fil som heter något annat:

  "outfits": [
    { "code": 2, "name": "Läderjacka" },
    { "code": 12, "name": "Jul", "spritePath": "jul.webp" }
  ]

Varje klädsel är en hel remsa i samma format som sprite.webp.

Specialattack
-------------

En karaktär kan ha en egen super. När chatten fyllt mätaren gör den sin special
i stället för den vanliga supersparken, och en gång per rond kan den ta till
den som sista utväg.

  "special": {
    "name": "Molotov",           // står på skärmen när den används
    "style": "throw",            // throw (kastar), swing (ett jätteslag), saw (många snabba)
    "spritePath": "special.webp",// valfritt, special.webp används ändå
    "propPath": "prop.webp",     // det som kastas – bara för throw
    "color": "#4dff6a"           // färgen på blixten, gnistorna och elden
  }

special.webp har tre rutor bredvid varandra: vanlig stans, laddning, slag. Rutorna
får vara större än i sprite.webp (till exempel 1024 × 760) så att vapnet får
plats – figuren ska vara lika stor och stå på samma linje 10 px ovanför
rutans underkant. Saknas special.webp används den vanliga sparken som pose.

En klädsel kan ha en egen special, ritad i sina egna kläder. Lägg den i
klädselns rad i "outfits". Bilderna heter då special2.webp och prop2.webp
(med klädselns kod) om inget annat anges:

  "outfits": [
    { "code": 2, "name": "Rutig", "special": { "name": "Bitchslap", "style": "swing", "color": "#ff4fc3" } }
  ]

En klädsel utan egen special använder karaktärens.

En arena
--------

  {
    "id": "sky",
    "displayName": "Svävande ringen",
    "imagePath": "arena.webp",   // valfritt, arena.webp används ändå
    "floor": 0.67,               // var fötterna står, andel av bildens höjd uppifrån
    "left": 0.2,                 // hur långt åt vänster en karaktär får gå (andel av bredden)
    "right": 0.8                 // och åt höger
  }

Bilden skalas så att den täcker hela 1920 × 1080. Genomskinliga delar av bilden
släpper igenom det som ligger under i OBS – en arena kan alltså vara en ring
som svävar över spelet. Vill du inte ha någon bakgrund alls väljer du
"Transparent – ingen bakgrund" i appen.

Göra nya
--------

Repot har ett verktyg för att ta fram nya karaktärer, outfits och arenor med
Codex bildgenerering: tools\fight-assets (och skillen fight-assets för Claude
Code). Det tar en bild på grön bakgrund och gör om den till rätt format.

Vill du ha tillbaka det som följde med appen: ta bort hela fight-mappen och
starta om appen.
