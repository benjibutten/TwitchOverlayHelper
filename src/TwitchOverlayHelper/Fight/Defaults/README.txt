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
