"use strict";

/* What one chat line looks like, for both pages that draw one: the dock the streamer reads and the
   overlay the viewers see. Only the inside of a message lives here – the words, the emotes, the
   links, the small markers – because that is the part that must look the same in both places and is
   the part that is fiddly enough to be worth having exactly one of.

   Everything here is a pure function of a message and a settings object. Nothing reaches for the
   page, the socket or any shared state: the two views hold different settings, different chrome and
   different opinions about what a name is for, and the moment this file knew about either of them it
   would stop being usable by both.

   The settings object needs four things: calmShouting, showEmotes, giantEmotes and collapseLinks.
   Both views' settings carry them under those names, which is why neither has to translate. */

/* Nobody in chat types "https://". A link is written the way it is read – "linktr.ee/perralinks" –
   and a pattern that only knows schemes leaves most of them lying there as dead text. So a bare
   host counts too, but only when its last part is a suffix we recognise: that is what keeps
   "t.ex", "3.5" and "kl.20" out of the link list. */
const URL_PATTERN =
  /(?:https?:\/\/|www\.)[^\s]+|[a-z0-9][a-z0-9-]*(?:\.[a-z0-9][a-z0-9-]*)*\.[a-z]{2,24}(?::\d{2,5})?(?:\/[^\s]*)?/gi;

const LINK_SUFFIXES = new Set((
  "com net org edu gov int mil info biz name pro io gg tv me ly co cc to sh ws am fm " +
  "se no dk fi is ee lv lt de at ch nl be fr es it pt pl cz sk hu ro gr ie uk eu ru ua " +
  "us ca mx br ar cl au nz jp kr cn in id tr za il ae " +
  "app dev art blog chat cloud club design digital email fun games game group host life link " +
  "live media music news one online page pro shop site social space store stream studio team " +
  "tech today top tube video wiki work world xyz zone"
).split(" "));

/* Trailing punctuation belongs to the sentence, not to the address. */
const LINK_TAIL = /[.,;:!?)\]}'"»…]+$/u;

