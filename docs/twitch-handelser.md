# Fler Twitch-händelser i chattvyerna

Plan för att visa subs, raids, announcements, inlösta belöningar, shoutouts, power-ups och
hypetåg i både OBS-docken och WPF-overlayen. Kryssa av allteftersom.

Mönstret att följa finns redan: modereringshändelser går som ett eget spår vid sidan av
chattmeddelanden, hela vägen från `ChatModerationEvent` till egen envelope och egen rendering.
Allt nedan är ett tredje spår, `ChatEvent`, byggt likadant.

## Vad varje funktion kräver

| Funktion | Källa | Scope | Var det funkar |
|---|---|---|---|
| Subs, resubs, gift subs | IRC `USERNOTICE` | inget, funkar anonymt | alla kanaler |
| Raids in / raids ut | IRC `USERNOTICE` | inget | alla kanaler |
| Announcements | IRC `USERNOTICE` (`msg-id=announcement`) | inget | alla kanaler |
| Cheers (bits) | IRC `PRIVMSG` + `bits`-tagg | inget | alla kanaler |
| Watch streaks, modiversary, bits-badge | IRC `USERNOTICE` | inget | alla kanaler |
| Inlösta belöningar (även utan textfält) | EventSub `channel.channel_points_custom_reward_redemption.add` | `channel:read:redemptions` | bara egen kanal |
| Inlösta belöningar, *utan* inloggning | IRC `custom-reward-id` på `PRIVMSG` | inget | alla kanaler, men bara en GUID — visas som en neutral markör |
| Power-ups: förstorad emote, firande | EventSub `channel.bits.use` | `bits:read` | bara egen kanal |
| Power-ups: meddelandeeffekter | IRC `animation-id` på `PRIVMSG` | inget | alla kanaler |
| Shoutouts | EventSub `channel.shoutout.create` / `.receive` | `moderator:read:shoutouts` | kanaler du modererar |
| Hypetåg | EventSub `channel.hype_train.begin/progress/end` **v2** | `channel:read:hype_train` | bara egen kanal |
| Pinnade meddelanden | Helix `/helix/chat/pins` (ingen EventSub-push) | oklar, slås upp | måste pollas |

---

## Beslut att ta innan kodning

- [JA] Ska varje händelsetyp gå att slå av var för sig i `DockSettings`? (rekommendation: ja –
      annars drunknar en lugn läsvy i gift sub-spam)
- [URVAL, KONFIGURERBART] Ska overlayen visa allt docken visar, eller bara ett urval? (rekommendation: urval – overlayen ligger ovanpå ett spel och ska vara lugn)
- [KLART] [De ska synas stort om de är gjorda att vara stora] Ska stora emotes renderas i full storlek eller plattas ut med en `⚡ förstorad`-markör?
      (reglaget finns nu, ett per vy — se etapp 3)

---

## Etapp 1 — allt som IRC redan ger

Störst effekt, minst risk: ingen inloggning, inga scopes, ingen ny anslutning. Raderna rullar
redan förbi i `ReadLoopAsync` och kastas osedda.

### Modell och parser

- [x] Lägg till `ChatEventType`-enum och `ChatEvent`-record i `src/TwitchOverlayHelper/Models/ChatMessage.cs`
- [x] Skriv `TryParseUserNotice` i `src/TwitchOverlayHelper/Twitch/IrcMessageParser.cs`, byggd som
      `TryParseModerationEvent`
- [x] Läs `msg-id` och plocka relevanta `msg-param-*` per typ
- [x] Använd `system-msg` som fallback-text, så en okänd `msg-id` blir en läsbar rad i stället för
      att försvinna
- [x] Fånga cheers: `bits`-taggen på vanlig `PRIVMSG` i `TryParseChatMessage`
- [ ] Verifiera `msg-id=announcement` mot en riktig kanal — den skickas i praktiken men saknas i
      Twitchs IRC-dokumentation
      → **kvar att göra live.** Koden hanterar den och `ParsesAnAnnouncementWithItsColourAndText`
      spikar formen, men en riktig kanal är det enda som kan bekräfta att taggarna ser ut så.
      Missar den, blir det ett `Other`-kort med Twitchs egen `system-msg` — inget försvinner.

Två saker som växte fram under vägen och inte stod i planen:

