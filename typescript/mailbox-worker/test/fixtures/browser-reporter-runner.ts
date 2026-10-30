import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const { tests, stdout, stderr } = JSON.parse(await readFile(new URL('./diagnostic.json', import.meta.url), 'utf8'));
for (const message of stdout) console.log(message);
for (const message of stderr) console.error(message);

for (const { name, error } of tests) {
    test(name, () => {
        if (error) throw Object.assign(new Error(error.message), { code: error.code });
        assert.equal(1, 1);
    });
}
