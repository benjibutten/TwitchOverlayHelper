# Overlayen över spelet

En transparent chattruta som ligger överst på skärmen utan att ta fokus. Använd **Redigera overlay** för att flytta den och ändra storlek, och **Lås overlay** så att musklick går rakt igenom till spelet.

- Globala snabbtangenter som fungerar även när spelet har fokus: visa/dölj overlay (standard `Ctrl+F9`) och redigeringsläge (standard `Ctrl+F10`). Kombinationerna ändras i appen.
- Separata reglage för rutans bakgrund och varje meddelandes bakgrund. Båda kan dras till 0 %.
- Valfri kantlinje runt texten som håller den läsbar utan bakgrund.
- Textstorlek, radavstånd, typsnitt och antal meddelanden ändras medan overlayen visas.
- Twitch-emotes visas som bilder, även animerade, med statisk bild som reserv. Det fungerar anonymt.
- Unicode-emojis visas i färg med [Twemoji](https://github.com/jdecked/twemoji) (CC-BY 4.0), eftersom WPF annars ritar dem i svartvitt.
- Markörer för broadcaster, moderator, VIP och subscriber. Med Client ID och inloggning används Twitchs egna badge-bilder.
- Appen återansluter av sig själv och stannar med ett tydligt besked om inloggningen nekas.

## Kantljus

Ett mjukt ljus som tonar in längs skärmens kanter och släpper igenom musklick. Det tänds i två fall, med varsin färg, styrka och varaktighet:

- En mod eller streamern skriver kommandot (standard `!psst`).
- En ny chattare skriver sitt första meddelande.

Testknappar finns i appen. Flera träffar tätt inpå varandra ger aldrig flera ljus samtidigt:

- Ett mod-anrop går alltid fram och trängs aldrig undan av ett välkomnande.
- Välkomnanden är tysta i 15 sekunder efter ett visat ljus.
- Ett ljus som redan lyser hålls kvar i stället för att blinka om, som mest dubbelt så länge som det är inställt på.

Kantljuset kan också tändas när en uppläsning väntar på godkännande. Se [Uppläsning](upplasning.md#godkännande).

## Övrigt

- Appen körs som en enda instans. En ny start öppnar inställningsfönstret i den som redan körs.
- **Starta med Windows** startar appen minimerad i meddelandefältet.
