import assert from 'node:assert/strict';
import {test} from 'node:test';
import {runInNewContext} from 'node:vm';
import {accountProofPage,accountProofScript} from '../src/account-proof-page.ts';

const challenge='A'.repeat(43);
const control='c'.repeat(64);

test('first-party handoff protects control against HTML injection, framing and external scripts', async () => {
    const page=accountProofPage('https://auth.example/?<script>alert(1)</script>', 'https://weave.example/mcp');
    const html=await page.text();
    assert.equal(page.status,200);
    assert.match(html,/&#60;script&#62;alert\(1\)&#60;\/script&#62;/);
    assert.doesNotMatch(html,/<script>alert\(1\)<\/script>/);
    assert.equal(page.headers.get('cache-control'),'no-store');
    assert.equal(page.headers.get('referrer-policy'),'no-referrer');
    assert.equal(page.headers.get('x-frame-options'),'DENY');
    assert.match(page.headers.get('content-security-policy')??'',/form-action 'none'/);
    assert.match(html,/src="\/account-proof.js"/);
    const js=await accountProofScript();
    assert.equal(js.headers.get('cache-control'),'no-store');
    assert.match(js.headers.get('content-type')??'',/^text\/javascript/);
});

async function exercise(url: string, submittedChallenge: string, submittedControl: string, response: unknown) {
    let handler: ((event: {preventDefault: () => void}) => Promise<void>)|undefined;
    const controls: Record<string, {value: string; disabled?: boolean; textContent?: string; addEventListener?: (name: string, callback: typeof handler) => void}> = {
        'owner-proof-form': {value:'',addEventListener: (_name,cb) => {handler=cb;}},
        'proof-challenge':{value:submittedChallenge}, 'owner-control':{value:submittedControl},
        'owner-proof':{value:''}, 'make-proof':{value:'',disabled:false}, 'proof-status':{value:'',textContent:''}
    };
    const calls: {path:string;init:RequestInit}[]=[];
    const script=await (await accountProofScript()).text();
    runInNewContext(script,{
        document:{getElementById:(id:string)=>controls[id]},
        window:{location:{href:url}}, URL,
        fetch:async (path:string,init:RequestInit)=>{calls.push({path,init});return response;}
    });
    assert.ok(handler);
    await handler!({preventDefault(){}});
    return {controls,calls};
}

test('successful handoff sends the control only to the same-origin proof route and clears it',async()=>{
    const proof='wop1.AA.BB';
    const result=await exercise('https://weave.example/account-proof?challenge='+challenge,'',control,
        {ok:true,json:async()=>({proof})});
    assert.equal(result.calls.length,1);
    assert.equal(result.calls[0].path,'/v1/accounts/proof');
    assert.equal(result.calls[0].init.mode,'same-origin');
    assert.equal(result.calls[0].init.redirect,'error');
    assert.equal(result.calls[0].init.cache,'no-store');
    assert.equal((result.calls[0].init.headers as Record<string,string>)['X-Weave-Account-Control'],control);
    assert.equal(JSON.parse(result.calls[0].init.body as string).challenge,challenge);
    assert.equal(result.controls['owner-control'].value,'');
    assert.equal(result.controls['owner-proof'].value,proof);
});

test('invalid challenge never transmits the account control',async()=>{
    const result=await exercise('https://weave.example/account-proof?challenge=bad','bad',control,
        {ok:true,json:async()=>({proof:'wop1.AA.BB'})});
    assert.equal(result.calls.length,0);
    assert.equal(result.controls['owner-control'].value,'');
    assert.equal(result.controls['owner-proof'].value,'');
});

test('failed minting clears the control without displaying an unverified proof',async()=>{
    const result=await exercise('https://weave.example/account-proof',challenge,control,{ok:false});
    assert.equal(result.calls.length,1);
    assert.equal(result.controls['owner-control'].value,'');
    assert.equal(result.controls['owner-proof'].value,'');
});
