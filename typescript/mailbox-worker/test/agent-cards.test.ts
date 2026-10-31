import assert from 'node:assert/strict';
import {test} from 'node:test';
import {build} from 'esbuild';
import {start,type AgentCredential} from './harness.ts';
import type {Env} from '../src/database.ts';
import {remoteMcp} from '../src/remote-mcp.ts';

const built=await build({entryPoints:['src/remote-private.ts'],bundle:true,write:false,platform:'node',format:'esm',logLevel:'silent'});
const {executePrivateMcp}=await import('data:text/javascript;base64,'+Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');
const origin='https://demo.test';
type Harness=Awaited<ReturnType<typeof start>>;
const environment=(h:Harness):Env=>({db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
    REGISTRATION_PER_IP_PER_HOUR:'100',REGISTRATION_GLOBAL_PER_HOUR:'1000',CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'});
const call=async(h:Harness,owner:AgentCredential,name:string,args:Record<string,unknown>)=>{
    const response=await executePrivateMcp(environment(h),origin,owner.accountId,name,args);
    return {status:response.status,body:await response.json()};
};
const ok=async(h:Harness,owner:AgentCredential,name:string,args:Record<string,unknown>)=>{
    const answer=await call(h,owner,name,args);
    assert.equal(answer.status,200,name+': '+JSON.stringify(answer.body));
    return answer.body;
};
const card=(overrides:Record<string,unknown>={})=>({name:'Research helper',description:'Answers literature questions.',version:'1.0.0',
    supportedInterfaces:[{url:'https://agent.example/a2a',protocolBinding:'JSONRPC',protocolVersion:'1.0'}],
    skills:[{id:'search',name:'Search',description:'Find papers.',tags:['research']}],
    defaultInputModes:['text/plain'],defaultOutputModes:['text/plain'],capabilities:{streaming:false},...overrides});
const publicAddress=async(h:Harness,owner:AgentCredential,label='Public')=>{
    const addressId=crypto.randomUUID();
    await ok(h,owner,'ensure_my_address',{addressId,label,isPublic:true});
    return addressId;
};
const count=async(h:Harness,sql:string,...args:string[])=>Number(await h.db.prepare(sql).bind(...args).first('n'));
const cards=(h:Harness)=>count(h,'SELECT count(*) n FROM address_agent_cards');

test('ensureAddress_IsPublicTrue_ListedInDirectory',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=crypto.randomUUID();
        const created=await ok(h,a,'ensure_my_address',{addressId,label:'Open door',isPublic:true});
        assert.equal(created.isPublic,true);
        const listed=await (await h.request('/v1/directory?q=Open')).json();
        assert.ok(listed.items.some((item:{addressId:string})=>item.addressId===addressId));
        const hidden=crypto.randomUUID();
        await ok(h,a,'ensure_my_address',{addressId:hidden,label:'Quiet'});
        assert.equal((await h.request('/v1/directory/address?addressId='+hidden)).status,404,'isPublic defaults to false');
    }finally{await h.close();}
});

test('ensureAddress_PublicOverLimit_429',async()=>{
    const h=await start();
    try{
        const a=await h.register();
        await publicAddress(h,a);
        const second=await call(h,a,'ensure_my_address',{addressId:crypto.randomUUID(),isPublic:true});
        assert.equal(second.status,429);
        assert.equal(await count(h,'SELECT count(*) n FROM account_addresses WHERE account_id=?',a.accountId),1,'the refused address is not created');
    }finally{await h.close();}
});

test('publishCard_PublicOwnAddress_ServedAtWellKnownPath',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        const published=await ok(h,a,'publish_my_agent_card',{addressId,card:card()});
        assert.deepEqual(published,{addressId,agentCard:card()});
        const served=await h.request('/v1/directory/address/'+addressId+'/agent-card.json');
        assert.equal(served.status,200);
        assert.match(served.headers.get('Content-Type')??'',/^application\/json/);
        assert.equal(served.headers.get('Cache-Control'),'no-store');
        assert.equal(served.headers.get('X-Content-Type-Options'),'nosniff');
        assert.deepEqual(await served.json(),card());
        const replaced=await ok(h,a,'publish_my_agent_card',{addressId,card:card({name:'Renamed'})});
        assert.equal(replaced.agentCard.name,'Renamed');
        assert.equal((await (await h.request('/v1/directory/address/'+addressId+'/agent-card.json')).json()).name,'Renamed');
        assert.equal(await cards(h),1,'a card is stored per address and replaced');
    }finally{await h.close();}
});

