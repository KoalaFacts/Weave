import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.mjs';
test('relay endpoint defaults to the real request origin and ignores forwarded host headers',async()=>{
    const h=await start();
    try {
        const a=await h.register();
        const response=await h.mf.dispatchFetch('https://actual.example.test/v1/addresses',{method:'POST',headers:{'X-Weave-Mailbox-Control':a.control,'Content-Type':'application/json','Forwarded':'host=attacker.example.test;proto=http','X-Forwarded-Host':'attacker.example.test','X-Forwarded-Proto':'http'},body:'{}'});
        assert.equal(response.status,200);const address=await response.json();
        assert.equal((await h.ok('/v1/contacts/card?cardId='+address.addressId,a.control)).methods[0].endpoint,'https://actual.example.test');
    } finally {await h.close();}
});
test('stale host bindings cannot replace the request origin or prevent registration',async()=>{
    for(const canonical of ['https://canonical.example.test','http://external.example.test','https://canonical.example.test/path','https://user:pass@canonical.example.test']){
        const h=await start({bindings:{WEAVE_HOST:canonical}});
        try {
            const a=await h.register();const response=await h.request('/v1/addresses',a.control,'POST',{});
            assert.equal(response.status,200);const address=await response.json();
            assert.equal((await h.ok('/v1/contacts/card?cardId='+address.addressId,a.control)).methods[0].endpoint,'http://localhost');
        } finally {await h.close();}
    }
});
