import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
const root=new URL('../../../',import.meta.url);
test('repository SDK selection is exact so servicing releases cannot alter locked implicit dependencies',()=>{
    const config=JSON.parse(readFileSync(new URL('global.json',root),'utf8'));
    assert.match(config.sdk.version,/^\d+\.\d+\.\d+$/);
    assert.equal(config.sdk.rollForward,'disable');
    assert.equal(config.sdk.allowPrerelease,false);
    assert.equal(config.test.runner,'Microsoft.Testing.Platform');
});
test('shared CI setup always installs the SDK selected by global.json without a floating default',()=>{
    const action=readFileSync(new URL('.github/actions/setup-dotnet/action.yml',root),'utf8');
    assert.match(action,/^        global-json-file: global\.json$/m);
    const input=/^  dotnet-version:\n([\s\S]*?)(?=^  \w|^runs:)/m.exec(action)?.[1];
    assert.ok(input);
    assert.match(input,/^    default: ''$/m);
    assert.match(action,/cache-dependency-path: '\*\*\/packages\.lock\.json'/);
});