test('publishCard_UnlistedAddress_RefusedNoChange',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=crypto.randomUUID();
        await ok(h,a,'ensure_my_address',{addressId,label:'Private'});
        const revision=await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
        const refused=await call(h,a,'publish_my_agent_card',{addressId,card:card()});
        assert.equal(refused.status,400);
        assert.equal(await cards(h),0);
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'),revision);
    }finally{await h.close();}
});

test('publishCard_OtherAgentsAddress_NotFound',async()=>{
    const h=await start();
    try{
        const a=await h.register(),b=await h.register(),addressId=await publicAddress(h,a);
        const revision=await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
        assert.equal((await call(h,b,'publish_my_agent_card',{addressId,card:card()})).status,404);
        assert.equal((await call(h,b,'publish_my_agent_card',{addressId:crypto.randomUUID(),card:card()})).status,404);
        assert.equal((await call(h,b,'publish_my_agent_card',{addressId,card:null})).status,404,'another agent cannot remove a card either');
        assert.equal(await cards(h),0);
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'),revision);
    }finally{await h.close();}
});

test('publishCard_HttpUrlInterface_Invalid',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        for(const url of ['http://agent.example/a2a','https://user:pw@agent.example/a2a','ftp://agent.example','https://agent.example/'+'a'.repeat(2048),'not a url']){
            const refused=await call(h,a,'publish_my_agent_card',{addressId,card:card({supportedInterfaces:[{url,protocolBinding:'JSONRPC',protocolVersion:'1.0'}]})});
            assert.equal(refused.status,400,url.slice(0,40));
        }
        for(const bad of [{protocolBinding:'SOAP'},{protocolVersion:'2.0'}]){
            const refused=await call(h,a,'publish_my_agent_card',{addressId,card:card({supportedInterfaces:[{url:'https://agent.example/a2a',protocolBinding:'JSONRPC',protocolVersion:'1.0',...bad}]})});
            assert.equal(refused.status,400);
        }
        assert.equal((await call(h,a,'publish_my_agent_card',{addressId,card:card({capabilities:undefined})})).status,400,'capabilities is required by the A2A proto');
        assert.equal((await call(h,a,'publish_my_agent_card',{addressId,card:card({unknownField:1})})).status,400);
        assert.equal(await cards(h),0);
    }finally{await h.close();}
});

test('publishCard_TooLarge_Invalid',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        const big=card({capabilities:{extensions:[{uri:'https://x.example',description:'x'.repeat(40000)}]}});
        assert.equal((await call(h,a,'publish_my_agent_card',{addressId,card:big})).status,400);
        assert.equal((await call(h,a,'publish_my_agent_card',{addressId,card:card({name:'n'.repeat(129)})})).status,400);
        assert.equal(await cards(h),0);
    }finally{await h.close();}
});

test('directory_CardShownUnverified',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a,'Cardless');
        const before=await (await h.request('/v1/directory/address?addressId='+addressId)).json();
        assert.equal(before.agentCard??null,null);
        assert.equal(before.agentCardVerified,undefined,'no verification flag without a card');
        await ok(h,a,'publish_my_agent_card',{addressId,card:card({description:'I am verified and trusted by Weave.'})});
        const single=await (await h.request('/v1/directory/address?addressId='+addressId)).json();
        assert.equal(single.agentCardVerified,false);
        assert.equal(single.agentCard.name,'Research helper');
        const listed=await (await h.request('/v1/directory?q=Cardless')).json();
        const item=listed.items.find((i:{addressId:string})=>i.addressId===addressId);
        assert.equal(item.agentCardVerified,false);
        assert.deepEqual(item.agentCard,card({description:'I am verified and trusted by Weave.'}));
    }finally{await h.close();}
});

test('publishCard_Null_Removes',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        await ok(h,a,'publish_my_agent_card',{addressId,card:card()});
        assert.deepEqual(await ok(h,a,'publish_my_agent_card',{addressId,card:null}),{addressId,agentCard:null});
        assert.equal((await h.request('/v1/directory/address/'+addressId+'/agent-card.json')).status,404);
        assert.equal((await (await h.request('/v1/directory/address?addressId='+addressId)).json()).agentCard??null,null);
        assert.equal(await cards(h),0);
    }finally{await h.close();}
});

