import {DurableObject} from "cloudflare:workers";
import {ttlMs,validChallenge} from "./challenge.ts";
interface Nonce {value:string; expiresAt:number}
/** Private, SQLite-backed, per-consent Durable Object.
 * A storage transaction, not KV read/delete, atomically stops parallel reuse.
 */
export class ConsentNonce extends DurableObject {
  async fetch(req:Request):Promise<Response>{
    const path=new URL(req.url).pathname, now=Date.now();
    if(req.method==="PUT"&&path==="/nonce"){
      const input:unknown=await req.json().catch(()=>null);
      const value=input&&typeof input==="object"&&!Array.isArray(input)&&
        Object.keys(input).length===1?(input as {challenge?:unknown}).challenge:null;
      if(!validChallenge(value))return new Response(null,{status:400});
      const inserted=await this.ctx.storage.transaction(async tx=>{
        if(await tx.get("pending"))return false;
        await tx.put("pending",{value,expiresAt:now+ttlMs} satisfies Nonce);return true;
      });
      if(!inserted)return new Response(null,{status:409});
      await this.ctx.storage.setAlarm(now+ttlMs);
      return new Response(null,{status:201});
    }
    if(req.method==="GET"&&path==="/nonce"){
      const pending=await this.ctx.storage.get<Nonce>("pending");
      return pending&&pending.expiresAt>now?
        Response.json({challenge:pending.value},{headers:{"Cache-Control":"no-store"}}):
        new Response(null,{status:404});
    }
    if(req.method==="DELETE"&&path==="/nonce"){
      const input:unknown=await req.json().catch(()=>null);
      const value=input&&typeof input==="object"&&!Array.isArray(input)?
        (input as {challenge?:unknown}).challenge:null;
      if(!validChallenge(value))return new Response(null,{status:400});
      const accepted=await this.ctx.storage.transaction(async tx=>{
        const pending=await tx.get<Nonce>("pending");
        if(!pending||pending.expiresAt<=Date.now()||pending.value!==value)return false;
        await tx.delete("pending");return true;
      });
      return new Response(null,{status:accepted?204:409});
    }
    return new Response(null,{status:404});
  }
  async alarm(){await this.ctx.storage.deleteAll();}
}
