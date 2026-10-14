import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { start, payload, secret } from './harness.mjs';

const source = await readFile('src/openapi.json','utf8');
const spec = JSON.parse(source);
const resolve = value => value.$ref ? value.$ref.slice(2).split('/').reduce((v,key) => v[key],spec) : value;

function responseShape(operation, value) {
    const schema = resolve(operation.responses['200'].content['application/json'].schema);
    assert.equal(schema.additionalProperties,false);
    assert.deepEqual(Object.keys(value).sort(),schema.required?.toSorted() ?? []);
}

test('OpenAPI is an anonymous no-store JSON asset independent of account storage', async () => {
    const h = await start({migrate:false,bindings:{REGISTRATION_ENABLED:'false'}});
    try {
        const response = await h.request('/openapi.json');
        assert.equal(response.status,200);
        assert.equal(response.headers.get('content-type'),'application/json');
        assert.equal(response.headers.get('cache-control'),'no-store');
        assert.equal(response.headers.get('x-content-type-options'),'nosniff');
        assert.equal(await response.text(),source);
        assert.equal((await h.request('/openapi.json','fictional-static-header-'.repeat(4))).status,200);
        assert.equal((await h.request('/openapi.json','short')).status,401);
        assert.equal((await h.mf.dispatchFetch('http://example.test/openapi.json')).status,400);
        assert.equal((await h.request('/openapi.json',null,'POST',{})).status,401);
    } finally { await h.close(); }
});

test('OpenAPI describes the current HTTP method/path set with resolved local references and security', () => {
    const routes = {
        '/v1/accounts':['post'], '/v1/contacts':['get'], '/v1/contacts/accounts/{accountId}':['get','delete'], '/v1/addresses':['get','post'], '/v1/addresses/{addressId}':['get','put','delete'],
        '/v1/directory':['get'], '/v1/directory/address':['get'], '/v1/contacts/cards':['get','put'],
        '/v1/contacts/owned-cards':['get'], '/v1/contacts/card':['get'], '/v1/contacts/requests':['get','post'],
        '/v1/contacts/requests/{requestId}':['get'], '/v1/contacts/requests/{requestId}/decision':['post'],
        '/v1/contacts/channel':['get'], '/v1/contacts/blocks':['put','delete'], '/v1/messages':['post'],
        '/v1/inbox':['get'], '/v1/outbox':['get'], '/v1/outbox/{messageId}':['get'], '/v1/inbox/{messageId}/ack':['post'],
        '/v1/inbox/events':['get'], '/inbox/{accountId}':['get'], '/outbox/{accountId}':['get','post']
    };
    assert.equal(spec.openapi,'3.1.1');
    assert.deepEqual(spec.servers.map(x => x.url),['/']);
    assert.deepEqual(Object.keys(spec.paths).sort(),Object.keys(routes).sort());
    const ids = new Set();
    for (const [path,methods] of Object.entries(routes)) {
        assert.deepEqual(Object.keys(spec.paths[path]).sort(),methods.toSorted(),path);
        for (const method of methods) {
            const operation = spec.paths[path][method];
            assert.equal(ids.has(operation.operationId),false);
            ids.add(operation.operationId);
            const anonymous = method === 'get' && ['/v1/directory','/v1/directory/address','/v1/contacts/cards','/v1/contacts/card'].includes(path);
            assert.deepEqual(operation.security ?? spec.security,anonymous ? [{},{AccountControl:[]},{MailboxControl:[]}] : [{AccountControl:[]},{MailboxControl:[]}]);
            const expectedPath = [...path.matchAll(/\{([^}]+)\}/g)].map(x => x[1]).sort();
            const actualPath = (operation.parameters ?? []).map(resolve).filter(x => x.in === 'path');
            assert.deepEqual(actualPath.map(x => x.name).sort(),expectedPath);
            assert.ok(actualPath.every(x => x.required));
        }
    }
    function refs(value) {
        if (!value || typeof value !== 'object') return;
        if (value.$ref) { assert.ok(value.$ref.startsWith('#/')); assert.ok(resolve(value),value.$ref); }
        for (const child of Object.values(value)) refs(child);
    }
    refs(spec);
    assert.deepEqual(spec.components.securitySchemes.MailboxControl.type,'apiKey');
    assert.equal(spec.components.securitySchemes.MailboxControl.name,'X-Weave-Mailbox-Control');
    assert.equal(spec.components.securitySchemes.MailboxControl.in,'header');
    assert.equal(spec.components.securitySchemes.AccountControl.name,'X-Weave-Account-Control');
    assert.match(source,/"maximum": 9223372036854775807\b/);
    assert.doesNotMatch(source,/9223372036854776000/);
    assert.equal(spec.paths['/v1/inbox/events'].get.responses['200'].content['text/event-stream'].schema.type,'string');
});

