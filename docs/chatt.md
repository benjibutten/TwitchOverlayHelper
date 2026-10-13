# Chatten i OBS

Appen kör en lokal webbserver med två chattsidor: en dock som du själv läser i, och en genomskinlig sida som tittarna ser på sändningen. Båda adresserna finns att kopiera under fliken **OBS-dock**.

Serverns adress innehåller en hemlig nyckel och är bunden till `127.0.0.1`. Den går inte att nå från nätverket och inte att använda från andra sidor på datorn.

## Docken

Klistra in adressen i OBS under **Vy → Dockor → Anpassade webbläsardockor**.

Docken har inga egna inställningar. Utseendet styrs från appen under **Ändra chattens läsbarhet** och slår igenom direkt. Innan chatten är ansluten visar docken exempelmeddelanden, så läsbarheten går att ställa in i förväg.

- Reglage för teckenstorlek, radavstånd, teckenmellanrum och ordmellanrum. De två sista hjälper mest vid dyslexi och saknas i Twitchs egen dock.
- Fem teman med gräddvit grund i stället för hård svartvit kontrast.
- Tempobroms som håller igen hur snabbt nya meddelanden dyker upp när chatten går fort.
- Meddelanden som nämner dig läggs i en remsa överst, så frågor inte scrollar bort.
- Klicka på ett namn för att nåla fast raden i remsan. Utan inloggning syns nålen bara för dig. Är du inloggad med modbehörighet kan samma rad nålas i chatten så tittarna ser den.
- Länkar kortas till en `🔗 länk`-knapp, VERSALER dämpas och `!kommandon` tonas ner.
- Paus, zebra-rader, namn på egen rad och val av typsnitt.
- Moderering: klicka på ett namn för timeout, ban eller att ta bort meddelandet. Ban kräver bekräftelse, och åtgärder går att ångra i notisen.
- Raid-väljare som listar de kanaler du följer som är live.

Moderering fungerar i alla kanaler där du är moderator. Raid går bara att starta från din egen kanal, så raid-knappen döljs när du tittar på någon annans chatt. Moderering, raid och skrivfältet kräver inloggning. Utan den är docken en ren läsvy.

## Chatt på streamen

En genomskinlig sida som läggs in som **Browser Source** i OBS. Utseendet ställs in under **Ändra utseende på streamchatten**.

Sidan är inte docken med bakgrunden borttagen. Tittarna får inte samma sak som du:

- Smeknamn, moderering, inloggning, fastnålade meddelanden, uppläsningsknappen och anslutningsstatusen skickas aldrig till sidan.
- Ett raderat meddelande försvinner direkt, tillsammans med allt som ännu inte ritats ut. I docken stryks det över i stället.
- `!kommandon` döljs helt, och du kan lista konton som aldrig ska synas. Chattbottarna är ifyllda från början.
- Egna val för vilka händelsekort tittarna ser.

Reglagen gäller storlek, platta bakom texten, mörk kontur runt bokstäverna, antal rader och hur länge raderna ligger kvar. Sätt en tid om rutan ligger ovanpå spelet, så töms den när det är tyst. Låt den vara noll om chatten har ett eget hörn. Nyaste raden kan ligga underst som i vanlig chatt, eller överst om rutan sitter i överkanten.

Sidan delar renderare med docken, så emotes och länkknappar ser likadana ut på båda ställena. Under en raid ritas nya rader några stycken per bildruta, och rader som aldrig hade hunnit synas kastas.

## Chatten ligger kvar

