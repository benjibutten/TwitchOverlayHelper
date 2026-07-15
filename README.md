# Twitch Overlay Helper

En Windows-app som läser Twitch-chatten och visar den som en stor, lugn och klickigenom overlay ovanpå ett spel. Gränssnittet prioriterar låg visuell trängsel, tydlig avsändare, generöst radavstånd och få beslut per vy.

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

Anonym anslutning räcker för att läsa chatten och få rollinformation via IRC-taggar. Twitchs badge-API kräver däremot en registrerad apps Client ID och en giltig access token. Fyll i dessa under den valfria sektionen för Twitchs egna badgebilder. Token används bara i minnet under pågående körning.

## Teknik

Projektet använder .NET 10, WPF, Twitch IRC över TLS WebSocket och Helix badge-API. Overlay-fönstret bygger vidare på samma beprövade Win32-mönster som MicMixer: `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW` och periodisk topmost-återställning.

## Nästa produktsteg

- Komplett Device Code OAuth för en enda tydlig “Logga in med Twitch”-knapp.
- Förhandsgranskning av flera dyslexiprofiler och stöd för OpenDyslexic/Atkinson Hyperlegible som paketerade fonter.
- Modereringshändelser (`CLEARMSG`/`CLEARCHAT`) och valfri uppläsning/prioritering av mentions.
- Val av skärm, fästpunkter och OBS Browser Source-läge.
