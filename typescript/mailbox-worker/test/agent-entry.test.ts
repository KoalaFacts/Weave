import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { createContext, runInContext } from 'node:vm';
import { webcrypto } from 'node:crypto';

const entryPath=new URL('../src/agent-entry.html',import.meta.url);
const guidePath=new URL('../src/agent-start.md',import.meta.url);
const owned={addressId:'01991341-0000-7000-8000-000000000001',accountId:'recipient',label:'Private test address',isPublic:false,effectiveFrom:null,effectiveTo:null};
function ui(read=async()=>({items:[owned],nextCursor:null})){
    assert.ok(existsSync(entryPath),'Agent entry panel is missing');
    const html=readFileSync(entryPath,'utf8');
    const source=/<script nonce="WEAVE_NONCE">([\s\S]*?)<\/script>/.exec(html)?.[1];
    assert.ok(source,'Invitation script is missing');
    const elements=new Map(),calls=[],copied=[];
    function element(){return {value:'',textContent:'',disabled:false,children:[],handlers:{},addEventListener(name,fn){(this.handlers[name]??=[]).push(fn);},append(...children){this.children.push(...children);},replaceChildren(...children){this.children=[...children];this.value='';}};}
    const get=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);};
    const session={accountId:'recipient',token:'SYNTHETIC_PRIVATE_TOKEN_DO_NOT_COPY'};
    const context=createContext({document:{getElementById:get,createElement:element},session,location:{origin:'https://demo.weave-389.workers.dev'},crypto:webcrypto,URL,Date,Map,JSON,navigator:{clipboard:{writeText:async text=>copied.push(text)}},api:async(s,path,method='GET')=>{calls.push({s,path,method});return read(s,path,method);},current:s=>{if(context.session!==s)throw Error('Session changed');}});
    runInContext(source,context);
    return {get,calls,copied,context,click:async id=>{for(const fn of get(id).handlers.click??[])await fn();},change:async id=>{for(const fn of get(id).handlers.change??[])await fn();},async ready(){await this.click('loadInvitationAddresses');get('invitationAddress').value=owned.addressId;}};
}
test('agent entry is readable without JavaScript and points to one canonical guide',()=>{
    assert.ok(existsSync(entryPath),'Agent entry panel is missing');
    const html=readFileSync(entryPath,'utf8');
    assert.match(html,/<a href="\/agent-start\.md">/);
    assert.match(html,/no account operation/i);
    assert.ok(existsSync(guidePath),'Canonical agent-start.md is missing');
    const guide=readFileSync(guidePath,'utf8');
    for(const term of ['waiting_contact_acceptance','waiting_receipt','needs_owner_action','not_executed','outcome_unknown','runId','replyTo','nextAction','same exact','WEAVE_MAILBOX_CONTROL'])assert.ok(guide.includes(term),term);
    assert.match(guide,/not.*automatically wake/i);
});
test('discovery is inert and invitation contains only the allowlisted nonsecret fields',async()=>{
    const app=ui();assert.equal(app.calls.length,0);assert.equal(app.copied.length,0);
    await app.ready();await app.click('buildInvitation');
    const card=JSON.parse(app.get('invitationCard').value);
    assert.deepEqual(Object.keys(card).sort(),['goal','origin','peerAddressId','peerAccountId','peerAgentId','runId','type','version'].sort());
    assert.equal(card.peerAccountId,'recipient');assert.equal(card.peerAgentId,'recipient');assert.equal(card.peerAddressId,owned.addressId);
    assert.equal(card.type,'weave-demo-invitation');assert.equal(card.goal,'one_ping_pong');assert.equal(card.version,1);
    assert.match(card.runId,/^[0-9a-f-]{36}$/);assert.ok(!app.get('invitationCard').value.includes(app.context.session.token));
    assert.ok(app.calls.every(call=>call.method==='GET'&&call.path.startsWith('/v1/addresses')));
});
test('rebuilding and copying reuse the run until a new run is explicitly requested',async()=>{
    const app=ui();await app.ready();await app.click('buildInvitation');const original=app.get('invitationCard').value;
    await app.click('buildInvitation');await app.click('copyInvitation');await app.click('copyInvitation');
    assert.equal(app.get('invitationCard').value,original);assert.deepEqual(app.copied,[original,original]);
    await app.click('newInvitationRun');assert.notEqual(JSON.parse(app.get('invitationCard').value).runId,JSON.parse(original).runId);
});
test('no session reports an exact prerequisite without registering or reading',async()=>{
    const app=ui();app.context.session=null;await app.click('loadInvitationAddresses');await app.click('buildInvitation');
    assert.equal(app.calls.length,0);assert.equal(app.get('invitationCard').value,'');assert.match(app.get('invitationStatus').textContent,/create or restore/i);
});
for(const change of [{accountId:'another'},{effectiveTo:'2000-01-01T00:00:00Z'},{effectiveFrom:'2999-01-01T00:00:00Z'},{effectiveTo:'not-a-date'}])test('foreign or ineffective addresses cannot become invitations '+JSON.stringify(change),async()=>{
    const app=ui(async()=>({items:[{...owned,...change}],nextCursor:null}));await app.ready();await app.click('buildInvitation');
    assert.equal(app.get('invitationCard').value,'');assert.match(app.get('invitationStatus').textContent,/current address/i);
});
test('only an explicit next-page click follows an encoded owner address cursor',async()=>{
    const app=ui(async()=>({items:[owned],nextCursor:'addresses:recipient:10/+?'}));await app.ready();
    assert.equal(app.calls.length,1);assert.equal(app.get('invitationAddressesNext').disabled,false);
    await app.click('invitationAddressesNext');assert.equal(app.calls.length,2);assert.ok(app.calls[1].path.endsWith('afterCursor=addresses%3Arecipient%3A10%2F%2B%3F'));
});
test('clearing a session erases the invitation and late reads cannot restore it',async()=>{
    let resolve;const app=ui(()=>new Promise(r=>{resolve=r;}));const pending=app.click('loadInvitationAddresses');
    app.context.session=null;await app.click('disconnect');resolve({items:[owned],nextCursor:null});await pending;
    assert.equal(app.get('invitationCard').value,'');assert.equal(app.get('invitationAddress').children.length,0);assert.equal(app.get('copyInvitation').disabled,true);
});
test('clipboard failure preserves the card and offers manual copy without sending',async()=>{
    const app=ui();await app.ready();await app.click('buildInvitation');const original=app.get('invitationCard').value;
    app.context.navigator.clipboard.writeText=async()=>{throw Error('denied');};await app.click('copyInvitation');
    assert.equal(app.get('invitationCard').value,original);assert.match(app.get('invitationStatus').textContent,/select and copy/i);
    assert.ok(app.calls.every(call=>call.method==='GET'));
});

