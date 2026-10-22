# Väntskärm med fajt

En väntskärm för när streamen står still: två karaktärer slåss mot varandra, runda efter runda, utan att du behöver göra något. Rundorna hålls jämna genom att den som ligger under slår lite hårdare, och en stjärna per vunnen runda räknas upp under namnet.

Allt ställs in under fliken **Fajt**. Lägg adressen som en Browser Source på 1920 × 1080 i en egen scen. Ändringar i fliken syns direkt i OBS, så adressen behöver aldrig bytas.

## Texten längst ned

Två fält, en rubrik och en rad under. Förslagen i listan (Strax tillbaka, AFK, medvetslös, tekniskt strul) fyller bara i fälten, som sedan går att skriva om. Tomma fält visar ingen text. **💾 Spara text** lägger det du skrivit överst i listan, markerat med ★. **Ta bort sparad** tar bort den valda.

## Karaktärsval

Varje match börjar med att chatten röstar om vilka som ska slåss. Alla karaktärer visas i ett rutnät.

- `!p1 my` röstar på My till spelare 1.
- `!p2 zelda2` röstar på Zelda i klädsel 2 till spelare 2. En siffra direkt efter namnet väljer klädsel.
- Namn med mellanslag skrivs som de är: `!p1 dj jenni`.

En tittare har en röst per hörn, och en ny röst flyttar den gamla. När tiden är ute får varje hörn den karaktär och klädsel som fått flest röster. Ett hörn ingen röstat på får en slumpad karaktär och klädsel – den visas i hörnet medan rösten pågår, så samma par går inte om och om igen när chatten är tyst.

Matchen spelas först till 3 vunna ronder (eller 1, 2, 4, 5, 7 eller utan slut). Sedan koras vinnaren och det är dags att välja igen. Rutnätet bläddrar mellan sidor när karaktärerna inte får plats i läsbar storlek. Rösttid, matchlängd och röstorden ställs in i appen, och karaktärsvalet kan stängas av.

## Hörn och arena

Du väljer vem som står i hörnet för spelare 1 (vänster) och spelare 2 (höger) innan chatten har valt, i vilken klädsel, och var de slåss:

- *Transparent – ingen bakgrund* visar bara fajten, med streamen bakom.
- *Svävande ringen* är en ring mitt i bild med strålkastare och publik. Resten är genomskinligt.
- *Garaget* täcker hela bilden.

## Chattens kommandon

- `!heja 1` / `!heja 2` fyller hörnets supermätare. Full mätare gör nästa spark till en super som alltid träffar och tar ungefär en tredjedel av livet.
- `!hela 1` / `!hela 2` ger hälsa tillbaka.

Siffran är hörnet, inte karaktären. Det går också att skriva `p1`/`p2` eller karaktärens namn. P1/P2 syns vid livsmätarna, och tittarens namn visas på skärmen i chattfärgen.

Hur mycket ett kommando ger beror på hur många som hjälper hörnet. Är du ensam räknas du två och en halv gånger: två hejarop laddar supern, en helning ger 25 HP. Ju fler som hjälper samma hörn, desto mindre betyder var och en. Tjugo bakom ett hörn gör fortfarande mer sammanlagt än två, men inte tio gånger mer. Bara de som skrivit nyligen räknas. Hur mycket den ensamme räknas (1–5 gånger) ställs in under **Ensam räknas**.

På var sida av ringen finns en lista över chattarna som hjälper hörnet, med vad de gjort och en nedräkning tills varje kommando fungerar för dem igen. Listan rullar när den blir lång och kan stängas av. Kommandoorden och väntetiden per tittare går att ändra. Kommandon som skrivs när ingen väntskärm är öppen ignoreras.

## Egna karaktärer och arenor

Karaktärer och arenor är mappar på disken:

- `%LOCALAPPDATA%\TwitchOverlayHelper\fight\fighters\<id>` med `fighter.json` och `sprite.webp`
- `%LOCALAPPDATA%\TwitchOverlayHelper\fight\arenas\<id>` med `arena.json` och en bild

Det som följer med appen (Silver, Ink, Garaget och Svävande ringen) skrivs dit vid första start och går sedan att ändra. Lägg till en mapp och klicka **Ladda om**. `LÄS MIG.txt` i mappen beskriver formatet.

**Klädslar.** `sprite.webp` är klädsel 1, och `sprite2.webp`, `sprite3.webp` och så vidare i samma mapp blir klädsel 2, 3 och uppåt. Klädslarna kan få namn i `fighter.json`.

**Figurremsan** har åtta rutor på 640 × 560 px i den här ordningen: stå, stå (andning), gå, slag, spark, träffad, knockad, seger. Alla är vända åt höger med fötterna på samma linje.

**Specialattack.** En karaktär kan ha en egen special som blir dess super. När mätaren är full gör den specialen i stället för supersparken, och en gång per rond kan den ta till den som sista utväg. Det finns fyra sorter:

- *throw* kastar något i en båge som slår upp i eld (Zeldas molotov)
- *swing* är ett enda jätteslag (My:s lollipopklubba)
- *saw* är en serie snabba träffar (DJ Jennis motorsåg)
- *confuse* är en besvärjelse som skadar och sedan får motståndaren att irra omkring med frågetecken runt huvudet

Specialen beskrivs i `fighter.json` och har egna poser i `special.webp` med tre rutor: stans, laddning, slag. Ett kast behöver också en bild på det som kastas, `prop.webp`. En klädsel kan ha en egen special (`special2.webp` för klädsel 2), annars används karaktärens.

**Arenor.** En arena är vilken 16:9-bild som helst. `floor`, `left` och `right` i `arena.json` säger var fötterna står och hur långt karaktärerna får gå. Genomskinliga delar släpper igenom streamen.

Nya karaktärer, klädslar och arenor tas fram med Codex bildgenerering och skripten i `tools/fight-assets`, som gör om en bild på grön bakgrund till rätt format. Skillen `fight-assets` för Claude Code går igenom hela vägen från referensbild till färdig mapp.
