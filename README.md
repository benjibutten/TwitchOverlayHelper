# Twitch Overlay Helper

En Windows-app som läser Twitch-chatten och visar den på två ställen: som en stor, lugn och klickigenom overlay ovanpå ett spel, och som en dyslexianpassad chatt i OBS via en Custom Browser Dock. Gränssnittet prioriterar låg visuell trängsel, tydlig avsändare, generöst radavstånd och få beslut per vy.

## Chatt-dock i OBS

Appen kör en lokal webbserver som serverar en chatt byggd för läsbarhet. Adressen klistras in i OBS under **Vy → Dockor → Anpassade webbläsardockor**; den finns färdig att kopiera i appens inställningsfönster.

- Egna reglage för teckenstorlek, radavstånd, **teckenmellanrum och ordmellanrum** – de två sistnämnda hjälper mest vid dyslexi men saknas i Twitchs egen dock.
- Fem lugna teman med gräddvit grund i stället för hård svartvit kontrast.
- **Tempobroms** som håller igen hur snabbt nya meddelanden dyker upp, så de hinner läsas när chatten går varmt.
- **Fastnålade mentions**: meddelanden till dig läggs i en remsa överst så frågor inte scrollar bort.
- **Nåla fast för hand**: klicka på ett namn och nåla raden i remsan. Fungerar utan inloggning – nålen syns bara för dig. Är du inloggad med modbehörighet kan samma rad nålas i chatten så tittarna ser den.
- Länkar kortas till en `🔗 länk`-knapp, VERSALER dämpas och `!kommandon` tonas ner.
- Paus, zebra-rader, namn på egen rad och val av typsnitt.
- Moderering med stora knappar: klicka på ett namn för timeout, ban eller att ta bort meddelandet. Ban kräver bekräftelse och åtgärder går att ångra direkt i notisen.
- Raid-väljare som listar de kanaler du följer som är live just nu.
- Skrivfält för att svara i chatten.
- Docken innehåller inga inställningar – allt utseende styrs från appen under **Ändra chattens läsbarhet** och slår igenom direkt. Docken visar exempelmeddelanden innan chatten är ansluten, så det går att ställa in läsbarheten i lugn och ro.

Moderering fungerar i alla kanaler där du är moderator, inte bara i din egen. Raid går däremot bara att starta från din egen kanal – Twitch tillåter inget annat – så raid-knappen döljs när du tittar på någon annans chatt.
- Serverns adress innehåller en hemlig nyckel och är bunden till `127.0.0.1`, så den är varken nåbar från nätverket eller användbar för andra sidor på datorn.

Moderering, raid och skrivfältet kräver inloggning; utan den fungerar docken som en ren läsvy.

## Händelser i chatten

Subs, raids, meddelanden från streamern och annat som händer i chatten visas som egna kort bland meddelandena – i både docken och overlayen. Ett hypetåg är ingen enskild rad utan ett tillstånd som lever i minuter, så det får en remsa överst i docken med nivå, mätare och toppbidrag; overlayen visar i stället två kort, ett när tåget startar och ett när det tar slut.

Det mesta kommer via IRC och fungerar **utan inloggning i vilken kanal som helst**: prenumerationer och gåvor, raids, `/announce`, cheers (som en markör på meddelandet), bits-märken, tittarstreaks och nya chattare. Något Twitch skickar som saknar eget kort visas med Twitchs egen text i stället för att försvinna.

Resten kommer via EventSub och kräver inloggning, rätt behörighet och – för de flesta – **din egen kanal**:

| Funktion | Kräver inloggning | Var det fungerar |
|---|---|---|
| Subs, gåvor, raids, `/announce`, cheers, bits-märken, streaks, nya chattare | Nej | Alla kanaler |
| Inlösta belöningar med namn och kostnad | Ja (`channel:read:redemptions`) | Bara din egen kanal |
| Power-ups: förstorad emote, firande | Ja (`bits:read`) | Bara din egen kanal |
| Hypetåg | Ja (`channel:read:hype_train`) | Bara din egen kanal |
| Shoutouts | Ja (`moderator:read:shoutouts`) | Kanaler där du är moderator |
| Nåla fast för tittarna | Ja (`moderator:manage:chat_messages`) | Kanaler där du är moderator |
| Timeout, ban, ta bort meddelande | Ja | Kanaler där du är moderator |
| Raid | Ja | Bara din egen kanal |
| Skriva i chatten | Ja | Alla kanaler |

Saknas något slutar det alltid med *färre kort* – aldrig med en chatt som slutar läsa. Appen skriver under **5. Logga in för moderering** vilka extra händelser som är påslagna just nu, och skiljer på de två orsakerna till att något inte syns: en behörighet du inte gett, och en kanal som inte är din. En sparad inloggning från innan en behörighet fanns fixas med knappen **Logga in igen** – en förnyad token ger tillbaka gamla behörigheter, aldrig nya.

Power-upen *förstorad emote* ritas i full storlek på en egen rad. Meddelandeeffekter (`animation-id`) kommer över IRC och visas som en markör i alla kanaler, även utloggad – animationen i sig återges inte.

Varje typ går att stänga av för sig: overlayens val ligger under **Händelser i overlayen** i appen, dockens under **Ändra chattens läsbarhet → Händelser i chatten**. De är separata, eftersom en overlay ovanpå ett spel tål mindre än en dock man läser i lugn och ro. Att stänga av en typ tar bort korten som redan står kvar; att slå på den igen gäller det som händer sedan (docken hämtar tillbaka historiken vid omladdning). Reglagen styr bara korten – en belöning triggar fortfarande pets, och en cheer är fortfarande en markör på meddelandet den kom med.

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
- **Kantljus**: ett mjukt, klickigenom ljus som tonar in längs skärmens kanter när mods skriver ett kommando (standard `!psst`, bara streamern och mods kan trigga det) eller när en ny chattare skriver sitt första meddelande – varsin färg, styrka och varaktighet, med testknappar i appen.
- Twitch-metadata för broadcaster, moderator, VIP, subscriber med lokala markörer.
- Riktiga Twitch-badge-bilder när Client ID och OAuth-token anges.
- Automatisk återanslutning, PING/PONG-hantering och tydligt stopp vid nekad inloggning.
- Uppläsning av chattares namn via DeepSeek och ElevenLabs, för namn som är svåra att läsa högt.
- Inställningar sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\settings.json`. OAuth-token sparas aldrig.
- Körs som en enda instans; en ny vanlig start öppnar den redan körande appens inställningsfönster.
- Valbar **Starta med Windows**-inställning som startar appen minimerad i meddelandefältet utan extra bakgrundstjänst.

## Kör

```powershell
dotnet run --project src/TwitchOverlayHelper
```

Skriv kanalnamnet och välj **Anslut**. Använd **Redigera overlay** för att placera den och välj sedan **Lås overlay** så att musklick går rakt igenom till spelet.

## Twitch-inloggning (valfritt)

Anonym anslutning räcker för att läsa chatten, se subs, raids och meddelanden och få rollinformation via IRC-taggar. Inloggning behövs för timeout, ban, raid, för att skriva i chatten, för Twitchs egna badgebilder och för de händelser som går över EventSub – belöningar, shoutouts, power-ups och hypetåg. Tabellen under [Händelser i chatten](#händelser-i-chatten) visar vad som kräver vad, och vad som bara fungerar i din egen kanal.

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
