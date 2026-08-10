# Twitch Overlay Helper

En Windows-app som läser Twitch-chatten och visar den på tre ställen: som en stor, lugn och klickigenom overlay ovanpå ett spel, som en dyslexianpassad chatt i OBS via en Custom Browser Dock, och som en genomskinlig chattruta på själva sändningen för tittarna. Gränssnittet prioriterar låg visuell trängsel, tydlig avsändare, generöst radavstånd och få beslut per vy.

## Chatt-dock i OBS

Appen kör en lokal webbserver som serverar en chatt byggd för läsbarhet. Adressen klistras in i OBS under **Vy → Dockor → Anpassade webbläsardockor**; den finns färdig att kopiera i appens inställningsfönster.

- Egna reglage för teckenstorlek, radavstånd, **teckenmellanrum och ordmellanrum** – de två sistnämnda hjälper mest vid dyslexi men saknas i Twitchs egen dock.
- Fem lugna teman med gräddvit grund i stället för hård svartvit kontrast.
- **Tempobroms** som håller igen hur snabbt nya meddelanden dyker upp, så de hinner läsas när chatten går varmt.
- **Fastnålade mentions**: meddelanden till dig läggs i en remsa överst så frågor inte scrollar bort.
- **Nåla fast för hand**: klicka på ett namn och nåla raden i remsan. Fungerar utan inloggning – nålen syns bara för dig. Är du inloggad med modbehörighet kan samma rad nålas i chatten så tittarna ser den.
- **Chatten ligger kvar över en omstart**, och en avdelare i spalten låter dig dölja ett tidigare pass med en knapp. Se [Chatten ligger kvar](#chatten-ligger-kvar).
- Länkar kortas till en `🔗 länk`-knapp, VERSALER dämpas och `!kommandon` tonas ner.
- Paus, zebra-rader, namn på egen rad och val av typsnitt.
- **Smeknamn**: klicka på ett namn och ge chattaren ett namn du känner igen. Det visas bredvid Twitch-namnet i både docken och overlayen. Se [Smeknamn på chattare](#smeknamn-på-chattare).
- Moderering med stora knappar: klicka på ett namn för timeout, ban eller att ta bort meddelandet. Ban kräver bekräftelse och åtgärder går att ångra direkt i notisen.
- Raid-väljare som listar de kanaler du följer som är live just nu.
- Skrivfält för att svara i chatten, med **@-förslag** och **emote-väljare**. Se [Skriva i chatten](#skriva-i-chatten).
- Docken innehåller inga inställningar – allt utseende styrs från appen under **Ändra chattens läsbarhet** och slår igenom direkt. Docken visar exempelmeddelanden innan chatten är ansluten, så det går att ställa in läsbarheten i lugn och ro.

Moderering fungerar i alla kanaler där du är moderator, inte bara i din egen. Raid går däremot bara att starta från din egen kanal – Twitch tillåter inget annat – så raid-knappen döljs när du tittar på någon annans chatt.
- Serverns adress innehåller en hemlig nyckel och är bunden till `127.0.0.1`, så den är varken nåbar från nätverket eller användbar för andra sidor på datorn.

Moderering, raid och skrivfältet kräver inloggning; utan den fungerar docken som en ren läsvy.

## Chatt på streamen

Samma chatt en gång till, fast för tittarna: en genomskinlig sida som läggs in som **Browser Source** i OBS. Adressen finns att kopiera bredvid dockens, under fliken **OBS-dock**, och utseendet ställs in under **Ändra utseende på streamchatten**.

Det är avsiktligt inte docken med bakgrunden bortskruvad. De två läses av olika personer på olika avstånd, och därför skiljer sig både vad som visas och hur:

- **Ingenting privat följer med.** Smeknamn, moderering, inloggning, fastnålade meddelanden, uppläsningsknappen och anslutningsstatusen finns inte på sidan – och skickas inte ens dit. Sidan säger vid anslutningen vilken vy den är, och servern håller de bitarna kvar hos docken.
- **Borttaget är borta.** Docken stryker över ett raderat meddelande så du ser att det hände; på sändningen försvinner det direkt, tillsammans med allt som ännu inte hunnit ritas ut.
- `!kommandon` **döljs helt** i stället för att tonas ner, och du kan lista konton som aldrig får synas – chattbottarna är ifyllda från början.
- Egna reglage för storlek, platta bakom texten, mörk kontur runt bokstäverna, antal rader och hur länge de ligger kvar. Sätt en tid om rutan ligger ovanpå spelet, så städas den av sig själv när det är tyst; låt den vara noll om chatten har ett eget hörn.
- Nyaste raden underst som i vanlig chatt, eller överst om rutan sitter i överkanten.
- Egna val för vilka händelsekort tittarna ska se – ett annat beslut än vad du själv vill ha i spalten.

Sidan delar renderare med docken, så en emote, en länkknapp eller en förstorad emote ser likadan ut på båda ställena. Under en raid ritas nya rader några stycken per bildruta i stället för allihop på en gång, och det som ändå aldrig hade hunnit synas kastas – rutan håller ett dussin rader, och en kö tre gånger så djup är redan historia.

## Händelser i chatten

Subs, raids, meddelanden från streamern och annat som händer i chatten visas som egna kort bland meddelandena – i både docken och overlayen. Ett hypetåg är ingen enskild rad utan ett tillstånd som lever i minuter, så det får en remsa överst i docken med nivå, mätare och toppbidrag; overlayen visar i stället två kort, ett när tåget startar och ett när det tar slut.

Det mesta kommer via IRC och fungerar **utan inloggning i vilken kanal som helst**: prenumerationer och gåvor, raids, `/announce`, cheers (som en markör på meddelandet), bits-märken, tittarstreaks och nya chattare. Något Twitch skickar som saknar eget kort visas med Twitchs egen text i stället för att försvinna.

Resten kommer via EventSub och kräver inloggning, rätt behörighet och – för de flesta – **din egen kanal**:

| Funktion | Kräver inloggning | Var det fungerar |
|---|---|---|
| Subs, gåvor, raids, `/announce`, cheers, bits-märken, streaks, nya chattare | Nej | Alla kanaler |
| Inlösta belöningar med namn och kostnad | Ja (`channel:read:redemptions`) | Bara din egen kanal |
| Återbetalning av pet-belöningar | Ja (`channel:manage:redemptions`) | Bara din egen kanal, och bara belöningar appen själv skapat |
| Power-ups: förstorad emote, firande | Ja (`bits:read`) | Bara din egen kanal |
| Hypetåg | Ja (`channel:read:hype_train`) | Bara din egen kanal |
| Shoutouts | Ja (`moderator:read:shoutouts`) | Kanaler där du är moderator |
| Nåla fast för tittarna | Ja (`moderator:manage:chat_messages`) | Kanaler där du är moderator |
| Timeout, ban, ta bort meddelande | Ja | Kanaler där du är moderator |
| Raid | Ja | Bara din egen kanal |
| Skriva i chatten | Ja | Alla kanaler |
| Dina egna emotes i emote-väljaren | Ja (`user:read:emotes`) | Alla kanaler |

Saknas något slutar det alltid med *färre kort* – aldrig med en chatt som slutar läsa. Appen skriver under **5. Logga in för moderering** vilka extra händelser som är påslagna just nu, och skiljer på de två orsakerna till att något inte syns: en behörighet du inte gett, och en kanal som inte är din. En sparad inloggning från innan en behörighet fanns fixas med knappen **Logga in igen** – en förnyad token ger tillbaka gamla behörigheter, aldrig nya.

Power-upen *förstorad emote* ritas i full storlek på en egen rad. Meddelandeeffekter (`animation-id`) kommer över IRC och visas som en markör i alla kanaler, även utloggad – animationen i sig återges inte.

Varje typ går att stänga av för sig: overlayens val ligger under **Händelser i overlayen** i appen, dockens under **Ändra chattens läsbarhet → Händelser i chatten** och streamchattens under **Ändra utseende på streamchatten → Händelser i chatten**. De är separata, eftersom en overlay ovanpå ett spel tål mindre än en dock man läser i lugn och ro – och vad tittarna ska se firas är en tredje fråga. Att stänga av en typ tar bort korten som redan står kvar; att slå på den igen gäller det som händer sedan (docken hämtar tillbaka historiken vid omladdning). Reglagen styr bara korten – en belöning triggar fortfarande pets, och en cheer är fortfarande en markör på meddelandet den kom med.

## Pets som kan betalas tillbaka

En pet som aldrig syntes ska inte kosta tittaren någonting. Appen kan därför skapa pet-belöningen åt dig i Twitch och svara på varje inlösen: **klart** när peten levde ut sin tid, **återbetalning** när den aldrig kom fram.

Det kräver att belöningen är skapad **härifrån**. Twitch låter en app besvara en inlösen bara på belöningar som appens eget Client ID har skapat – en belöning du gjort för hand i Twitchs dashboard går inte att adoptera, och svaret blir 403 hur behörigheterna än ser ut. Appen skapar den därför med kön påslagen (`should_redemptions_skip_request_queue` = false); hoppar en belöning över kön blir den klarmarkerad i samma sekund och kan aldrig återbetalas.

Så här byter du över:

1. Logga in igen efter uppdateringen – `channel:manage:redemptions` är nytt.
2. Döp om eller ta bort den gamla, handgjorda belöningen. Twitch vägrar skapa två med samma namn.
3. Fyll i namn, tid och poäng på raden under **Pets → Belöningar som ger pets** och klicka **⚡**. Raden får ett 🔒 när den är appens egen.
4. Sätt bilden i Twitchs dashboard efteråt – den går inte att ladda upp via API:t.
5. Kryssa i **Shutdown source when not visible** på pet-källan i OBS. Då märker appen när gräsmattan faktiskt är borta i stället för att gissa.

Inlösen markeras **inte** som klar när peten spawnar, utan när den levt färdigt. Det är enda sättet att hålla återbetalningen möjlig hela vägen: att servern lade till en pet och skickade en frame betyder inte att någon såg den. Overlayen kvitterar därför varje pet den faktiskt ritar, och en inlösen betalas tillbaka när

- kvittot uteblir – browser-källan är uppe men ritade aldrig något,
- gräsmattan är full, eller peten knuffas ut i förtid av en annan,
- ingen pet-overlay är igång,
- overlayen försvinner mitt i (en omladdning i OBS hinner tillbaka och kostar ingenting),
- du stänger av pets i appen medan pets lever – hela gräsmattan göms, så ingen ser vad de betalat för.

Återbetalar du själv i Twitchs kö går peten ned här också. Byter du kanal töms gräsmattan – en pet köpt i en annan kanal hör inte hemma på den nya.

Allt som lösts in **medan appen inte lyssnade** betalas tillbaka när den kopplar upp igen: förra körningen, en krasch mitt i sändningen, ett svep i en annan kanal, ett tapp i EventSub mitt i sändningen, eller bara sekunderna innan socketen kom upp. De petsen finns inte längre, så poängen ska tillbaka. Inlösen som EventSub redan levererat rörs inte, och går genomgången inte att slutföra görs den om vid nästa anslutning i stället för att bockas av. Har en kö vuxit förbi 2 000 väntande inlösen tas resten inte här – det står i loggen, och Twitchs egen kö har dem kvar.

Är EventSub nere spawnar en 🔒-belöning ingen pet alls. Chattvägen ser bara belöningens ID, aldrig inlösens eget, så en pet utdelad där hade aldrig kunnat bokföras – tittaren hade fått både peten och poängen tillbaka. Inlösen ligger i stället kvar i Twitchs kö tills appen är tillbaka.

Belöningar du redan har fungerar precis som förut: peten spawnar, poängen är spenderade, ingenting nytt händer, och chattvägen bär dem även utan EventSub. Bara rader med 🔒 kan besvaras – och deras ID är låst, eftersom det är det enda som binder raden till belöningen Twitch låter oss svara på. Peka om en rad genom att ta bort den och skapa en ny.

## Chatten ligger kvar

Chatten sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\chat-history.json` och läggs tillbaka när appen startar, så en omstart mitt i strömmen inte lämnar en tom spalt. Det som sparas är bara det appen själv har sett: Twitch skickar ingen historik när man ansluter, och det finns ingen Helix-endpoint för chatt.

- **Som mest 12 timmar bakåt och 200 rader.** Åldern räknas per rad och inte per kalenderdygn – "dagens chatt" hade tömts vid midnatt, vilket är mitt i kvällen för en ström som började nio, och en omstart 00:05 är precis när historiken behövs mest. Rader äldre än så läggs aldrig tillbaka, så det finns ingen historik veckor eller månader bakåt.
- Historiken hör till en kanal. Byter du kanal slängs den, i stället för att visa fel rums chatt.
- Är **Hämta tidigare meddelanden** påslaget vävs det som sades innan appen anslöt in ovanför de egna raderna, med samma 12-timmarsgräns.

**Dölj tidigare pass.** Har det varit tyst i mer än sex timmar mellan två rader räknas det som ett nytt pass, och docken ritar en avdelare på just det stället: `Nytt pass · 9 timmar tyst`, med knappen **Dölj N rader ovanför**. Trycker du på den försvinner raderna på riktigt ur chattspalten, ur overlayen och ur filen på disk – så de kommer inte tillbaka vid nästa omladdning eller omstart. Ett undantag: rader du **nålat fast för hand** ligger kvar i remsan överst. En nål är ett eget beslut och tas bara ner för hand – att den överlever att spalten scrollar bort är hela poängen med remsan, och att dölja ett gammalt pass ska inte tysta en fråga du sparat att svara på. Sex timmar är gränsen eftersom tystnaden är det enda ärliga svaret som finns: varken Twitch eller appen vet när en ström började, men ingen är fortfarande i samma samtal efter sex timmar. Avdelaren ritas i spalten och inte i knappraden överst, för en knapp som erbjuder sig att dölja "äldre" chatt är värdelös om man inte ser var gränsen dras.

## Skriva i chatten

Skrivfältet längst ner i docken syns när du är inloggad. Två saker gör det snabbare att svara utan att lämna docken – båda öppnar sig **ovanför** fältet, eftersom fältet sitter i underkanten och en lista nedåt inte har någonstans att ta vägen.

Ett skickat meddelande väntar på Twitchs svar innan det visas: kommer bekräftelsen läggs raden in i chatten som vilken annan som helst, och nekar Twitch den – slowmode, följarläge, en timeout, en dubblett – får du Twitchs egen förklaring och texten tillbaka i rutan i stället för en tom ruta och en rad som aldrig kom fram. Egna rader som Twitch inte gav något meddelande-ID får inga knappar för att nåla fast åt tittarna eller ta bort, eftersom de knapparna behöver just det ID:t.

**@-förslag.** Skriv `@` så listas de som nyligen sagt något, senast först. Listan kommer ur chatten som redan rullat förbi, så den kräver varken inloggning eller extra anrop, och den söker på Twitch-namnet, visningsnamnet **och smeknamnet du satt** – tre stammisar vars namn börjar likadant är precis det som gör en @-lista svår, och namnet du känner igen är det som skiljer dem åt. Pilarna flyttar markeringen, Tabb eller Enter väljer, Esc stänger. Det som skrivs in är Twitch-namnet när visningsnamnet bara är samma namn med versaler, annars inloggningsnamnet – det är det Twitch känner igen som en mention för konton vars visningsnamn står i ett annat skriftsystem. Byter docken kanal glöms listan bort direkt: namnen i det förra rummet är inte de som är här nu.

**Emote-väljare.** 🙂-knappen visar vad du faktiskt får skicka i just den här kanalen, i fyra avdelningar: *nyligen använda*, *kanalens emotes*, *dina emotes* och *globala*. Den översta är genvägen till dem du själv använder ofta – den samlas ur **dina egna rader**, inte ur chatten i stort: en emote som rullar förbi tillhör den som skrev den, och överst i din egen väljare vore den mest en inbjudan att skicka något som når chatten som lösa bokstäver. Sökrutan letar bland allihop. Väljaren stannar öppen när du klickar, så tre emotes i rad är tre klick.

En vald emote hamnar i skrivfältet **som bild**, inte som sitt namn – annars säger fältet ingenting om du träffade rätt, vilket är hela poängen med en väljare. Det som skickas är fortfarande bara text; namnet läses tillbaka av bilden när du trycker skicka.

Raden du själv skrev kommer tillbaka från Twitch utan att någon talat om vilka ord som var emotes – Twitch räknar ut det på vägen till tittarna och berättar det för alla **utom avsändaren**. Appen fyller därför i det själv, mot samma kontrollerade lista, innan meddelandet går ut till vyerna. Det sker i appen och inte i docken just för att båda ska visa samma sak: annars vore overlayen ovanpå spelet den enda vy som fortfarande stavade ut `Kappa` med bokstäver.

Genomgående gäller att en emote du inte får skicka aldrig erbjuds – den skulle hamna i chatten som lösa bokstäver i stället för som en bild, och det märks först när meddelandet redan är ute. Det är inte gratis att veta: Twitch har ingen fråga som lyder "får det här kontot skicka den här emoten här", så svaret sätts ihop av tre listor. Kanalens lista innehåller allt kanalen har, prenumerationsnivåer inkluderat, oavsett om du får använda det; din personliga lista är precis vad du får skriva. Kanalens lista visas bara när den går att hålla mot din personliga – eller när det är **din egen kanal**, där du alltid får använda dina egna emotes.

Går den inte att kontrollera visas den inte alls, och väljaren säger rakt ut varför i stället för att gissa. Det gäller två fall: behörigheten `user:read:emotes` saknas (en inloggning från innan den fanns), eller att din egen emote-lista är så lång att den slog i hämtningstaket – en lista som slutade i förtid går att läsa ur men inte att utesluta med. I det första fallet räcker knappen **Logga in igen** i appen; tills dess visas bara de globala emotes, som alla får skicka.

Listan hämtas en gång per kanal och konto, och slängs så fort något av dem ändras.

## Smeknamn på chattare

Twitch-namn är valda för att se ut på ett visst sätt, inte för att kännas igen: `xXx`-utfyllnad, medvetna felstavningar och tre stammisar vars namn börjar likadant. Därför går det att ge en chattare ett eget namn som visas **bredvid** Twitch-namnet – aldrig i stället för det, eftersom hela poängen är att koppla ihop de två.

- Klicka på ett namn i docken → **🏷 Sätt smeknamn**, eller klicka direkt på ett smeknamn som redan står där för att ändra det.
- 🏷-knappen i dockens överkant listar alla smeknamn med sökruta, så ett namn du satte för ett halvår sedan går att hitta utan att vänta på att personen skriver något.
- Smeknamnet syns i **både docken och overlayen**, på alla rader personen redan skrivit – även i historiken efter en omladdning. Overlayen är fortfarande klickigenom och inert; den visar smeknamnen men går inte att sätta dem från.
- Det lämnar aldrig datorn. Ingen tittare ser det, ingenting skickas till Twitch, och det krävs varken inloggning eller modbehörighet – precis som den lokala nålen.
- Namnet knyts i första hand till Twitch-ID:t, så det följer med om personen byter användarnamn. Ett tomt fält (eller **🗑 Ta bort**) tar bort smeknamnet, och notisen efteråt har en **Ångra**-knapp.

Smeknamn sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\nicknames.json` – en egen fil, eftersom det är det enda i appen som är handskrivet och omöjligt att hämta tillbaka från Twitch. Filen skrivs i samma ögonblick som något ändras (inte på en timer), och skrivningen är atomisk: en avbruten sparning kan aldrig lämna en halv fil efter sig. Vid **varje** sparning läggs dessutom en daterad kopia i `%LOCALAPPDATA%\TwitchOverlayHelper\backups\`; de tjugo senaste sparas. Går huvudfilen sönder läses den nyaste kopian som fortfarande går att tolka, och den skrivs tillbaka på plats – i stället för att appen startar med en tom lista.

## Uppläsning av namn

Twitch-namn är skrivna för att titta på, inte för att säga: dekorativa x, dubblerade bokstäver och versala förkortningar. Med uppläsning påslagen får varje namn i docken en `🔊`-knapp. Ett klick skickar namnet till **DeepSeek** (`deepseek-v4-flash`), som svarar med en rad om hur en människa sannolikt skulle säga det, och den raden läses upp av **ElevenLabs** (`eleven_v3`) på datorn där appen körs – inte i webbläsaren, eftersom en dock i OBS ofta är dämpad eller ligger på en annan ljudenhet.

Ställs in under **6. Uppläsning av namn** i appen:

- Två API-nycklar, en per tjänst. De sparas krypterade med Windows DPAPI (`CurrentUser`) i `%LOCALAPPDATA%\TwitchOverlayHelper\speech.bin` och hamnar aldrig i `settings.json`.
- Röst hämtas från ElevenLabs-kontot, eller klistras in som röst-ID. Modellnamnen går att byta om kontot saknar den senaste modellen.
- En testruta som visar den tolkade raden och läser upp den, så hela kedjan kan provas innan knappen släpps in i docken.

Knappen syns bara när nycklar, röst och inställningen är på plats – är något ofyllt finns den inte alls. Båda tjänsterna kostar per anrop, så inget hämtas två gånger i onödan:

- Ljudklippet sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\namecache` och överlever omstart. ElevenLabs-anropet görs alltså en gång per namn och röst.
- Tolkningen från DeepSeek sparas så länge appen är igång. Efter en omstart frågas DeepSeek en gång till för samma namn – ett kort och billigt anrop, och ljudet återanvänds ändå.

Om DeepSeek inte svarar läses namnet upp som det står, med en notis om varför det kan låta fel. Den tolkningen sparas inte, så nästa klick försöker igen.

## Funktioner

- Anslut anonymt genom att bara ange kanalnamn eller Twitch-länk.
- Transparent, alltid-överst och fokusfri chatt-overlay.
- Låst klickigenom-läge och separat redigeringsläge för flytt/storleksändring.
- Globala snabbtangenter (fungerar även när spelet har fokus): visa/dölj overlay (standard `Ctrl+F9`) och redigeringsläge (standard `Ctrl+F10`). Ändra kombinationerna direkt i appen.
- Twitch-emotes renderas som bilder inuti meddelanden, inklusive animerade emotes med automatisk statisk fallback – fungerar anonymt, ingen app-registrering behövs.
- Unicode-emojis renderas i färg med [Twemoji](https://github.com/jdecked/twemoji) (CC-BY 4.0) eftersom WPF annars visar dem monokromt.
- Separata reglage för hela rutans bakgrund och varje meddelandes bakgrund; båda kan dras till 0 % för helt ren overlay.
- Valfri kantlinje runt texten som håller den läsbar utan bakgrund.
- Liveinställningar för textstorlek, radavstånd, typsnitt och antal meddelanden.
- Händelsekort för subs, raids, meddelanden från streamern, belöningar, shoutouts, power-ups och hypetåg – med reglage per typ i varje vy. Se [Händelser i chatten](#händelser-i-chatten).
- **Kantljus**: ett mjukt, klickigenom ljus som tonar in längs skärmens kanter när mods skriver ett kommando (standard `!psst`, bara streamern och mods kan trigga det) eller när en ny chattare skriver sitt första meddelande – varsin färg, styrka och varaktighet, med testknappar i appen. Flera träffar tätt inpå varandra blir aldrig flera samtidiga ljus: ett mod-anrop går alltid fram och kan aldrig knuffas undan av ett välkomnande, välkomnanden är tysta i 15 sekunder efter ett visat ljus, och ett ljus som redan lyser hålls kvar i stället för att blinka om – som mest dubbelt så länge som det är inställt på, så kanterna slocknar alltid till slut även mitt i en raid.
- @-förslag och emote-väljare i dockens skrivfält. Se [Skriva i chatten](#skriva-i-chatten).
- Twitch-metadata för broadcaster, moderator, VIP, subscriber med lokala markörer.
- Riktiga Twitch-badge-bilder när Client ID och OAuth-token anges.
- Automatisk återanslutning, PING/PONG-hantering och tydligt stopp vid nekad inloggning.
- Uppläsning av chattares namn via DeepSeek och ElevenLabs, för namn som är svåra att läsa högt.
- Egna smeknamn på chattare, synliga i både dock och overlay, sparade med säkerhetskopia vid varje ändring.
- Inställningar sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\settings.json`. OAuth-token sparas aldrig.
- Körs som en enda instans; en ny vanlig start öppnar den redan körande appens inställningsfönster.
- **Uppdaterar sig själv** från GitHub-släppen. Se [Uppdateringar](#uppdateringar).
- Valbar **Starta med Windows**-inställning som startar appen minimerad i meddelandefältet utan extra bakgrundstjänst.

## Uppdateringar

Appen håller sig själv uppdaterad. Åtta sekunder efter start – och som mest var tolfte timme – frågar den GitHub om det finns ett nyare släpp, och hittar den ett visar den en fråga. Finns inget nytt säger den ingenting: en dialog mitt i en sändning ska bara dyka upp när den har något att erbjuda. Startar appen minimerad i meddelandefältet väntar kontrollen tills fönstret öppnas.

Säger du ja hämtas zip-filen, kontrolleras mot släppets SHA-256-summa och packas upp av en kopia av appen som kör från `%TEMP%`. Den kopian väntar på att appen stängs ordentligt – inställningar och chatthistorik hinner sparas – byter filerna och startar den nya versionen. Varje fil som skrivs över säkerhetskopieras först, så ett avbrott mitt i installationen lämnar den gamla versionen hel i stället för en halv av varje. Ligger appen i en mapp som kräver administratör frågar Windows om godkännande; ligger den i din egen profil frågar den ingenting.

Vill du kolla själv finns **Sök efter uppdateringar** längst ned i inställningsfönstret och i meddelandefältets meny. En lokal `dotnet run`-byggnad har ingen släppt version och uppdaterar sig aldrig – annars skulle den installera ett släpp ovanpå din arbetskopia.

## Kör

```powershell
dotnet run --project src/TwitchOverlayHelper
```

Skriv kanalnamnet och välj **Anslut**. Använd **Redigera overlay** för att placera den och välj sedan **Lås overlay** så att musklick går rakt igenom till spelet.

## Twitch-inloggning (valfritt)

Anonym anslutning räcker för att läsa chatten, se subs, raids och meddelanden och få rollinformation via IRC-taggar. Inloggning behövs för timeout, ban, raid, för att skriva i chatten, för emote-väljaren, för Twitchs egna badgebilder och för de händelser som går över EventSub – belöningar, shoutouts, power-ups och hypetåg. Tabellen under [Händelser i chatten](#händelser-i-chatten) visar vad som kräver vad, och vad som bara fungerar i din egen kanal.

Inloggningen sker med **Device Code Flow**: registrera en egen app på [dev.twitch.tv](https://dev.twitch.tv/console/apps) med klienttypen **Public**, klistra in dess Client ID i appen och välj **Logga in med Twitch**. Du får en kod att skriva in på `twitch.tv/activate` – ingen client secret och ingen redirect-URI behövs.

Refresh-token sparas krypterad med Windows DPAPI (`CurrentUser`) i `%LOCALAPPDATA%\TwitchOverlayHelper\credentials.bin`, så inloggningen överlever omstart utan att ligga i klartext och utan att kunna läsas av ett annat Windows-konto.

## Teknik

Projektet använder .NET 10, WPF, Twitch IRC över TLS WebSocket, Helix och en inbäddad Kestrel-server för OBS-docken. Overlay-fönstret bygger vidare på samma beprövade Win32-mönster som MicMixer: `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW` och periodisk topmost-återställning.

Docken serveras från inbäddade resurser eftersom appen publiceras som en enda självständig fil.

## Nästa produktsteg

- Uppmärksamhetspuff: mods triggar en dämpad kantglöd på streamerns skärm när något behöver kollas, med `WDA_EXCLUDEFROMCAPTURE` så tittarna inte ser den.
- OpenDyslexic/Atkinson Hyperlegible som paketerade webfonts i docken i stället för att kräva installation i Windows.
- Uppläsning av mentions.
- Val av skärm, fästpunkter och OBS Browser Source-läge.
