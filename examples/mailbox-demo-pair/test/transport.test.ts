import assert from 'node:assert/strict';
import {test} from 'node:test';
import {StdioClient} from '../mcp.ts';

// Real child-process framing; this fixture is not a Weave server or live acceptance.
const prelude=`const r=require('node:readline').createInterface({input:process.stdin});const emit=(id,result)=>process.stdout.write(JSON.stringify({jsonrpc:'2.0',id,result})+'\\n');`;
function fixture(code:string,timeout=1500,args:string[]=[]){return new StdioClient(process.execPath,['-e',prelude+code,...args],{PATH:process.env.PATH},timeout);}
test('stdio child handshake and tool output preserve exact int64 values',async()=>{
    const c=fixture(`let ready=false;r.on('line',line=>{const m=JSON.parse(line);if(m.method==='initialize')emit(m.id,{protocolVersion:'2024-11-05'});else if(m.method==='notifications/initialized')ready=true;else if(ready)emit(m.id,{content:[{type:'text',text:'{"generation":9223372036854775807}'}]});});`);
    try{await c.initialize();assert.equal((await c.call('fixture')).generation,'9223372036854775807');}finally{await c.close();}
});
test('stdio tool failures do not echo upstream response content',async()=>{
    const c=fixture(`r.on('line',line=>{const m=JSON.parse(line);emit(m.id,{isError:true,content:[{type:'text',text:'sensitive-fixture-never-log'}]});});`);
    try{await assert.rejects(c.call('fixture'),e => (e instanceof Error) && e.message === 'mcp_tool_failed');}finally{await c.close();}
});
for(const output of ['not-json\n',JSON.stringify({jsonrpc:'2.0',id:999,result:{}})+'\n']){
    test('malformed or unsolicited child response closes transport',async()=>{
        const c=fixture(`r.on('line',()=>process.stdout.write(process.argv[1]));`,1500,[output]);
        try{await assert.rejects(c.call('fixture'),/mcp_transport_failed/);await assert.rejects(c.call('fixture'),/mcp_transport_closed/);}finally{await c.close();}
    });
}
test('stdio timeout terminates a silent process without retrying the request',async()=>{
    const c=fixture(`r.on('line',()=>{});`,50);
    try{await assert.rejects(c.call('fixture'),/mcp_transport_failed/);await assert.rejects(c.call('fixture'),/mcp_transport_closed/);}finally{await c.close();}
});
test('failed process creation has a bounded transport error',async()=>{
    const c=new StdioClient('/weave-missing-fixture-executable',[],{},100);
    try{await assert.rejects(c.call('fixture'),/mcp_transport_failed/);}finally{await c.close();}
});
