import assert from 'node:assert/strict';
import {test} from 'node:test';
import {existsSync,mkdtempSync,mkdirSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {randomUUID} from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {start,secret} from './harness.ts';
import {Endpoint,verifyEvidence} from '../../../examples/mailbox-demo-pair/endpoint.ts';
import {StdioClient} from '../../../examples/mailbox-demo-pair/mcp.ts';
import {modelEnvironment} from '../../../examples/mailbox-demo-pair/runtime.ts';

test('pair endpoint protocol executes through real stdio MCP and local Worker D1 with synthetic inference',{skip:process.platform==='win32'&&'Requires POSIX owner-only file modes for private pair state'},async()=>{
    const h=await start(),root=mkdtempSync(join(tmpdir(),'weave-pair-d1-')),runId=randomUUID();
    const url=String(await h.mf.ready),controls={A:secret(),B:secret()};
    const script=fileURLToPath(new URL('../../../examples/mailbox-demo-mcp/server.ts',import.meta.url));
    const make=(role:'A'|'B')=>{
        const dir=join(root,role);if(!existsSync(dir))mkdirSync(dir,{mode:0o700});
        return new Endpoint(role,runId,dir,async()=>{
            // Synthetic credentials are only for this disposable local D1 fixture.
            const client=new StdioClient(process.execPath,[script],{...modelEnvironment(),WEAVE_DEMO_URL:url,WEAVE_ACCOUNT_CONTROL:controls[role]});
            try{await client.initialize();return client;}catch(error){await client.close();throw error;}
        },async(sequence,history)=>({text:`Fixture ${role} ${sequence}: ${history.at(-1)?.text??'question'}`,threadId:randomUUID(),inputTokens:1,outputTokens:1}));
    };
    let a=make('A'),b=make('B');
    try{
        const ai=await a.setup(),bi=await b.setup();assert.notEqual(ai.accountId,bi.accountId);
        a.peer(bi);b.peer(ai);await a.introduce();await b.accept();
        await a.step(1);await b.step(2);
        // Dispose both actual stdio MCP children and rebuild endpoint state from private checkpoints.
        await Promise.all([a.close(),b.close()]);a=make('A');b=make('B');
        await Promise.all([a.restore(),b.restore()]);
        for(let n=3;n<=6;n++)await(n%2?a:b).step(n);
        await a.consumeFinal();assert.equal(verifyEvidence(await a.evidence(),await b.evidence()).messages.length,6);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts').first('n'),2);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM account_addresses WHERE is_public=1').first('n'),0);
    }finally{await a.close();await b.close();await h.close();rmSync(root,{recursive:true,force:true});}
});