Chatten sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\chat-history.json` och läggs tillbaka när appen startar. Det som sparas är bara det appen själv har sett. Twitch skickar ingen historik vid anslutning.

- Som mest 12 timmar bakåt och 200 rader. Åldern räknas per rad, inte per kalenderdygn, så en omstart strax efter midnatt tömmer inte kvällens chatt.
- Historiken hör till en kanal och slängs när du byter kanal.
- Med **Hämta tidigare meddelanden** påslaget läggs det som sades innan appen anslöt ovanför de egna raderna, med samma 12-timmarsgräns.

Har det varit tyst i mer än sex timmar mellan två rader ritar docken en avdelare där: `Nytt pass · 9 timmar tyst`, med knappen **Dölj N rader ovanför**. Knappen tar bort raderna ur chattspalten, ur overlayen och ur filen på disk, så de kommer inte tillbaka vid omladdning eller omstart. Rader du nålat fast för hand ligger kvar i remsan tills du själv tar ner dem.

## Skriva i chatten

Skrivfältet längst ner i docken syns när du är inloggad. Ett skickat meddelande visas först när Twitch har bekräftat det. Nekar Twitch det (slowmode, följarläge, timeout, dubblett) får du Twitchs förklaring och texten tillbaka i rutan. Egna rader som Twitch inte gav något meddelande-ID saknar knapparna för att nåla fast åt tittarna och ta bort, eftersom de behöver ID:t.

### @-förslag

Skriv `@` så listas de som nyligen sagt något, senast först. Listan söker på Twitch-namn, visningsnamn och smeknamnet du satt, och kräver varken inloggning eller extra anrop. Pilarna flyttar markeringen, Tabb eller Enter väljer, Esc stänger.

Det som skrivs in är Twitch-namnet när visningsnamnet bara är samma namn med versaler, annars inloggningsnamnet. Det är det Twitch känner igen som en mention för konton vars visningsnamn står i ett annat skriftsystem. Listan töms när docken byter kanal.

### Emote-väljare

🙂-knappen visar vad du får skicka i den här kanalen, i fyra avdelningar: nyligen använda, kanalens emotes, dina emotes och globala. *Nyligen använda* samlas ur dina egna rader, inte ur chatten i stort. Sökrutan letar bland alla, och väljaren stannar öppen när du klickar.

En vald emote visas som bild i skrivfältet. Det som skickas är fortfarande text.

Twitch talar inte om för avsändaren vilka ord i den egna raden som var emotes. Appen fyller i det själv innan meddelandet går ut till vyerna, så docken och overlayen visar samma sak.

En emote du inte får skicka erbjuds aldrig. Twitch har ingen fråga för "får det här kontot skicka den här emoten här", så svaret sätts ihop av kanalens lista (allt kanalen har, oavsett om du får använda det) och din personliga lista (det du får skriva). Kanalens emotes visas bara när listan går att hålla mot din personliga, eller när det är din egen kanal.

Går det inte att kontrollera visas bara de globala emotes, och väljaren säger varför. Det gäller två fall:

- Behörigheten `user:read:emotes` saknas. **Logga in igen** i appen löser det.
- Din egen emote-lista är så lång att den slog i hämtningstaket.

Listan hämtas en gång per kanal och konto.

## Smeknamn på chattare

Ge en chattare ett eget namn som visas bredvid Twitch-namnet, aldrig i stället för det.

- Klicka på ett namn i docken → **🏷 Sätt smeknamn**, eller klicka på ett befintligt smeknamn för att ändra det.
- 🏷-knappen i dockens överkant listar alla smeknamn med sökruta.
- Smeknamnet syns i både docken och overlayen, på alla rader personen redan skrivit. Det går bara att sätta från docken.
- Det lämnar aldrig datorn. Ingen tittare ser det, och det krävs varken inloggning eller modbehörighet.
- Namnet knyts i första hand till Twitch-ID:t, så det följer med om personen byter användarnamn.
- Ett tomt fält eller **🗑 Ta bort** tar bort smeknamnet. Notisen efteråt har en **Ångra**-knapp.

Smeknamn sparas i `%LOCALAPPDATA%\TwitchOverlayHelper\nicknames.json`, direkt när något ändras. Hur filen skyddas står i [Data och uppdateringar](data-och-uppdateringar.md).
