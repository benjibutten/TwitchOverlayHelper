# Twitch Overlay Helper

En Windows-app som läser Twitch-chatten och visar den där du behöver den: som en overlay ovanpå spelet, som en lättläst dock i OBS och som en genomskinlig chattruta på sändningen. Den är byggd för att vara lätt att läsa, även med dyslexi: stor text, generöst radavstånd och få val per vy.

[**Hämta för Windows**](https://github.com/benjibutten/TwitchOverlayHelper/releases/latest)
· Windows x64 · fristående, ingen .NET-installation · Apache-2.0

## Vad den gör

- **Overlay över spelet.** En transparent chattruta som ligger överst och släpper igenom musklick, med kantljus när en mod behöver dig. [Mer](docs/overlay.md)
- **Chatt-dock i OBS.** Chatt med reglage för teckenmellanrum och ordmellanrum, tempobroms, fastnålade mentions, smeknamn, moderering och skrivfält. [Mer](docs/chatt.md)
- **Chatt på streamen.** Samma chatt för tittarna som Browser Source, utan något privat. [Mer](docs/chatt.md#chatt-på-streamen)
- **Händelsekort.** Subs, raids, belöningar, shoutouts, power-ups och hypetåg, med reglage per typ i varje vy. [Mer](docs/handelser.md)
- **Pets och lyckosnurren.** Belöningar som släpper ut pets på skärmen och betalas tillbaka om peten aldrig syntes, och en snurr där tittare vinner pets att behålla. [Mer](docs/pets.md)
- **Väntskärm med fajt.** Två karaktärer slåss medan streamen står still, och chatten röstar, hejar och helar. [Mer](docs/fajt.md)
- **Uppläsning.** Svårlästa namn upplästa för dig, och meddelanden som tittare köpt med bits eller channel points upplästa i sändningen. [Mer](docs/upplasning.md)
- **Chatbot.** Berättar för tittaren varför poängen kom tillbaka, och svarar på kommandon du skriver själv. [Mer](docs/chatbot.md)

## Kom igång

1. Hämta zip-filen från [senaste släppet](https://github.com/benjibutten/TwitchOverlayHelper/releases/latest), packa upp den i en mapp och starta `TwitchOverlayHelper.exe`.
2. Skriv kanalnamnet eller klistra in en Twitch-länk och välj **Anslut**. Det fungerar utan inloggning.
3. Välj **Redigera overlay**, placera rutan och välj **Lås overlay** så att klick går igenom till spelet.
4. För OBS: kopiera adresserna under fliken **OBS-dock**. Docken läggs in under **Vy → Dockor → Anpassade webbläsardockor**, streamchatten som **Browser Source**.

Appen uppdaterar sig själv från GitHub-släppen och säkerhetskopierar inställningarna vid varje sparning. Se [Data och uppdateringar](docs/data-och-uppdateringar.md).

## Twitch-inloggning (valfritt)

Utan inloggning kan appen läsa chatten och visa subs, raids och roller. Inloggning behövs för att moderera, raida, skriva i chatten, använda emote-väljaren, visa Twitchs badge-bilder och ta emot händelser via EventSub (belöningar, shoutouts, power-ups, hypetåg). [Tabellen i Händelser](docs/handelser.md#vad-som-kräver-inloggning) visar vad som kräver vad.

1. Registrera en app på [dev.twitch.tv](https://dev.twitch.tv/console/apps) med klienttypen **Public**.
2. Klistra in dess Client ID i appen och välj **Logga in med Twitch**.
3. Skriv in koden du får på `twitch.tv/activate`.

Ingen client secret och ingen redirect-URI behövs. Refresh-token sparas krypterad med Windows DPAPI (`CurrentUser`) i `%LOCALAPPDATA%\TwitchOverlayHelper\credentials.bin`.

## Dokumentation

- [Overlayen över spelet](docs/overlay.md): snabbtangenter, utseende och kantljus
- [Chatten i OBS](docs/chatt.md): dock, streamchatt, historik, skrivfält och smeknamn
- [Händelser i chatten](docs/handelser.md): korttyper och behörigheter
- [Pets och lyckosnurren](docs/pets.md)
- [Väntskärm med fajt](docs/fajt.md): kommandon, egna karaktärer och arenor
- [Uppläsning](docs/upplasning.md): namn och tittarnas meddelanden
- [Chatbot](docs/chatbot.md)
- [Data och uppdateringar](docs/data-och-uppdateringar.md)

## Utveckling

Kräver .NET 10 SDK på Windows.

```powershell
dotnet run --project src/TwitchOverlayHelper
dotnet test
```

Appen är skriven i WPF och pratar med Twitch via IRC över WebSocket, Helix och EventSub. En inbäddad Kestrel-server på `127.0.0.1` serverar docken och de andra OBS-sidorna från inbäddade resurser, eftersom appen publiceras som en enda fil. Overlay-fönstret använder `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE` och `WS_EX_TOOLWINDOW` och återställer topmost med jämna mellanrum.

Pushar till `main` som ändrar `src/**` bygger och publicerar ett nytt släpp via `.github/workflows/release.yml`.

## Stöd och tack

Twitch Overlay Helper byggs och underhålls av BenjiButten och är gratis och öppen källkod. Ett särskilt tack till [Pixlexi](https://www.twitch.tv/pixlexi), som har bidragit med användningsfallen bakom appen, testat den i skarpt läge och gett återkoppling under hela utvecklingen.

Gillar du appen och vill ge något tillbaka? Ge gärna bort en sub till [Pixlexi](https://www.twitch.tv/pixlexi) på Twitch.

Emojis ritas med [Twemoji](https://github.com/jdecked/twemoji) (CC-BY 4.0).

## Licens

Twitch Overlay Helper är licensierad under [Apache License 2.0](LICENSE). Den som sprider appen vidare ska också behålla uppgifterna i [NOTICE](NOTICE), som licensen kräver.

Släpp till och med `v2026.10.6` gavs ut under MIT och finns kvar under de villkoren. Källkoden från och med bytet till Apache-2.0 är licensierad under Apache-2.0.