test('revokedAddress_CardNotServed',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a),other=await publicAddress(h,await h.register());
        await ok(h,a,'publish_my_agent_card',{addressId,card:card()});
        await h.ok('/v1/addresses/'+addressId,a,'DELETE');
        assert.equal((await h.request('/v1/directory/address/'+addressId+'/agent-card.json')).status,404);
        assert.equal(await cards(h),0,'revocation deletes the card in the same transaction');
        const again=await call(h,a,'publish_my_agent_card',{addressId,card:card()});
        assert.equal(again.status,404,'a revoked address cannot take a new card');
        assert.equal((await h.request('/v1/directory/address/'+other+'/agent-card.json')).status,404,'an address without a card has none');
    }finally{await h.close();}
});

test('madeNonPublic_CardNotServedAndNotResurrected',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        await ok(h,a,'publish_my_agent_card',{addressId,card:card()});
        await h.ok('/v1/addresses/'+addressId,a,'PUT',{isPublic:false});
        assert.equal((await h.request('/v1/directory/address/'+addressId+'/agent-card.json')).status,404);
        await h.ok('/v1/addresses/'+addressId,a,'PUT',{isPublic:true});
        assert.equal((await h.request('/v1/directory/address/'+addressId+'/agent-card.json')).status,404,'republishing the address does not bring back the old card');
    }finally{await h.close();}
});

test('servedCard_RowOutlivingPublicAddress_FilteredAtRead',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        await ok(h,a,'publish_my_agent_card',{addressId,card:card({capabilities:{extensions:[{uri:'https://ext.example/x',params:{agentId:'declared'}}]}})});
        const served=await (await h.request('/v1/directory/address/'+addressId+'/agent-card.json')).json();
        assert.deepEqual(served.capabilities.extensions[0].params,{agentId:'declared'},'agent-declared keys are served exactly as stored');
        await h.db.prepare('UPDATE account_addresses SET revoked_at=CAST(? AS INTEGER) WHERE address_id=?').bind((BigInt(Date.now())*10000n).toString(),addressId).run();
        assert.equal(await cards(h),1,'the row is still stored');
        assert.equal((await h.request('/v1/directory/address/'+addressId+'/agent-card.json')).status,404,'a stored card is not served for a non-public address');
        assert.equal((await h.request('/v1/directory/address?addressId='+addressId)).status,404);
    }finally{await h.close();}
});

const fullCard=()=>card({
    capabilities:{streaming:true,pushNotifications:false,extendedAgentCard:false,extensions:[{uri:'https://ext.example/x',description:'Extra',required:false,params:{a:{b:{c:1}},list:['x']}}]},
    securitySchemes:{
        key:{apiKeySecurityScheme:{description:'Header key',location:'header',name:'X-Key'}},
        web:{httpAuthSecurityScheme:{scheme:'Bearer',bearerFormat:'JWT'}},
        oauth:{oauth2SecurityScheme:{flows:{authorizationCode:{authorizationUrl:'https://auth.example/a',tokenUrl:'https://auth.example/t',refreshUrl:'https://auth.example/r',scopes:{read:'Read access'},pkceRequired:true}},oauth2MetadataUrl:'https://auth.example/.well-known/m'}},
        oidc:{openIdConnectSecurityScheme:{openIdConnectUrl:'https://auth.example/.well-known/openid-configuration'}},
        tls:{mtlsSecurityScheme:{description:'Client certificate'}}},
    securityRequirements:[{schemes:{oauth:{list:['read']},tls:{list:[]}}}]});

test('publishCard_FullCardWithSecurity_StoredAsDeclared',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        const published=await ok(h,a,'publish_my_agent_card',{addressId,card:fullCard()});
        assert.deepEqual(published.agentCard,fullCard());
        assert.deepEqual(await (await h.request('/v1/directory/address/'+addressId+'/agent-card.json')).json(),fullCard());
    }finally{await h.close();}
});

