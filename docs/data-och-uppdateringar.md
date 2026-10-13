# Data och uppdateringar

## Dina data

Allt du ställt in ligger i `%LOCALAPPDATA%\TwitchOverlayHelper\`: inställningar, smeknamn, pets, vinster från lyckosnurren, inloggningar och cache. **Säkerhetskopior** längst ned i inställningsfönstret öppnar mappen.

| Fil eller mapp | Innehåll |
|---|---|
| `settings.json` | Inställningar |
| `nicknames.json` | Smeknamn på chattare |
| `chat-history.json` | Chatthistorik, som mest 12 timmar och 200 rader |
| `spinwins.json` | Vinster från lyckosnurren |
| `credentials.bin`, `credentials.bot.bin` | Inloggning för dig och boten, krypterad med DPAPI |
| `speech.bin` | API-nycklar för uppläsning, krypterade med DPAPI |
| `namecache\` | Ljudklipp för upplästa namn |
| `fight\` | Karaktärer och arenor till väntskärmen |
| `backups\` | Daterade kopior från varje sparning |
| `snapshots\` | Zip av hela profilen per version |

OAuth-token sparas aldrig, bara refresh-token.

### Skydd mot förlorade data

**Halvskriven fil.** Varje sparning skrivs till en temporärfil och byts in med ett namnbyte. En avbruten sparning lämnar den gamla filen hel.

**Trasig fil.** Vid varje sparning läggs en daterad kopia i `backups\`. De tjugo senaste sparas. Går huvudfilen inte att läsa används den nyaste kopian som går att tolka, och den skrivs tillbaka på plats. Går ingen kopia att läsa flyttas den trasiga filen till `backups\unreadable\`, och appen startar med standardvärden och säger var filen hamnade. Inställningarna får en kopia som mest var femte minut.

**Ny version.** Första gången appen startar under en ny version kopieras hela profilen till en zip i `snapshots\` innan något skrivs tillbaka. De åtta senaste behålls. Cache och loggar hoppas över. Resten går att lägga tillbaka med Utforskaren när appen är stängd. Kunde någon fil inte läsas heter zippen `-delvis`, och nästa start gör om försöket.

Inställningsfilen har ett `SchemaVersion`-fält, så en senare version kan läsa en äldre form och flytta över värdena. En äldre version som startas ovanpå en nyare fil lämnar det den inte känner igen orört.

## Uppdateringar

Appen frågar GitHub efter ett nyare släpp åtta sekunder efter start, och sedan som mest var tolfte timme. Finns ett nyare visas en fråga. Finns inget säger den ingenting. Startar appen minimerad i meddelandefältet väntar kontrollen tills fönstret öppnas.

Säger du ja hämtas zip-filen och kontrolleras mot släppets SHA-256-summa. En kopia av appen som kör från `%TEMP%` väntar tills appen stängts, byter filerna och startar den nya versionen. Varje fil som skrivs över säkerhetskopieras först, så ett avbrott lämnar den gamla versionen hel. Ligger appen i en mapp som kräver administratör frågar Windows om godkännande.

**Sök efter uppdateringar** finns längst ned i inställningsfönstret och i meddelandefältets meny. En lokal `dotnet run`-byggnad uppdaterar sig aldrig.