test('changing the selected address invalidates the old card before copying',async()=>{
    const other={...owned,addressId:'01991341-0000-7000-8000-000000000002'};
    const app=ui(async()=>({items:[owned,other],nextCursor:null}));await app.ready();await app.click('buildInvitation');
    const original=JSON.parse(app.get('invitationCard').value);
    app.get('invitationAddress').value=other.addressId;await app.change('invitationAddress');
    assert.equal(app.get('invitationCard').value,'');assert.equal(app.get('copyInvitation').disabled,true);
    await app.click('copyInvitation');assert.equal(app.copied.length,0);
    await app.click('buildInvitation');const current=JSON.parse(app.get('invitationCard').value);
    assert.equal(current.peerAddressId,other.addressId);assert.notEqual(current.runId,original.runId);
});
test('copy revalidates address selection even if a change event was not delivered',async()=>{
    const app=ui();await app.ready();await app.click('buildInvitation');app.get('invitationAddress').value='';
    await app.click('copyInvitation');assert.equal(app.copied.length,0);assert.equal(app.get('invitationCard').value,'');
});
test('guide explicitly retrieves and correlates the inbox introduction before acceptance',()=>{
    const guide=readFileSync(guidePath,'utf8'),section=guide.split('### Recipient introduction review before acceptance')[1]?.split('## 4.')[0];
    assert.ok(section,'Recipient review step is missing');
    for(const field of ['read_inbox','GET /v1/inbox','requestMessageId','requesterAccountId','recipientAccountId','nextCursor','needs_owner_action'])assert.ok(section.includes(field),field);
    assert.ok(section.indexOf('read_inbox')<section.indexOf('decide_contact'));
});
