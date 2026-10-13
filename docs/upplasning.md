# Uppläsning

Appen kan läsa upp två saker: chattares namn, för dig, och meddelanden som tittare betalat för, i sändningen.

## Uppläsning av namn

Med uppläsning påslagen får varje namn i docken en `🔊`-knapp. Ett klick skickar namnet till DeepSeek (`deepseek-v4-flash`), som svarar med hur en människa sannolikt skulle säga det. Den raden läses upp av ElevenLabs (`eleven_v4`) på datorn där appen körs, inte i webbläsaren, eftersom en dock i OBS ofta är dämpad.

Ställs in under **6. Uppläsning av namn**:

- Två API-nycklar, en per tjänst. De sparas krypterade med Windows DPAPI (`CurrentUser`) i `%LOCALAPPDATA%\TwitchOverlayHelper\speech.bin`, aldrig i `settings.json`.
- Röst hämtas från ElevenLabs-kontot eller klistras in som röst-ID. Modellnamnen går att byta.
- En testruta visar den tolkade raden och läser upp den.

Knappen syns bara när nycklar, röst och inställningen är på plats. Båda tjänsterna kostar per anrop:

- Ljudklippet sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\namecache` och överlever omstart. ElevenLabs anropas en gång per namn och röst.
- Tolkningen från DeepSeek sparas så länge appen är igång. Efter en omstart frågas DeepSeek en gång till, men ljudet återanvänds.

Svarar DeepSeek inte läses namnet upp som det står, med en notis om varför det kan låta fel. Den tolkningen sparas inte, så nästa klick försöker igen.

## Uppläsning av tittarnas meddelanden

Tittare betalar för att få ett meddelande uppläst. Du väljer ett av två sätt i appen:

- **Power-up (bits).** En custom Power-up som du skapar i Twitchs Creator Dashboard. Twitch har inget API för att skapa eller ändra dem. Appen prenumererar på `channel.custom_power_up_redemption.add` (kräver `bits:read` och din egen kanal).
- **Belöning (channel points).** Skapas från appen med `⚡`, med kön påslagen så att inlösen går att svara på.

### Bits går inte att betala tillbaka

Twitch har ingen endpoint för att fullfölja, avbryta eller återbetala en Power-up-inlösen. API:t kan bara läsa dem. Twitch har sagt i [lanseringstråden](https://discuss.dev.twitch.com/t/introducing-api-and-eventsub-support-for-custom-power-ups/64708) att hantering av Power-ups ska utvärderas senare.

Därför heter knappen `🚫 Neka` på bits-spåret och `↩ Refundera` på channel points-spåret. Att neka en Power-up betyder bara att meddelandet inte läses upp. Vill du kunna ge tillbaka måste du använda en belöning skapad av appen.

Inlösen-eventet har ett `status`-fält som appen inte använder. Vad det kan innehålla är inte dokumenterat, så appen vet inte om en refundering för hand i Creator Dashboard är möjlig.

### Godkännande

Med godkännande påslaget hamnar varje inlöst meddelande överst i OBS-docken, med vem som betalade, vad det kostade, texten som kommer läsas upp och knapparna `Godkänn` / `Refundera`. Ett i taget, äldst först, med en räknare för resten. Kön ligger i appen, så den överlever en omstart av OBS.

Kantljuset kan tändas när något väntar, i egen färg. Det rankas mellan mod-anropet och nya chattare.

Svarar ingen inom svarstiden släpps inlösen. Med channel points går poängen då tillbaka.

Stänger du appen eller byter kanal medan något väntar besvaras det inte. Inlösen ligger kvar som UNFULFILLED i Twitchs kö, där du eller dina mods kan ta den för hand. Vid nästa anslutning städas den upp av samma genomgång som städar pet-belöningarna. Genomgången hoppar över det som just då väntar på svar i docken. Når appen inte Twitch när den ska svara försöker den igen, upp till fem gånger.

### Var ljudet hamnar

Standardvalet är en egen Browser Source som bara låter. Appens eget ljud går ut på skrivbordet och hörs av tittarna bara om OBS fångar Desktop Audio. En browserkälla får egen volym, ljudkanal och monitorering i OBS. Kopiera adressen från appen och lägg till den som Browser Source på 1×1 px.

Sidan kvitterar varje klipp när det är slut. Kvittot håller nästa uppläsning tillbaka tills den pågående är klar. På channel points-spåret är det också beviset för att uppläsningen levererades: en inlösen som aldrig hördes betalas tillbaka.

Rösten behöver inte vara samma som läser upp namn. ElevenLabs-nyckeln delas. En teckengräns styr vad en enskild inlösen kan kosta dig: längre meddelanden klipps vid närmaste ordslut, och upprepade tecken kortas (`hejjjjjjjjjj` blir `hejjj`).

### Rutan i bild

Samma browserkälla kan rita en ruta medan uppläsningen pågår: vem som betalade, vad de skrev, vad det kostade och staplar som rör sig när rösten talar. Rutan kommer upp när klippet börjar och går ner när det tystnar.

Rutan är avstängd från början. När du slår på den måste källan göras om från 1×1 px till lika stor som scenen. Resten av sidan är genomskinlig, och rutan placerar sig i något av nio lägen.

Under `Ställ in rutan` finns läge och avstånd från kanterna, största bredd, typsnitt, storlek, färg, platta, rundade hörn, hur den kommer in (glider, tonar, studsar eller ingenting), hur länge den ligger kvar efter sista ordet och vad som står i den. `Visa rutan i OBS` ritar en påhittad uppläsning i fem sekunder. Den kostar inga tecken hos ElevenLabs och rör inte kön.

Med rutan avstängd skickas namn och meddelande aldrig till sidan. Rutan visas heller aldrig medan något väntar på godkännande, bara när uppläsningen spelas.

Är ljudet satt till *den här datorns högtalare* får browserkällan inget klipp, och då ritas ingen ruta. Appen säger det.
