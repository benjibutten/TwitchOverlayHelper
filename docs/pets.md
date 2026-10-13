# Pets och lyckosnurren

## Pets som kan betalas tillbaka

En pet som aldrig syntes ska inte kosta tittaren något. Appen kan skapa pet-belöningen åt dig i Twitch och svara på varje inlösen: klart när peten levt ut sin tid, återbetalning när den aldrig kom fram.

Det kräver att belöningen är skapad från appen. Twitch låter en app svara på inlösen bara för belöningar som appens eget Client ID har skapat. En belöning gjord för hand i Twitchs dashboard går inte att ta över, och svaret blir 403 oavsett behörigheter. Appen skapar belöningen med kön påslagen (`should_redemptions_skip_request_queue` = false), eftersom en belöning som hoppar över kön klarmarkeras direkt och aldrig kan återbetalas.

### Kom igång

1. Logga in med behörigheten `channel:manage:redemptions`. Har du en äldre inloggning: **Logga in igen**.
2. Döp om eller ta bort en eventuell handgjord belöning med samma namn. Twitch vägrar skapa två med samma namn.
3. Fyll i namn, tid och poäng på raden under **Pets → Belöningar som ger pets** och klicka **⚡**. Raden får ett 🔒 när den är appens egen.
4. Sätt bilden i Twitchs dashboard. Den går inte att ladda upp via API:t.
5. Kryssa i **Shutdown source when not visible** på pet-källan i OBS, så märker appen när gräsmattan inte längre visas.

Belöningar utan 🔒 fungerar utan svar: peten spawnar och poängen är spenderade. De fungerar även utan EventSub. Bara rader med 🔒 kan besvaras, och deras ID är låst. Peka om en rad genom att ta bort den och skapa en ny.

### När poängen betalas tillbaka

En inlösen markeras som klar först när peten levt färdigt, inte när den spawnar. Overlayen kvitterar varje pet den ritar, och inlösen betalas tillbaka när

- kvittot uteblir, alltså browser-källan är uppe men ritade aldrig något,
- gräsmattan är full, eller peten knuffas ut i förtid av en annan,
- ingen pet-overlay är igång,
- overlayen försvinner mitt i (en omladdning i OBS hinner tillbaka och kostar ingenting),
- du stänger av pets i appen medan pets lever.

Återbetalar du själv i Twitchs kö tas peten bort här också. Byter du kanal töms gräsmattan.

### Det som löstes in medan appen inte lyssnade

Allt som lösts in medan appen inte lyssnade betalas tillbaka när den kopplar upp igen: förra körningen, en krasch, ett tapp i EventSub eller sekunderna innan anslutningen kom upp. Inlösen som EventSub redan levererat rörs inte. Går genomgången inte att slutföra görs den om vid nästa anslutning. Har kön vuxit förbi 2 000 väntande inlösen tas resten inte. Det står i loggen, och Twitchs egen kö har dem kvar.

Är EventSub nere spawnar en 🔒-belöning ingen pet. Chattvägen ser bara belöningens ID, inte inlösens, så en pet utdelad den vägen hade inte gått att bokföra. Inlösen ligger kvar i Twitchs kö tills appen är tillbaka.

## Lyckosnurren

En egen belöning som snurrar fram en pet tittaren får behålla. Snurren glider in i pet-overlayen från den kant du väljer, loopar igenom allt som går att vinna och stannar på vinsten, som sedan hoppar ut på gräsmattan. Ingen ny källa i OBS behövs.

Vinsten knyts till Twitch-ID:t, inte namnet, så samlingen följer med vid namnbyte. Allt sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\spinwins.json`.

### Vad som går att vinna

Väljs under **Pets → Lyckosnurren**. Varje pet har en kryssruta och en sällsynthet: `vanlig`, `ovanlig`, `sällsynt` eller `legendarisk`. En vanlig är ungefär trettio gånger så trolig som en legendarisk. Valen skrivs i petens egen `pet.json` (`winOnly` och `rarity`), så de följer med mappen till en annan dator. Fält appen inte känner till lämnas orörda.

En pet markerad som vinstbar försvinner ur den vanliga pet-belöningen för alla utom den som vunnit den. Ber någon annan om den med namn betalas inlösen tillbaka och boten säger varför. Kan belöningen inte återbetalas får de en vanlig pet i stället. Du och dina moderatorer kommer åt alla arter med `!pet namn`.

### Sällsynthet på gräsmattan

- Ovanlig: svag grön aura.
- Sällsynt: lila aura med ett ljus som sveper över kroppen då och då.
- Legendarisk: guld med gnistor och en varmare fläck mark under sig.

Vanliga pets har ingen effekt. Samma färger lyser upp snurrens fönster när den stannar. Auran tonar bort efter tio sekunder. Tiden ändras under **Pets → Inställningar**, och `0` låter dem lysa hela tiden. **Visa sällsynthet med skimmer** stänger av alltihop.

### Dubbletter blir gåvor

Landar snurren på något vinnaren redan äger får de skänka bort den. Boten ber om ett namn, och `!ge kajsa` knyter vinsten till det kontot.

- Poängen kommer inte tillbaka för en dubblett. Inlösen bokförs som levererad när den dras.
- Chansen att ge bort tar slut när tiden du satt går ut, eller när mottagaren visar sig äga peten redan.
- Ett namn som inte finns på Twitch räknas inte som ett försök.
- Snurrar någon igen medan deras förra gåva väntar på mottagare ersätts den av den nya.

Kan boten inte skriva (avstängd, utloggad, chatten nere eller raden avslagen under **Vad boten säger**) hoppar dragningen över det vinnaren redan har. Då får bara den som samlat allt sina poäng tillbaka.

### Om något går fel

Vinnaren dras och sparas innan animationen börjar. Går overlayen ner mitt i snurren gäller vinsten ändå.

Inlösen betalas tillbaka när snurren aldrig blev av: lyckosnurren avstängd, pets avstängda, ingen pet-overlay igång, ingenting markerat som vinstbart, eller hela samlingen redan vunnen.

En vinst som bokfördes men inte hann kvitteras hos Twitch innan appen stängdes sparas på disk och kvitteras vid nästa anslutning.

### Kommandon

`!mina` visar samlingen och `!ge` skänker bort en dubblett. Kommandona och botens texter för snurren ändras under **Vad boten säger**. Se [Chatbot](chatbot.md).

## Testvy för en pet

🔍 längst till höger på varje rad öppnar peten ensam i webbläsaren, i valfri storlek mot rutmönster, svart, vitt, greenscreen eller en egen färg. Där kan du

- spela varje beteende overlayen kan sätta peten i, och stega ruta för ruta,
- låsa en enskild rad i spritesheeten och vrida blicken runt hela varvet,
- se peten i varje sällsynthet,
- se hela arket med rutnät, där tomma rutor är streckade och ett klick fryser peten på rutan,
- läsa vad appen får ut ur filen: bildstorlek, cellstorlek, antal rader och ifyllda blickriktningar, med varningar för tomma rader, luckor i en animation och ett `spriteVersion` som inte stämmer med arket.

Vyn ritas med overlayens egen stilmall och ändrar ingenting i overlayen. Den kräver att appens lokala server är igång. Har du ändrat i `pet.json` klickar du **Ladda om pets** i appen först. Har du ritat om bilden räcker **↻ Ladda om** i vyn.