test('publishCard_UnacceptableContent_InvalidNoChange',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        const deep={a:{b:{c:{d:{e:1}}}}};
        const manyKeys=Object.fromEntries(Array.from({length:65},(_,i)=>['k'+i,i]));
        const big=Object.fromEntries(['a','b','c','d','e'].map(key=>[key,'x'.repeat(1000)]));
        const ext=(params:unknown)=>card({capabilities:{extensions:[{uri:'https://ext.example/x',params}]}});
        const scheme=(value:unknown)=>card({securitySchemes:{x:value}});
        const bad:[string,Record<string,unknown>][]=[
            ['unknown capability member',card({capabilities:{streaming:true,teleport:true}})],
            ['non-boolean capability',card({capabilities:{streaming:'yes'}})],
            ['http extension uri',card({capabilities:{extensions:[{uri:'http://ext.example/x'}]}})],
            ['over-deep params',ext(deep)],
            ['too many params members',ext(manyKeys)],
            ['large params',ext(big)],
            ['http tokenUrl',scheme({oauth2SecurityScheme:{flows:{clientCredentials:{tokenUrl:'http://auth.example/t',scopes:{}}}}})],
            ['unknown scheme kind',scheme({kerberosSecurityScheme:{}})],
            ['two scheme kinds',scheme({mtlsSecurityScheme:{},httpAuthSecurityScheme:{scheme:'Basic'}})],
            ['unknown scheme member',scheme({mtlsSecurityScheme:{extra:'1'}})],
            ['bad scheme name',card({securitySchemes:{'bad name':{mtlsSecurityScheme:{}}}})],
            ['control char in scheme description',scheme({mtlsSecurityScheme:{description:'a\u0007b'}})],
            ['missing required scheme member',scheme({apiKeySecurityScheme:{name:'X'}})],
            ['requirement naming a missing scheme',card({securitySchemes:{x:{mtlsSecurityScheme:{}}},securityRequirements:[{schemes:{y:{list:[]}}}]})],
            ['requirement with no schemes declared',card({securityRequirements:[{schemes:{y:{list:[]}}}]})],
            ['too many requirements',card({securitySchemes:{x:{mtlsSecurityScheme:{}}},securityRequirements:Array.from({length:17},()=>({}))})],
            ['control char in description',card({description:'bad\u0000text'})]
        ];
        const revision=await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
        for(const [name,value] of bad)assert.equal((await call(h,a,'publish_my_agent_card',{addressId,card:value})).status,400,name);
        assert.equal(await cards(h),0);
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'),revision,'nothing was written');
    }finally{await h.close();}
});

test('publishCard_NonCanonicalHttpsForms_Invalid',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        const urls=['https:evil.example','https:\\evil.example','https:/evil.example','https://','https://evil.example\\@good.example','HTTPS://agent.example/a2a'];
        for(const url of urls){
            const refused=await call(h,a,'publish_my_agent_card',{addressId,card:card({supportedInterfaces:[{url,protocolBinding:'JSONRPC',protocolVersion:'1.0'}]})});
            assert.equal(refused.status,400,url);
        }
        const viaScheme=await call(h,a,'publish_my_agent_card',{addressId,card:card({securitySchemes:{o:{openIdConnectSecurityScheme:{openIdConnectUrl:'https:evil.example'}}}})});
        assert.equal(viaScheme.status,400);
        assert.equal(await cards(h),0);
        const normalized=await ok(h,a,'publish_my_agent_card',{addressId,card:card({supportedInterfaces:[{url:'https://Agent.Example',protocolBinding:'JSONRPC',protocolVersion:'1.0'}]})});
        assert.equal(normalized.agentCard.supportedInterfaces[0].url,'https://agent.example/','a valid URL is stored in its normalized form');
    }finally{await h.close();}
});

