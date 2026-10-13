# Chatbot

Boten säger i chatten det tittaren behöver veta: varför poängen kom tillbaka, att en uppläsning väntar på svar, att pet-overlayen ligger nere. Den svarar också på kommandon du själv skriver.

## Vem som skriver

Väljs under fliken **Bot**:

- **Eget botkonto.** Ett andra Twitch-konto med egen inloggning och egen IRC-anslutning. Token sparas i `credentials.bot.bin`, krypterad med DPAPI. Inloggningen begär bara `chat:read` och `chat:edit` och använder samma Client ID som din egen.
- **Mitt eget konto.** Går över anslutningen appen redan har. Meddelandena ser ut att komma från dig.
- **Av.** Ingenting skrivs.

Logga in som boten i ett privat webbläsarfönster, annars godkänns koden med ditt eget konto.

Gör gärna boten till moderator i kanalen. Det höjer Twitchs skrivtak från 20 till 100 meddelanden per 30 sekunder och släpper förbi follower- och slow mode.

## Vad boten säger

Varje meddelande är en egen rad som går att slå av och skriva om, med förhandsvisning under textrutan. `{viewer}`, `{cost}`, `{reason}`, `{link}` och de andra byts ut när meddelandet skickas.

Påslaget från början:

- poängen kom tillbaka, och varför
- uppläsningen väntar på svar
- gräsmattan är full
- overlayen är nere, vänta med att lösa in
- mod-anropet gick fram eller inte
- välkommen till chatten
- tack för raiden

Avstängt från början är subs, hypetåg och en pet som levde klart. Twitch säger redan det mesta av det.

## Egna kommandon

Ovanför meddelandelistan skriver du egna kommandon: ett ord någon skriver i chatten och vad boten svarar. Appen levereras utan några.

Svaret är en mall som alla andra:

```
!discord   →  Häng med i vår Discord: exempel.se/discord
!regler    →  {viewer}: var schysst mot varandra, det är hela regeln.
```

Varje rad har en paus i sekunder innan samma kommando får svara igen, 30 från början. Rader kan märkas som **endast mods**. Andra som skriver dem får inget svar alls.

Fönstret varnar om samma ord står på fler än en rad. Bara den första svarar.

## Egna ord

Under **Ord boten använder** skriver du vad din kanal kallar pets, i tre former: *en vän*, *två vänner*, *vännen*. Orden används överallt, också i förklaringen till en återbetalning. Ett tomt fält för ditt eget namn betyder att boten säger "streamern".

## Begränsningar

- **Upprepar sig inte.** Twitch släcker tyst ett meddelande som är identiskt med ett från samma konto inom 30 sekunder. Boten hoppar över det och skriver i loggen.
- **Håller Twitchs tak.** Allt går genom en kö. Att gå över taket ger skrivstopp på hela kontot.
- **Slår ihop återbetalningar.** Från tre och uppåt på en gång blir det en enda rad, till exempel efter en omstart.
- **Läcker inga felmeddelanden.** Bara skäl appen känner igen skrivs i chatten. Allt annat får en neutral formulering.
- **Svarar inte sig själv.** Botens egna rader triggar varken pets, kantljus eller välkomnanden.
- **Lovar ingen återbetalning som inte skett.** En uppläsning annonseras som återbetald bara när poängen gick tillbaka.
- **Döljs i overlayen.** Botens rader visas i docken men som standard inte i overlayen över spelet.
