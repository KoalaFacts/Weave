import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Script } from 'node:vm';
import { start } from './harness.ts';
test('public registry has a credential-free entry with nonce CSP and only public API access',async()=>{
    const h=await start();
    try {
        const r=await h.request('/registry');assert.equal(r.status,200,'Public registry must be accessible without an account');
        const html=await r.text(),script=/<script nonce="([^"]+)">([\s\S]*?)<\/script>/.exec(html);assert.ok(script);
        assert.ok(r.headers.get('content-security-policy').includes("'nonce-"+script[1]+"'"));new Script(script[2]);
        assert.ok(html.includes('Public registry'));assert.ok(html.includes('does not grant permission'));
        assert.equal(/localStorage|sessionStorage|document\.cookie|innerHTML/.test(script[2]),false);
        assert.ok(script[2].includes('/v1/directory'));assert.ok((await (await h.request('/')).text()).includes('href="/registry"'));
    }finally{await h.close();}
});
test('registry search and exact lookup expose only actively public effective addresses, never profile or messages',async()=>{
    const h=await start();
    try {
        const a=await h.register({displayName:'PRIVATE-PROFILE',description:'PRIVATE-DESCRIPTION'});
        // Three public addresses exceed the unclaimed-agent limit of one.
        await h.claim(a);
        const visible=await h.address(a,{label:'Visible <script>untrusted</script>',isPublic:true});
        const hidden=[await h.address(a,{label:'PRIVATE-ADDRESS'}),await h.address(a,{label:'EXPIRED',isPublic:true,effectiveTo:'2020-01-01T00:00:00Z'}),await h.address(a,{label:'FUTURE',isPublic:true,effectiveFrom:'2099-01-01T00:00:00Z'})];
        const page=await h.ok('/v1/directory');assert.deepEqual(page.items.map(x=>x.addressId),[visible.addressId]);
        assert.equal((await h.ok('/v1/directory?q=Visible')).items.length,1);
        for(const address of hidden)assert.equal((await h.request('/v1/directory/address?addressId='+address.addressId)).status,404);
        const exposed=JSON.stringify(page);for(const value of ['PRIVATE-PROFILE','PRIVATE-DESCRIPTION','PRIVATE-ADDRESS',await h.token(a),a.key.privateJwk.d!,'payload'])assert.ok(!exposed.includes(value));
        assert.equal((await h.request('/v1/inbox')).status,401);
    }finally{await h.close();}
});
test('actual registry page script renders untrusted labels as text and performs only anonymous public lookups',async()=>{
    const {createContext,runInContext}=await import('node:vm'),h=await start();
    try{
        const account=await h.register(),label='<img src=x onerror="globalThis.compromised=true">',address=await h.address(account,{label,isPublic:true}),hidden=await h.address(account,{label:'private'});
        const html=await(await h.request('/registry')).text(),script=/<script nonce="[^"]+">([\s\S]*?)<\/script>/.exec(html)[1],elements=new Map(),calls=[];
        function element(){return {textContent:'',value:'',children:[],disabled:false,handlers:{},set innerHTML(_value){throw Error('HTML parsing is forbidden');},append(child){this.children.push(child)},replaceChildren(...children){this.children=children},addEventListener(event,fn){this.handlers[event]=fn}};}
        const get=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id)};
        const context=createContext({document:{getElementById:get,createElement:element},fetch:async(path,options)=>{calls.push({path,options});return h.request(path)}});
        await runInContext(script,context);
        assert.equal(get('results').children[0].children[0].textContent,label);assert.equal(context.compromised,undefined);
        assert.ok(calls.every(call=>call.path.startsWith('/v1/directory')&&call.options.credentials==='omit'&&!call.options.headers));
        get('address').value=hidden.addressId;await get('lookup').handlers.click();assert.equal(get('results').children.length,0);assert.equal(get('status').textContent,'Public address unavailable.');
        get('address').value=address.addressId;await get('lookup').handlers.click();assert.equal(get('results').children.length,1);
    }finally{await h.close();}
});
