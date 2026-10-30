import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { mcpClient, value } from './mcp-client.ts';

// Protocol fixtures only: no account server, database, vault or credential creation.
const syntheticControl='synthetic-test-only-'.repeat(4);
async function fixture(status=200){
    let calls=0;
    const server=createServer((req,res)=>{calls++;req.resume();res.writeHead(status,{'Content-Type':'application/json'});res.end('{"items":[]}');});
    server.listen(0,'127.0.0.1');await once(server,'listening');
    return {url:'http://127.0.0.1:'+server.address().port,get calls(){return calls;},async close(){server.closeAllConnections();await new Promise<void>(resolve=>server.close(()=>resolve()));}};
}
test('guide discovery reports missing prerequisites without making a network request',async()=>{
    const f=await fixture(),c=mcpClient(f.url);
    try{await c.initialize();const s=value(await c.call('demo_guide')).connection;
        assert.equal(s.state,'needs_owner_action');assert.equal(s.setupComplete,false);
        assert.equal(s.networkStatus,'not_checked');assert.equal(s.authenticationStatus,'not_checked');
        assert.equal(s.registrationStatus,'not_attempted');assert.equal(s.receiving.mode,'unsupported');
        assert.ok(s.blockers.some(b=>b.code==='credential_not_configured'));
        assert.equal(f.calls,0);
    }finally{await c.close();await f.close();}
});
test('injected control and authenticated response do not prove vault persistence or recovery',async()=>{
    const f=await fixture(),c=mcpClient(f.url,syntheticControl);
    try{await c.initialize();await c.call('list_addresses');const s=value(await c.call('demo_guide')).connection;
        assert.equal(s.networkStatus,'reachable');assert.equal(s.authenticationStatus,'authenticated');
        assert.equal(s.setupComplete,false);assert.equal(s.vaultPersistenceStatus,'not_verified');assert.equal(s.recoveryVerificationStatus,'not_verified');
        assert.equal(s.receiving.mode,'session_only');assert.equal(s.receiving.backgroundSupported,false);
        assert.ok(s.blockers.some(b=>b.code==='secure_vault_not_verified'));
        assert.ok(s.blockers.some(b=>b.code==='secure_secret_injection_not_verified'));
        assert.equal(s.registrationStatus,'not_attempted');assert.equal(f.calls,1);
        assert.ok(!c.logs.includes(syntheticControl));
    }finally{await c.close();await f.close();}
});
test('public discovery success never authenticates the configured account',async()=>{
    const f=await fixture(),c=mcpClient(f.url,syntheticControl);
    try{await c.initialize();await c.call('search_registry');const s=value(await c.call('demo_guide')).connection;
        assert.equal(s.networkStatus,'reachable');assert.equal(s.authenticationStatus,'not_checked');assert.equal(s.receiving.mode,'unsupported');
    }finally{await c.close();await f.close();}
});
test('403 blocks further network dispatch across tools in the same process',async()=>{
    const f=await fixture(403),c=mcpClient(f.url,syntheticControl);
    try{await c.initialize();const first=await c.call('search_registry');assert.equal(first.isError,true);
        const second=await c.call('list_addresses');assert.equal(second.isError,true);
        const s=value(await c.call('demo_guide')).connection;
        assert.equal(s.networkStatus,'access_denied');assert.equal(s.state,'needs_owner_action');
        assert.ok(s.blockers.some(b=>b.code==='access_denied'&&b.nextAction.includes('Do not retry')));
        assert.equal(f.calls,1);
        assert.equal((await c.call('prepare_payload',{text:'local only'})).isError,false);
    }finally{await c.close();await f.close();}
});
test('authentication failure is distinct from network failure and does not register',async()=>{
    const f=await fixture(401),c=mcpClient(f.url,syntheticControl);
    try{await c.initialize();assert.equal((await c.call('read_inbox')).isError,true);const s=value(await c.call('demo_guide')).connection;
        assert.equal(s.networkStatus,'reachable');assert.equal(s.authenticationStatus,'rejected');
        assert.equal(s.registrationStatus,'not_attempted');assert.equal(s.receiving.mode,'unsupported');assert.equal(f.calls,1);
    }finally{await c.close();await f.close();}
});
test('invalid response content cannot establish authenticated session receiving',async()=>{
    const server=createServer((req,res)=>{req.resume();res.writeHead(200,{'Content-Type':'text/html'});res.end('not an API response');});
    server.listen(0,'127.0.0.1');await once(server,'listening');const c=mcpClient('http://127.0.0.1:'+server.address().port,syntheticControl);
    try{await c.initialize();assert.equal((await c.call('read_inbox')).isError,true);const s=value(await c.call('demo_guide')).connection;
        assert.equal(s.authenticationStatus,'not_verified');assert.equal(s.receiving.mode,'unsupported');
    }finally{await c.close();server.closeAllConnections();await new Promise<void>(resolve=>server.close(()=>resolve()));}
});
test('transport failure records an unknown registration outcome without retrying',async()=>{
    let calls=0;const server=createServer(req=>{calls++;req.socket.destroy();});
    server.listen(0,'127.0.0.1');await once(server,'listening');const c=mcpClient('http://127.0.0.1:'+server.address().port,syntheticControl);
    try{await c.initialize();assert.equal((await c.call('register_account')).isError,true);const s=value(await c.call('demo_guide')).connection;
        assert.equal(s.networkStatus,'request_failed');assert.equal(s.registrationStatus,'outcome_unknown');assert.equal(s.setupComplete,false);assert.equal(calls,1);
    }finally{await c.close();server.closeAllConnections();await new Promise<void>(resolve=>server.close(()=>resolve()));}
});
test('application resource denials do not latch a global access denial',async()=>{
    let calls=0;const server=createServer((req,res)=>{calls++;req.resume();res.writeHead(calls===1?404:calls===2?409:200,{'Content-Type':'application/json'});res.end('{"items":[]}');});
    server.listen(0,'127.0.0.1');await once(server,'listening');const c=mcpClient('http://127.0.0.1:'+server.address().port,syntheticControl);
    try{await c.initialize();assert.equal((await c.call('get_contact',{accountId:'synthetic-peer'})).isError,true);
        assert.equal((await c.call('get_channel',{peerAccountId:'synthetic-peer'})).isError,true);
        assert.equal((await c.call('list_addresses')).isError,false);assert.equal(calls,3);
        assert.equal(value(await c.call('demo_guide')).connection.authenticationStatus,'authenticated');
    }finally{await c.close();server.closeAllConnections();await new Promise<void>(resolve=>server.close(()=>resolve()));}
});
test('registration HTTP response alone never upgrades durable setup evidence',async()=>{
    const f=await fixture(),c=mcpClient(f.url,syntheticControl);
    try{await c.initialize();assert.equal((await c.call('register_account')).isError,false);
        const s=value(await c.call('demo_guide')).connection;assert.equal(s.registrationStatus,'response_received');
        assert.equal(s.vaultPersistenceStatus,'not_verified');assert.equal(s.recoveryVerificationStatus,'not_verified');assert.equal(s.setupComplete,false);
        assert.equal(s.authenticationStatus,'not_checked');assert.equal(f.calls,1);
    }finally{await c.close();await f.close();}
});
test('an authenticated write rejection revokes prior session access evidence',async()=>{
    const server=createServer((req,res)=>{req.resume();res.writeHead(req.method==='GET'?200:401,{'Content-Type':'application/json'});res.end('{"items":[]}');});
    server.listen(0,'127.0.0.1');await once(server,'listening');const c=mcpClient('http://127.0.0.1:'+server.address().port,syntheticControl);
    try{await c.initialize();await c.call('list_addresses');assert.equal((await c.call('register_account')).isError,true);
        const s=value(await c.call('demo_guide')).connection;assert.equal(s.authenticationStatus,'rejected');assert.equal(s.receiving.mode,'unsupported');
        assert.equal(s.lastAuthenticatedRead.status,'authenticated');assert.equal(s.registrationStatus,'failed');
    }finally{await c.close();server.closeAllConnections();await new Promise<void>(resolve=>server.close(()=>resolve()));}
});
