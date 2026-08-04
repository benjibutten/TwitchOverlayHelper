# Twitch Overlay Helper

En Windows-app som läser Twitch-chatten och visar den på två ställen: som en stor, lugn och klickigenom overlay ovanpå ett spel, och som en dyslexianpassad chatt i OBS via en Custom Browser Dock. Gränssnittet prioriterar låg visuell trängsel, tydlig avsändare, generöst radavstånd och få beslut per vy.

## Chatt-dock i OBS

Appen kör en lokal webbserver som serverar en chatt byggd för läsbarhet. Adressen klistras in i OBS under **Vy → Dockor → Anpassade webbläsardockor**; den finns färdig att kopiera i appens inställningsfönster.

- Egna reglage för teckenstorlek, radavstånd, **teckenmellanrum och ordmellanrum** – de två sistnämnda hjälper mest vid dyslexi men saknas i Twitchs egen dock.
- Fem lugna teman med gräddvit grund i stället för hård svartvit kontrast.
- **Tempobroms** som håller igen hur snabbt nya meddelanden dyker upp, så de hinner läsas när chatten går varmt.
- **Fastnålade mentions**: meddelanden till dig läggs i en remsa överst så frågor inte scrollar bort.
- Länkar kortas till en `🔗 länk`-knapp, VERSALER dämpas och `!kommandon` tonas ner.
- Paus, zebra-rader, namn på egen rad och val av typsnitt.
- Moderering med stora knappar: klicka på ett namn för timeout, ban eller att ta bort meddelandet. Ban kräver bekräftelse och åtgärder går att ångra direkt i notisen.
- Raid-väljare som listar de kanaler du följer som är live just nu.
- Skrivfält för att svara i chatten.
- Docken innehåller inga inställningar – allt utseende styrs från appen under **Ändra chattens läsbarhet** och slår igenom direkt. Docken visar exempelmeddelanden innan chatten är ansluten, så det går att ställa in läsbarheten i lugn och ro.

Moderering fungerar i alla kanaler där du är moderator, inte bara i din egen. Raid går däremot bara att starta från din egen kanal – Twitch tillåter inget annat – så raid-knappen döljs när du tittar på någon annans chatt.
- Serverns adress innehåller en hemlig nyckel och är bunden till `127.0.0.1`, så den är varken nåbar från nätverket eller användbar för andra sidor på datorn.

Moderering, raid och skrivfältet kräver inloggning; utan den fungerar docken som en ren läsvy.

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
- Twitch-metadata för broadcaster, moderator, VIP, subscriber med lokala markörer.
- Riktiga Twitch-badge-bilder när Client ID och OAuth-token anges.
- Automatisk återanslutning, PING/PONG-hantering och tydligt stopp vid nekad inloggning.
- Inställningar sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\settings.json`. OAuth-token sparas aldrig.
- Körs som en enda instans; en ny vanlig start öppnar den redan körande appens inställningsfönster.
- Valbar **Starta med Windows**-inställning som startar appen minimerad i meddelandefältet utan extra bakgrundstjänst.

## Kör

```powershell
dotnet run --project src/TwitchOverlayHelper
```

Skriv kanalnamnet och välj **Anslut**. Använd **Redigera overlay** för att placera den och välj sedan **Lås overlay** så att musklick går rakt igenom till spelet.

## Twitch-inloggning (valfritt)

Anonym anslutning räcker för att läsa chatten och få rollinformation via IRC-taggar. Inloggning behövs för timeout, ban, raid, för att skriva i chatten och för Twitchs egna badgebilder.

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
