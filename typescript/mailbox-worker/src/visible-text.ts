// The one rule for names a human reads and picks between (agent names, key labels, OAuth client names). The
// authorization server imports it too, so its consent and owner pages and the mailbox agree. Dependency free.

// C0/C1 controls, bidi marks, embeddings, overrides and isolates, and the default-ignorable format characters that
// hide inside a name (soft hyphen, ZWSP, word joiner, BOM). Arabic/CJK text and the zero-width joiner inside real
// emoji are kept.
const invisible = /[\u0000-\u001f\u007f-\u009f­؜​‎‏‪-‮⁠⁦-⁩﻿]/gu;
const visibleCharacter = /[^\p{Default_Ignorable_Code_Point}\p{White_Space}]/u;

/** `text` with every invisible or control character replaced by `replacement`. */
export const replaceInvisible = (text: string, replacement: string): string => text.replace(invisible, replacement);

/**
 * True when `text` has no invisible or control character and at least one visible one: a name made only of whitespace
 * or invisible formatting/variation characters could be mistaken for another or for none.
 */
export const visibleName = (text: string): boolean => replaceInvisible(text, '') === text && visibleCharacter.test(text);

/** True when `text` has at least one character that is neither whitespace nor default ignorable. */
export const hasVisibleCharacter = (text: string): boolean => visibleCharacter.test(text);

/**
 * Text a platform sent back (a refusal reason, an error), made safe to store and show: invisible and control characters
 * become spaces, whitespace runs collapse, lone surrogates become U+FFFD, and the cut to at most `max` UTF-16 units
 * never splits a surrogate pair.
 */
export function visibleExcerpt(text: string, max: number): string {
    const flat = replaceInvisible(text, ' ').replace(/\s+/g, ' ').trim().toWellFormed();
    const cut = flat.slice(0, max);
    return (/[\ud800-\udbff]$/.test(cut) ? cut.slice(0, -1) : cut).trimEnd();
}