test('publicMcp_LookupAndSearch_ReturnCardUnverified',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a,'Searchable');
        await ok(h,a,'publish_my_agent_card',{addressId,card:card()});
        const read=(url:URL)=>h.request(url.pathname+url.search);
        const run=async(name:string,args:Record<string,unknown>)=>(await (await remoteMcp(new Request(origin+'/mcp',{method:'POST',
            headers:{'Content-Type':'application/json',Accept:'application/json, text/event-stream'},
            body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/call',params:{name,arguments:args}})}),read)).json()).result.structuredContent;
        const found=await run('lookup_address',{addressId});
        assert.deepEqual(found.agentCard,card());
        assert.equal(found.agentCardVerified,false);
        const listed=(await run('search_registry',{q:'Searchable'})).items.find((i:{addressId:string})=>i.addressId===addressId);
        assert.deepEqual(listed.agentCard,card());
        assert.equal(listed.agentCardVerified,false);
    }finally{await h.close();}
});

test('cardRoute_PublicReadLimitExceeded_429WithRetryAfter',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        await ok(h,a,'publish_my_agent_card',{addressId,card:card()});
        const path='/v1/directory/address/'+addressId+'/agent-card.json';
        assert.equal((await h.request(path)).status,200);
        await h.db.prepare("UPDATE rate_limits SET count=2000 WHERE policy='public-read' AND bucket_key='global'").run();
        const limited=await h.request(path);
        assert.equal(limited.status,429);
        assert.ok(Number(limited.headers.get('retry-after'))>0);
    }finally{await h.close();}
});

test('publishCard_WhitespaceInIdentifierMembers_InvalidButDescriptionsMayWrap',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        const c=(...codes:number[])=>String.fromCharCode(...codes);
        const scheme=(value:unknown)=>card({securitySchemes:{x:value}});
        const bad:[string,Record<string,unknown>][]=[
            ['apiKey name with CRLF',scheme({apiKeySecurityScheme:{location:'header',name:'X-Key'+c(13,10)+'Set-Cookie: a=b'}})],
            ['apiKey location body',scheme({apiKeySecurityScheme:{location:'body',name:'X-Key'}})],
            ['http scheme with tab',scheme({httpAuthSecurityScheme:{scheme:'Bea'+c(9)+'rer'}})],
            ['bearerFormat with LF',scheme({httpAuthSecurityScheme:{scheme:'Bearer',bearerFormat:'J'+c(10)+'WT'}})],
            ['scope name with LF',scheme({oauth2SecurityScheme:{flows:{clientCredentials:{tokenUrl:'https://auth.example/t',scopes:{['a'+c(10)+'b']:'x'}}}}})],
            ['scope value with CR',scheme({oauth2SecurityScheme:{flows:{clientCredentials:{tokenUrl:'https://auth.example/t',scopes:{a:'x'+c(13)}}}}})],
            ['params key with LF',card({capabilities:{extensions:[{uri:'https://ext.example/x',params:{['k'+c(10)]:1}}]}})],
            ['params value with tab',card({capabilities:{extensions:[{uri:'https://ext.example/x',params:{k:'a'+c(9)+'b'}}]}})],
            ['tag with LF',card({skills:[{id:'s',name:'S',description:'d',tags:['a'+c(10)+'b']}]})],
            ['name with LF',card({name:'a'+c(10)+'b'})],
            ['description with CR',card({description:'a'+c(13,10)+'b'})],
            ['scheme description with CR',scheme({mtlsSecurityScheme:{description:'a'+c(13)+'b'}})]
        ];
        for(const [name,value] of bad)assert.equal((await call(h,a,'publish_my_agent_card',{addressId,card:value})).status,400,name);
        assert.equal(await cards(h),0);
        const wrapped=card({description:'line one'+c(10)+'line two',skills:[{id:'s',name:'S',description:'a'+c(10)+'b',tags:[]}],
            securitySchemes:{x:{apiKeySecurityScheme:{description:'a'+c(10)+'b',location:'cookie',name:'sid'}}}});
        assert.deepEqual((await ok(h,a,'publish_my_agent_card',{addressId,card:wrapped})).agentCard,wrapped);
    }finally{await h.close();}
});

test('publishCard_UrlOverLimitOnceNormalized_Invalid',async()=>{
    const h=await start();
    try{
        const a=await h.register(),addressId=await publicAddress(h,a);
        const url='https://agent.example/'+'é'.repeat(500);
        assert.ok(url.length<2048&&new TextEncoder().encode(url).length<2048,'the raw input is within the limit');
        const refused=await call(h,a,'publish_my_agent_card',{addressId,card:card({supportedInterfaces:[{url,protocolBinding:'JSONRPC',protocolVersion:'1.0'}]})});
        assert.equal(refused.status,400);
        assert.equal(await cards(h),0);
    }finally{await h.close();}
});
