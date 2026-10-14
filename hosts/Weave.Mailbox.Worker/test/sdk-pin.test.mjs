import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
const root=new URL('../../../',import.meta.url);
test('repository SDK selection accepts stable .NET 10 feature bands without crossing majors',()=>{
    const config=JSON.parse(readFileSync(new URL('global.json',root),'utf8'));
    assert.equal(config.sdk.version,'10.0.100');
    assert.equal(config.sdk.rollForward,'latestMinor');
    assert.equal(config.sdk.allowPrerelease,false);
    assert.equal(config.test.runner,'Microsoft.Testing.Platform');
});
test('shared CI setup installs the .NET 10 channel with the requested quality and retains lock caching',()=>{
    const action=readFileSync(new URL('.github/actions/setup-dotnet/action.yml',root),'utf8');
    assert.doesNotMatch(action,/global-json-file:/);
    const input=/^  dotnet-version:\n([\s\S]*?)(?=^  \w|^runs:)/m.exec(action)?.[1];
    assert.ok(input);
    assert.match(input,/^    default: '10\.x'$/m);
    assert.match(action,/dotnet-version: \$\{\{ inputs\.dotnet-version \}\}/);
    assert.match(action,/dotnet-quality: \$\{\{ inputs\.dotnet-quality \}\}/);
    const quality=/^  dotnet-quality:\n([\s\S]*?)(?=^  \w|^runs:)/m.exec(action)?.[1];
    assert.match(quality,/^    default: 'ga'$/m);
    assert.match(action,/cache-dependency-path: '\*\*\/packages\.lock\.json'/);
    const publish=readFileSync(new URL('.github/workflows/publish-nuget.yml',root),'utf8');
    assert.match(publish,/dotnet-version: '10\.x'/);
    assert.match(publish,/dotnet-quality: 'ga'/);
});