- Ordvalen bor i `Models/ChatEventText.cs`, inte i vyerna. `system-msg` är på engelska, och
  eftersom både docken och overlayen ska säga samma sak på svenska formuleras raden på ett ställe.
- `ChatTimelineItem` i samma modellfil är det som gör historiken och overlaykön till en tidslinje.

### Transport

- [x] Nytt `event Action<ChatEvent>? EventReceived` på `TwitchChatClient`
- [x] Anropa den från `ReadLoopAsync` bredvid `TryParseModerationEvent`
- [x] Koppla in den i `MainWindow.xaml.cs` där `MessageReceived`/`ModerationReceived` kopplas idag

### Historik (gör nu, inte senare)

- [x] Gör om `ChatHub._history` från `Queue<ChatMessage>` till en tidslinje av *antingen* meddelande
      eller händelse, så ordningen överlever en OBS-omstart
- [x] Uppdatera `IsAffected`-filtreringen i `PublishModeration` för den nya kötypen
      → moderering biter bara på meddelanden. En timeout tar tillbaka det någon sa, inte suben de
      betalade för, och båda vyerna låter händelsekorten stå kvar av samma skäl.
- [x] Se till att `BuildHello` skickar med händelser i historiken
      → historiken går som taggade poster (`{type, message | event}`), så docken kan spela upp den
      genom samma kod som tar emot live-rutorna.

### Utfläkt och rendering

- [x] `PublishEvent` i `ChatHub.cs`
- [x] `DockEvent`-record och mappning i `Web/DockContracts.cs`
- [x] `event`-gren i `handle()` i `Web/wwwroot/app.js`
- [x] Händelsekort i `Web/wwwroot/styles.css`, med samma lugna typografi som meddelanden
- [x] Händelsekort i `OverlayWindow.CreateMessageCard` (eller en syskonmetod `CreateEventCard`)
- [x] Kontrollera att korten räknas rätt mot `maxMessages` i båda vyerna
      → korten ligger i samma kolumn i båda vyerna, så `childElementCount` respektive
      `MessagePanel.Children.Count` räknar dem redan. Det som behövde fixas var ombyggnaden vid
      inställningsändring: den plockade förut ut `ChatMessage` ur `card.Tag` och hade tappat
      händelserna.

### Tester

- [x] Parser-tester per `msg-id` i stil med `tests/TwitchOverlayHelper.Tests/ModerationParsingTests.cs`
- [x] Test för `system-msg`-fallback vid okänd `msg-id`
- [x] Test för cheer-taggen
- [x] Test för att historiken behåller ordningen mellan meddelanden och händelser

---

## Etapp 2 — EventSub-klienten

Websocket-transporten använder den befintliga användartoken. Ingen client secret, ingen
redirect-URI, ingen app-token — device-flödet räcker precis som det står.

**Grundregel genom hela etappen:** varje "nej" — utloggad, saknat scope, annans kanal, ingen
mod-roll — slutar med *färre kort*, aldrig med en chatt som slutar läsa. Etapp 1 fungerar oförändrat
utan inloggning.

### Scope-migrering (bygg in från början)

- [x] Lägg till nya scopes i `TwitchAuth.RequiredScopes`
      → `channel:read:redemptions` och `moderator:read:shoutouts`. "Required" gäller *frågan* vid
      inloggning; vid körning är varje scope frivilligt.
- [x] Jämför sparade `StoredCredentials.Scopes` mot `RequiredScopes` vid start
- [x] Visa "logga in igen för att slå på X" i appen i stället för att tyst få 403 från Twitch
      → egen knapp, och texten säger den *verkliga* orsaken: saknat scope och kanalroll är olika
      problem och ska inte skylla på varandra.
- [x] Kom ihåg: en refresh ger tillbaka *gamla* scopes — nya kräver ny inloggning
      → därför loggar `Reauthorize_Click` ut och kör om device-flödet; en refresh hade inte räckt.

### Klienten

- [x] Ny `src/TwitchOverlayHelper/Twitch/TwitchEventSubClient.cs`, syskonklass till `TwitchChatClient`
- [x] Anslut till `wss://eventsub.wss.twitch.tv/ws`
- [x] Hantera `session_welcome` → spara `session_id`
- [x] Hantera `session_keepalive`
- [x] Hantera `session_reconnect` → byt till `reconnect_url` utan att tappa prenumerationer
      → och prenumerera *inte* om: den nya sessionen bär redan över dem, så en ny omgång hade
      dubblerat varje händelse.
