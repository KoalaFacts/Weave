import assert from 'node:assert/strict';
import { test } from 'node:test';
import { hasVisibleCharacter, replaceInvisible, visibleExcerpt, visibleName } from '../src/visible-text.ts';

// One representative of every class the rule hides, written as escapes so the source shows what is tested.
const hidden: Record<string, string> = {
    'C0 control (NUL)': '\u0000', 'C0 control (US)': '\u001f', 'DEL': '\u007f', 'C1 control (APC)': '\u009f',
    'soft hyphen': '­', 'Arabic letter mark': '؜', 'zero-width space': '​',
    'left-to-right mark': '‎', 'right-to-left mark': '‏', 'left-to-right embedding': '‪',
    'right-to-left override': '‮', 'word joiner': '⁠', 'left-to-right isolate': '⁦',
    'pop directional isolate': '⁩', 'byte order mark': '﻿',
};

test('replaceInvisible_EachHiddenClass_Replaced', () => {
    for (const [name, character] of Object.entries(hidden)) {
        assert.equal(replaceInvisible(`a${character}b`, '_'), 'a_b', name);
    }
});

test('replaceInvisible_ScriptsAndEmojiJoiner_Kept', () => {
    // Positive control: Arabic, CJK, accented Latin and the zero-width joiner inside an emoji sequence are real text.
    for (const text of ['مرحبا', '你好', 'Zoë', '👩‍💻', 'a b']) {
        assert.equal(replaceInvisible(text, '_'), text, text);
    }
});

test('visibleName_HiddenCharacterInsideName_Refused', () => {
    for (const [name, character] of Object.entries(hidden)) {
        assert.equal(visibleName(`agent${character}one`), false, name);
    }
});

test('visibleName_OnlyWhitespaceOrDefaultIgnorable_Refused', () => {
    // None of these is in the replaced set, so only the visible-character test refuses them.
    for (const text of ['', ' ', '　', ' ', ' ', '️', '͏', '‍', '᠎', ' ️ ']) {
        assert.equal(visibleName(text), false, JSON.stringify(text));
    }
});

test('visibleName_OrdinaryNames_Accepted', () => {
    for (const text of ['Release bot', 'مرحبا', '你好', '👩‍💻 helper', 'x']) {
        assert.equal(visibleName(text), true, text);
    }
});

test('hasVisibleCharacter_WhitespaceOrIgnorableOnly_FalseOtherwiseTrue', () => {
    assert.equal(hasVisibleCharacter(' 　️‍'), false);
    assert.equal(hasVisibleCharacter('​'), false);
    assert.equal(hasVisibleCharacter(' a '), true);
    assert.equal(hasVisibleCharacter('\u0000'), true, 'a control is not whitespace; visibleName refuses it, this only asks for one visible character');
});

test('visibleExcerpt_HiddenCharactersAndWhitespaceRuns_OneSpaceBetweenWords', () => {
    assert.equal(visibleExcerpt('  bad‮request\u0000\u0000\n\t token­ ', 100), 'bad request token');
    assert.equal(visibleExcerpt('a​b', 100), 'a b', 'a hidden character becomes a space, so words never fuse');
});

test('visibleExcerpt_LoneSurrogate_BecomesReplacementCharacter', () => {
    assert.equal(visibleExcerpt('a\ud800b\udc00c', 100), 'a�b�c');
});

test('visibleExcerpt_CutInsideSurrogatePair_PairDroppedNotSplit', () => {
    const text = 'abc😀def';
    assert.equal(visibleExcerpt(text, 4), 'abc');
    assert.equal(visibleExcerpt(text, 5), 'abc😀');
    assert.equal(visibleExcerpt('abc', 3), 'abc');
    assert.equal(visibleExcerpt('abcd', 3), 'abc');
});

test('visibleExcerpt_CutEndsOnSpace_TrailingSpaceRemoved', () => {
    assert.equal(visibleExcerpt('abc def', 4), 'abc');
    assert.equal(visibleExcerpt('ab 😀', 4), 'ab', 'the pair is dropped, then the space before it');
});