test('shared v1 field, required, enum and reference contracts stay aligned with the original schema', async () => {
    const original = JSON.parse((await readFile('../../protocol/weave-mailbox/v1/mailbox.schema.json','utf8')).replaceAll('#/$defs/','#/components/schemas/'));
    const shapeKeys = ['type','properties','additionalProperties','required','enum','const','$ref','items','anyOf'];
    function shape(value, properties = false) {
        if (Array.isArray(value)) return value.map(x => shape(x));
        if (!value || typeof value !== 'object') return value;
        return Object.fromEntries(Object.entries(value).filter(([key]) => properties || shapeKeys.includes(key))
            .map(([key,v]) => [key,shape(v,key === 'properties')]));
    }
    for(const [name,schema] of Object.entries(original.$defs)){
        const actual=spec.components.schemas[name==='messageSubmission'?'messageSubmissionV1':name==='contactSubmission'?'contactSubmissionV1':name];
        assert.equal(actual.type,schema.type,name);
        assert.equal(actual.additionalProperties,schema.additionalProperties,name);
        for(const [field,value] of Object.entries(schema.properties??{}))assert.deepEqual(shape(actual.properties[field]),shape(value),name+'.'+field);
        for(const required of schema.required??[])if(!['recipientMailboxId','requesterMailboxId','senderMailboxId','peerMailboxId'].includes(required))assert.ok(actual.required.includes(required),name+'.'+required);
    }
    assert.equal(spec.components.schemas.messageSubmissionV2.properties.version.const,2);
    assert.ok(spec.components.schemas.messageSubmissionV2.required.includes('connectionToken'));
    assert.ok(spec.components.schemas.contactSubmissionV2.required.includes('requesterAddressId'));
    assert.ok(spec.components.schemas.contactSubmissionV2.required.includes('recipientAccountId'));
    for (const name of ['accountSubmission','addressSubmission']) {
        assert.equal(spec.components.schemas[name].additionalProperties,false);
        assert.deepEqual(spec.components.schemas[name].required ?? [],[]);
    }
    assert.deepEqual(spec.components.schemas.contactDecision.required.toSorted(),['expectedGeneration','reply','status']);
    assert.deepEqual(spec.components.schemas.contactSummary.properties.status.enum,['pending','needsAction','accepted','rejected','expired']);
    assert.equal(spec.components.schemas.contactDecision.properties.status.enum.includes('expired'),false);
    assert.match(spec.components.schemas.payload.properties.messageId.pattern,/7\[/);
});

test('documented account/address/contact/message/ACK responses match real Worker projections', async () => {
    const h = await start();
    try {
        const a = await h.register(), b = await h.register();
        responseShape(spec.paths['/v1/accounts'].post,await h.ok('/v1/accounts',a.control,'POST',{}));
        const address = await h.address(b);
        responseShape(spec.paths['/v1/addresses'].post,address);
        responseShape(spec.paths['/v1/addresses'].get,await h.ok('/v1/addresses',b.control));
        assert.deepEqual(await h.ok('/v1/directory'),{items:[],nextCursor:null});
        const introduction = payload('hello');
        const request = {requestId:introduction.messageId,cardId:address.addressId,methodId:'relay',payload:introduction};
        const relation = await h.ok('/v1/contacts/requests',a.control,'POST',request);
        responseShape(spec.paths['/v1/contacts/requests'].post,relation);
        responseShape(spec.paths['/v1/contacts/requests/{requestId}'].get,relation.request);
        const accepted = await h.ok('/v1/contacts/requests/'+request.requestId+'/decision',b.control,'POST',{
            requesterMailboxId:a.agentId,expectedGeneration:relation.generation,status:'accepted',reply:null
        });
        assert.equal(accepted.isConnected,true);
        const envelope = {version:1,recipientMailboxId:b.agentId,contactGeneration:accepted.generation,payload:payload('ping')};
        const receipt = await h.ok('/v1/messages',a.control,'POST',envelope);
        responseShape(spec.paths['/v1/messages'].post,receipt);
        responseShape(spec.paths['/v1/inbox'].get,await h.ok('/v1/inbox',b.control));
        const ack = await h.ok('/v1/inbox/'+receipt.messageId+'/ack',b.control,'POST',{senderMailboxId:a.agentId});
        responseShape(spec.paths['/v1/inbox/{messageId}/ack'].post,ack);
        assert.equal(ack.state,'acknowledged');
        assert.deepEqual(await h.ok('/v1/outbox/'+receipt.messageId,a.control),ack);
        const pong = {version:1,recipientMailboxId:a.agentId,contactGeneration:accepted.generation,payload:payload('pong')};
        await h.ok('/v1/messages',b.control,'POST',pong);
        assert.deepEqual((await h.ok('/v1/inbox',a.control)).items,[{senderMailboxId:b.agentId,senderAccountId:b.agentId,envelope:{...pong,recipientAccountId:a.agentId}}]);
        const pongAck = await h.ok('/v1/inbox/'+pong.payload.messageId+'/ack',a.control,'POST',{senderMailboxId:b.agentId});
        assert.equal(pongAck.state,'acknowledged');
        assert.deepEqual(await h.ok('/v1/outbox/'+pong.payload.messageId,b.control),pongAck);
        for (const invalid of [{...request,unknown:true},{...request,requestId:payload('different',Date.now()-60000).messageId}]) {
            const response = await h.request('/v1/contacts/requests',a.control,'POST',invalid);
            assert.equal(response.status,400);
            assert.deepEqual(await response.json(),{status:400,title:'Invalid request',code:'invalid'});
        }
    } finally { await h.close(); }
});

test('429 contract distinguishes public-rate Retry-After from registration capacity', async () => {
    const h = await start({bindings:{REGISTRATION_MAX_ACCOUNTS:'1'}});
    try {
        await h.register();
        const capacity = await h.request('/v1/accounts',secret(),'POST',{});
        assert.equal(capacity.status,429);
        assert.equal(capacity.headers.get('retry-after'),null);
        assert.deepEqual(await capacity.json(),{status:429,title:'Capacity reached',code:'capacity'});
        assert.equal(spec.components.responses['429'].headers['Retry-After'],undefined);
        await h.ok('/v1/directory');
        await h.db.prepare("UPDATE rate_limits SET count=2000 WHERE policy='public-read' AND bucket_key='global'").run();
        const rate = await h.request('/v1/directory');
        assert.equal(rate.status,429);
        assert.ok(Number(rate.headers.get('retry-after')) > 0);
        assert.deepEqual(await rate.json(),{status:429,title:'Capacity reached',code:'capacity'});
        assert.equal(spec.components.responses.publicRate.headers['Retry-After'].required,true);
    } finally { await h.close(); }
});

test('503 maintenance response has its own exact contract without expanding shared v1 error codes', async () => {
    const h=await start({migrate:false,bindings:{MAINTENANCE_MODE:'true'}});
    try {
        const response=await h.request('/v1/directory');
        assert.equal(response.status,503);
        assert.equal(response.headers.get('retry-after'),'30');
        const value=await response.json(),schema=spec.components.schemas.maintenanceProblem;
        assert.deepEqual(value,{status:503,title:'Maintenance in progress',code:'maintenance'});
        assert.equal(schema.additionalProperties,false);
        assert.deepEqual(Object.keys(value).sort(),schema.required.toSorted());
        for(const [key,item] of Object.entries(value))assert.equal(schema.properties[key].const,item,key);
        const documented=spec.components.responses['503'].content['application/problem+json'].schema.oneOf;
        assert.deepEqual(documented[1],{$ref:'#/components/schemas/maintenanceProblem'});
        assert.equal(spec.components.schemas.problem.properties.code.enum.includes('maintenance'),false);
    } finally {await h.close();}
});
