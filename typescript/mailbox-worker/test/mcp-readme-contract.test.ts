import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { tools } from '../../../examples/mailbox-demo-mcp/tools.ts';

const readme = readFileSync(new URL('../../../examples/mailbox-demo-mcp/README.md', import.meta.url), 'utf8');
for (const name of ['request_contact', 'send_message']) {
    test(`README ${name} step supplies every advertised required argument`, () => {
        const step = readme.split('\n').find(line => /^\d+\. /.test(line) && line.includes('`' + name + '`'));
        assert.ok(step, `Missing numbered workflow step for ${name}`);
        const schema = tools.find(tool => tool.name === name)!.inputSchema;
        assert.equal(schema.type, 'object');
        if (schema.type !== 'object') throw Error('Expected an object schema');
        for (const field of schema.required)
            assert.ok(step.includes('`' + field + '`'), `${name} workflow omits required argument ${field}`);
    });
}
