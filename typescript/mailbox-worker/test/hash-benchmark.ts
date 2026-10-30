import { Miniflare, convertV4MiniflareOptions } from 'miniflare';
const mf=new Miniflare(convertV4MiniflareOptions({modules:true,compatibilityDate:'2026-07-01',script:`
export default {async fetch(request) {
 const {algorithm,n}=await request.json();
 const bytes=new TextEncoder().encode('benchmark-only-not-a-credential-0123456789abcdef0123456789abcdef012345');
 let output; for(let i=0;i<n;i++) output=await crypto.subtle.digest(algorithm,bytes);
 return new Response(String(output.byteLength));
}}` }));
try {
 const result={runtime:'local workerd via pinned Miniflare',samples:7,iterationsPerSample:2000,measurement:'Node monotonic HTTP round-trip time / iterations; includes request overhead; not remote CPU billing'};
 for(const algorithm of ['SHA-256','SHA-512']) {
  const samples=[];
  for(let run=0;run<9;run++) {
   const start=process.hrtime.bigint();
   const response=await mf.dispatchFetch('http://localhost/',{method:'POST',body:JSON.stringify({algorithm,n:2000})});
   if(!response.ok) throw new Error(await response.text()); await response.text();
   const elapsed=Number(process.hrtime.bigint()-start)/1e6;
   if(run>=2) samples.push(elapsed);
  }
  samples.sort((a,b)=>a-b); result[algorithm]={samplesMs:samples,medianMicrosecondsPerHash:samples[3]*1000/2000};
 }
 console.log(JSON.stringify(result,null,2));
} finally {await mf.dispose();}