function linksIn(text) {
  const found = [];
  for (const match of text.matchAll(URL_PATTERN)) {
    const before = match.index > 0 ? text[match.index - 1] : "";
    // Skip anything glued to a word, which is mostly the domain half of an e-mail address.
    if (before && /[\w.@/:-]/.test(before)) continue;

    const value = match[0].replace(LINK_TAIL, "");
    if (!value) continue;

    if (!/^(?:https?:\/\/|www\.)/i.test(value)) {
      const host = value.split(/[/:?#]/)[0];
      if (!LINK_SUFFIXES.has(host.slice(host.lastIndexOf(".") + 1).toLowerCase())) continue;
    }
    found.push({ start: match.index, length: value.length });
  }
  return found;
}

function isShouting(text) {
  const letters = text.replace(/[^A-Za-zÅÄÖåäö]/g, "");
  if (letters.length < 8) return false;
  const upper = letters.replace(/[^A-ZÅÄÖ]/g, "").length;
  return upper / letters.length > 0.7;
}

/* Collapsing is about how much room a link takes, not about whether it is one: an address stays
   clickable either way, and only the label changes. On the stream overlay nothing is clickable at
   all, but the anchor is harmless there and the chip is the point – a readable address on a
   broadcast is somebody else's advertisement. */
function appendLink(target, raw, collapse) {
  const anchor = document.createElement("a");
  anchor.className = collapse ? "link-chip" : "link";
  anchor.textContent = collapse ? "🔗 länk" : raw;
  anchor.href = /^https?:\/\//i.test(raw) ? raw : `https://${raw}`;
  anchor.target = "_blank";
  anchor.rel = "noreferrer noopener";
  anchor.title = raw;
  target.appendChild(anchor);
}

function appendText(target, text, calm, collapse) {
  const source = calm ? text.toLowerCase() : text;

  let cursor = 0;
  for (const link of linksIn(source)) {
    if (link.start > cursor) target.appendChild(document.createTextNode(source.slice(cursor, link.start)));
    // Calming a shout must not reach into an address: a path can be case-sensitive.
    appendLink(target, text.substr(link.start, link.length), collapse);
    cursor = link.start + link.length;
  }
  if (cursor < source.length) target.appendChild(document.createTextNode(source.slice(cursor)));
}

/* "default" rather than "static" so an animated emote animates where the browser can play it, which
   is the same choice the picker makes – the two must never show different pictures of one emote. */
const emoteUrl = (id, size) => `https://static-cdn.jtvnw.net/emoticons/v2/${encodeURIComponent(id)}/default/dark/${size}`;

function renderBody(message, view) {
  const body = document.createElement("span");
  body.className = "msg-text";
  if (message.isAction) body.dataset.action = "true";

  // Lower-casing preserves length, so emote spans stay valid after calming a shout.
  const calm = view.calmShouting && isShouting(message.text);
  const collapse = view.collapseLinks;
  /* Including our own lines, which Twitch sends no emote spans for – it decides which words were
     emotes on the way to the viewers and tells everyone except the sender. The app fills those in
     before either view sees the message, so the overlay over the game and the dock agree, and this
     stays one branch rather than two. */
  const emotes = view.showEmotes ? message.emotes : [];
  /* Which span the Gigantify an Emote power-up blew up. The desktop app decides it, so every view
     enlarges the same emote; showing it big is a setting of its own, because a three-line-tall image
     is exactly the kind of thing a narrow column – or a corner of a broadcast – wants to tame. */
  const giant = view.giantEmotes ? message.giantEmote : undefined;

  let cursor = 0;
  for (let i = 0; i < emotes.length; i++) {
    const emote = emotes[i];
    if (emote.start < cursor || emote.start + emote.length > message.text.length) continue;
    if (emote.start > cursor) appendText(body, message.text.slice(cursor, emote.start), calm, collapse);
    const image = document.createElement("img");
    image.className = "emote";
    image.loading = "lazy";
    image.alt = message.text.substr(emote.start, emote.length);
    image.title = image.alt;
    // The 3.0 variant is the only one with the pixels for it; 2.0 scaled up is a blurry mess.
    const size = i === giant ? "3.0" : "2.0";
    if (i === giant) image.dataset.giant = "true";
    image.src = emoteUrl(emote.id, size);
    body.appendChild(image);
    cursor = emote.start + emote.length;
  }
  if (cursor < message.text.length) appendText(body, message.text.slice(cursor), calm, collapse);
  return body;
}

function tag(text, kind) {
  const span = document.createElement("span");
  span.className = "tag";
  span.dataset.kind = kind;
  span.textContent = text;
  return span;
}

/* The badges Twitch has no image for, said in a word instead. Shared because a "mod" that reads as
   "mod" in one view and as nothing in the other would be the same badge telling two stories. */
const BADGE_WORDS = { broadcaster: "live", moderator: "mod", lead_moderator: "mod", vip: "vip", subscriber: "sub" };

/* One row of badges, as far as we can draw them. Twitch hands out an image for almost all of them;
   the handful it does not are the roles above, which are worth a word. */
function appendBadges(head, badges) {
  for (const badge of badges) {
    if (badge.imageUrl) {
      const image = document.createElement("img");
      image.className = "badge";
      image.src = badge.imageUrl;
      image.alt = badge.title || badge.setId;
      image.title = image.alt;
      head.appendChild(image);
    } else if (BADGE_WORDS[badge.setId]) {
      head.appendChild(tag(BADGE_WORDS[badge.setId], badge.setId));
    }
  }
}

/* Icons carry the type at a glance, but never alone: the headline says the same thing in words,
   because an icon is one more thing to decode. */
const EVENT_ICONS = {
  subscription: "★", subGift: "🎁", communityGift: "🎁", subUpgrade: "★",
  raid: "🚚", unraid: "↩", announcement: "📣", bitsBadge: "💎",
  watchStreak: "🔥", newChatter: "👋", other: "✨",
  reward: "🔮", shoutoutSent: "📢", shoutoutReceived: "📢",
  celebration: "🎉",
};

const clockOf = (at) => new Date(at).toLocaleTimeString("sv-SE", { hour: "2-digit", minute: "2-digit" });
