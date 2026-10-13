# Händelser i chatten

Subs, raids, meddelanden från streamern och annat som händer i chatten visas som egna kort bland meddelandena, i docken, overlayen och streamchatten.

Ett hypetåg pågår i minuter och får därför en remsa överst i docken med nivå, mätare och toppbidrag. Overlayen visar två kort i stället: ett när tåget startar och ett när det tar slut.

## Vad som kräver inloggning

Det mesta kommer via IRC och fungerar utan inloggning i vilken kanal som helst. Något Twitch skickar som saknar eget kort visas med Twitchs egen text. Resten kommer via EventSub och kräver inloggning, rätt behörighet och oftast din egen kanal.

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

Saknas en behörighet visas färre kort. Chatten fortsätter läsas som vanligt. Under **5. Logga in för moderering** står vilka extra händelser som är påslagna, och om något saknas för att en behörighet inte är given eller för att kanalen inte är din.

En sparad inloggning från innan en behörighet fanns rättas med **Logga in igen**. En förnyad token ger aldrig nya behörigheter.

Power-upen *förstorad emote* ritas i full storlek på en egen rad. Meddelandeeffekter (`animation-id`) kommer över IRC och visas som en markör i alla kanaler, även utloggad. Själva animationen återges inte.

## Stänga av korttyper

Varje typ går att stänga av för sig, och de tre vyerna har egna val:

- Overlayen: **Händelser i overlayen**
- Docken: **Ändra chattens läsbarhet → Händelser i chatten**
- Streamchatten: **Ändra utseende på streamchatten → Händelser i chatten**

Att stänga av en typ tar bort korten som redan visas. Att slå på den igen gäller det som händer därefter (docken hämtar tillbaka historiken vid omladdning). Reglagen styr bara korten: en belöning triggar fortfarande pets, och en cheer är fortfarande en markör på meddelandet.