- [x] Återanslutning med backoff, som `RunWithReconnectAsync` i chattklienten
- [x] `CreateSubscriptionAsync` i `TwitchApiClient.cs` (`POST /helix/eventsub/subscriptions`,
      `transport: { method: "websocket", session_id }`)
- [x] Avgör vilka prenumerationer som är möjliga i den anslutna kanalen (egen kanal / mod / ingen)
      och prenumerera bara på dem
      → `EventSubPlan` avgör före socketen öppnas. Mod-rollen går inte att slå upp utan ännu ett
      scope, så shoutouts frågas efter och en 403 tolkas som "inte moderator här" — inte som fel.
- [x] Slå upp och notera Twitchs gränser för antal prenumerationer per websocket-session
      → **300 aktiva prenumerationer per socket**, och **3 socketar med aktiva prenumerationer per
      client id**. Vi öppnar en socket och ber om högst tre saker, så ingendera är i närheten.
      ([EventSub WebSocket-referensen](https://dev.twitch.tv/docs/eventsub/handling-websocket-events/))

### Belöningar

- [x] Prenumerera på `channel.channel_points_custom_reward_redemption.add`
- [x] Visa titel, kostnad, användare och inskriven text — inte bara GUID som idag
      → `reward` är ett **nästlat** objekt (`id`/`title`/`cost`/`prompt`) och redemptionens eget id
      heter `id`. Namnen hämtas dessutom från `/helix/channel_points/custom_rewards` innan socketen
      öppnas, så den *första* inlösen redan visas med namn.
- [x] Låt `PetService`-reglerna trigga även på belöningar *utan* textfält
- [x] Låt pet-reglerna matchas på belöningens namn i stället för GUID i `MainWindow`
      → `RewardName` matchas *vid sidan av* `RewardId`, inte i stället för. I en kanal där namnen
      inte går att läsa är GUID:en fortfarande det enda en inlösen bär med sig.
- [x] Undvik dubbelvisning: belöningar med textfält kommer även in via IRC:s `custom-reward-id`
      → en inlösen *med* text får inget eget kort; den syns som meddelandet den skapade, med
      belöningen som markör — samma form som Twitchs egen chatt. Bara de tysta belöningarna får
      kort, eftersom inget annat kan visa dem. Pets triggar bara på en av vägarna.

### Shoutouts

- [x] Prenumerera på `channel.shoutout.create` och `channel.shoutout.receive`
- [x] Kräver `moderator:read:shoutouts` och mod-roll i kanalen — dölj/gråa ut när det saknas
      → läs-scopet räcker, `manage` behövs inte (vi skickar aldrig en shoutout).

---

## Etapp 3 — Power-ups och stora emotes

Twitch talar **inte** om vilken emote som är den förstorade. Känt öppet ärende
([twitchdev/issues#1047](https://github.com/twitchdev/issues/issues/1047)). Konventionen är att det
alltid är den *sista* emoten i meddelandet — kommentera det i koden, det vilar på konvention och
inte på ett kontrakt.

- [x] Prenumerera på `channel.bits.use` (`bits:read`), `type: "power_up"`
      → `bits:read` gäller bara egen kanal, till skillnad från shoutouts finns inget att lära av att
      fråga i någon annans chatt. `EventSubPlan` säger därför nej direkt, och nämner inte heller
      scopet bland de saknade när man tittar på en annan kanal.
- [x] Hantera `power_up.type`: `gigantify_an_emote`, `message_effect`, `celebration`
      → två av dem *ska* inte ge något här: en vanlig cheer syns redan via IRC:s `bits`-tagg, och en
      meddelandeeffekt kommer också över IRC. Firandet är den enda som saknar meddelande att sitta
      på, så den får ett eget kort. Egna bits-belöningar (`custom_power_up`) lämnas orörda.
- [x] Läs `animation-id`-taggen på IRC-sidan för meddelandeeffekter
      → odokumenterad men skickad i praktiken, och den vägen gör att effekter syns för en utloggad
      läsare i vilken kanal som helst — inte bara för streamern i sin egen.
- [x] Markera meddelandet som gigantified i modellen
      → knuten i hela etappen: Twitch skickar *inget* som binder ihop `channel.bits.use` med
      chattraden, och de kommer på var sin anslutning i oförutsägbar ordning. `PowerUpTracker` parar
      ihop dem på användar-id och texten, och klarar båda ordningarna: kommer power-upen först väntar
      den på raden, kommer raden först skickas den om märkt (`messageUpdate` till docken,
      `OverlayWindow.UpdateMessage` i overlayen). Historiken i `ChatHub` skrivs om på samma gång.
      → `power_up.emote.id` finns faktiskt i nyttolasten, så vi behöver inte gissa *vilken* emote.
      Konventionen (sista emoten) ligger kvar som fallback i `GigantifiedEmoteIndex` — den är vad
      som gäller när id:t inte längre finns i meddelandet.
      → två saker som visade sig behöva hängslen: `message` är *optional* på `channel.bits.use`, så
      matchningen kräver att emoten faktiskt finns i raden och inte bara att texten är tom; och
      märkningen måste publiceras *efter* raden själv (`_publishGate` i `MainWindow`), annars kan
      EventSub-tråden hinna före IRC-tråden och docken kastar en uppdatering av ett meddelande den
      aldrig fått.
- [x] Rendera sista emote-spannet med CDN-variant `3.0` i stället för `2.0` i `renderBody` (app.js)
- [x] Motsvarande i `OverlayWindow.CreateMessageBody`
      → WPF klipper bilden mot radhöjden, så kortet med en stor emote byter från
      `BlockLineHeight` till `MaxHeight` i `ApplyMessageTypography`. Alla andra rader står kvar på
      samma höjd som förut.
- [x] Reglage i appen: full storlek
      → ett per vy, som `ShowEmotes`: `AppSettings.GiantEmotes` för overlayen och
      `DockSettings.GiantEmotes` för docken. På som förval — någon har betalat bits för att göra den
      stor. Av ger vanlig storlek plus markören `⚡ förstorad`, precis som beslutet överst föreslog.
- [x] Kontrollera att en trippelstor emote inte river sönder radavstånd och tempobroms i docken
      → `.emote[data-giant="true"]` är `display: block` och tar en egen rad. Inline hade den sträckt
      ut raden den råkade hamna på och lämnat resten av meddelandet drivande runt sig. Tempobromsen
      räknar rutor, inte pixlar, så ett meddelande är fortfarande ett meddelande hur högt det än är —
      och `load`-lyssnaren på `el.chat` rullar om när bilden landar.

Två saker som växte fram under vägen:

- `bits:read` ligger nu i `RequiredScopes`, så en sparad inloggning från före etapp 3 kommer att
  visa "logga in igen"-knappen. Det är scope-migreringen från etapp 2 som gör sitt jobb; inget slutar
  fungera under tiden.
- Kortet för ett firande heter `Celebration` i `ChatEventType` och inte "PowerUp": de andra två
  power-upsen är markeringar på ett meddelande, inte händelser, och en gemensam typ hade suddat ut
  just den skillnaden.

---

## Etapp 4 — Hypetåg

Ett hypetåg är inte en rad i en logg, det är ett tillstånd som lever i minuter. Rätt form är en
remsa överst — samma plats och mekanik som den fastnålade mentions-remsan redan har.

- [x] Prenumerera på `channel.hype_train.begin`, `.progress`, `.end` — **version 2**, inte v1
      (v1 saknar delade tåg och golden kappa)
      → kräver `channel:read:hype_train` och bara egen kanal, precis som power-ups. Alla tre måste
      gå igenom: en `begin` utan `end` hade lämnat remsan uppe över ett tåg som tog slut för en
      halvtimme sedan.
- [x] Håll aktuellt hypetågstillstånd som fält i `ChatHub` (som `_statusText`)
      → och medvetet *inte* i historiken: ett tåg är en sak som ändrar sig i minuter, inte en rad
      per steg, och att minnas varje `progress` hade tryckt ut chatten ur kolumnen de delar.
- [x] Skicka med tillståndet i `hello`, så en dock som ansluter mitt i tåget inte står tom
      → men bara om tåget fortfarande har något sant att säga: ett tåg som tog slut innan sidan
      fanns är inga nyheter, så `IsWorthShowing` filtrerar i `BuildHello`.
- [x] Remsa överst i docken med nivå och progressbar
- [x] Uppdatera på `progress`, tona ut några sekunder efter `end`
      → två saker tar bort remsan av sig själv: ett avslutat tåg efter tolv sekunder, och ett
      *pågående* tåg när dess egen `expires_at` passerat. Utan den andra hade en tappad anslutning
      mitt i ett tåg lämnat en frusen mätare uppe resten av sändningen.
- [x] Visa toppbidragsgivare
      → tre stycken, och tiernumret översätts. Twitch skriver en prenumeration som sitt tierpris
      (500/1000/2500), vilket är en kodning och inte en siffra någon ska se. Raden heter
      *toppbidrag* och inte *störst bidrag*: Twitch rankar dem per bidragsmetod, så den första är
      inte nödvändigtvis det största enskilda bidraget.
- [x] I overlayen: hoppa över progressbaren, visa bara start och slut som två kort

Tre saker som växte fram under vägen:

- Twitch lovar uttryckligen *ingen* ordning: en `progress` kan komma före den `begin` som startade
  den. Regeln blev därför att ett tåg aldrig går bakåt (`HypeTrainState.Supersedes`), och att slutet
  alltid vinner — slutets nyttolast saknar både `progress` och `goal` och hade annars lästs som ett
  steg tillbaka.
- Det här är det första som *inte* går genom `OnChatEvent`. De två vyerna vill ha olika saker av
  samma sak: docken får hela tillståndet och ritar en remsa, overlayen får två kort. Ett kort per
  bidrag hade begravt chatten det står bredvid.
- `channel:read:hype_train` ligger nu i `RequiredScopes`, så en sparad inloggning från före etapp 4
  visar "logga in igen"-knappen. Samma scope-migrering som etapp 2 byggde; inget slutar fungera
  under tiden.

Fyra saker som code reviewn fångade och som är åtgärdade:

- Remsan åkte med `clear`-rutan, som *också* skickas när första riktiga raden ersätter exempelraderna.
  Ett tåg i ett tyst rum torkades bort av en främlings första "hej". Nu har remsan en egen ruta
  (`ChatHub.ClearHypeTrain`), och `clear` rör den inte.
- Prenumerationerna kedjades med kortslutande `&&`, så ett misslyckat `progress` gjorde att `end`
  aldrig ens efterfrågades — och `end` är den som tar ner remsan igen. Alla tre frågas nu var för sig.
- Frånkoppling och omstart av EventSub lämnade remsan uppe. Vi vet att vi slutat lyssna, så den ska
  inte påstå något: både `DisconnectButton_Click` och `RestartEventSubAsync` rensar nu.
- Korten deduplicerades inte alls. De grindas *inte* på remsans `Supersedes` — en `begin` som kommer
  efter sin `progress` avvisas med rätta av remsan men är fortfarande ett sant startkort — utan på
  kortets eget id, som namnger ögonblicket och inget annat.

---

## Etapp 5 — Pinnade meddelanden

Helix har fått endpoints för pins men det finns **ingen EventSub-push**, så "visa vad streamern
pinnat" kräver pollning. Bygg det åt andra hållet i stället.

- [ ] Slå upp scope-namnen för `/helix/chat/pins` innan skrivdelen byggs
- [ ] "Nåla fast"-knapp i användarpanelen i `app.js`
- [ ] Nåla lokalt i den befintliga pin-remsan — fungerar även utan inloggning, som ren läshjälp
- [ ] Valfritt: skicka även `POST /helix/chat/pins` när man är inloggad med rätt behörighet, så
      pinnen syns för tittarna
- [ ] Avgör hur lokala pins och mentions-pins samsas i samma remsa

---

## Efterarbete

- [ ] Reglage per händelsetyp i `Settings/DockSettings.cs` + UI i `MainWindow.xaml`
      → tills det finns visar **båda** vyerna alla typer. Urvalet i overlayen är just det här
      reglaget, så beslutet ovan är inte glömt – det ligger här.
- [ ] Uppdatera `README.md` med de nya funktionerna och vilka som kräver inloggning
- [ ] Notera i README vilka funktioner som bara fungerar i egen kanal

---

## Källor

- [IRC Concepts](https://dev.twitch.tv/docs/chat/irc/)
- [EventSub Subscription Types](https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/)
- [EventSub Reference](https://dev.twitch.tv/docs/eventsub/eventsub-reference/)
- [Twitch API Reference](https://dev.twitch.tv/docs/api/reference)
- [twitchdev/issues#1047 — gigantified emote](https://github.com/twitchdev/issues/issues/1047)
- [Power-up events — Twitch Developer Forums](https://discuss.dev.twitch.com/t/power-up-events/59404)
