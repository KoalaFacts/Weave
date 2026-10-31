/** Fresh, canonical 256-bit authorization nonce; never an agent credential. */
export const ttlMs = 600_000;
const pattern = /^[A-Za-z0-9_-]{43}$/;
export function createChallenge(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return btoa(String.fromCharCode(...bytes)).replaceAll("+","-").replaceAll("/","_").replace(/=+$/,"");
}
export function validChallenge(value: unknown): value is string {
  if (typeof value !== "string" || !pattern.test(value)) return false;
  try {
    const bytes = Uint8Array.from(atob(value.replaceAll("-","+").replaceAll("_","/")+"="), c=>c.charCodeAt(0));
    return bytes.length===32 && btoa(String.fromCharCode(...bytes))
      .replaceAll("+","-").replaceAll("/","_").replace(/=+$/,"")===value;
  } catch { return false; }
}
export const validHandle = validChallenge;
const accountId=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
/** No unbounded multipart body or attacker-controlled additional grant fields. */
export async function readConsent(request: Request): Promise<URLSearchParams|null> {
  if (!/^application\/x-www-form-urlencoded(?:;|$)/i.test(request.headers.get("content-type")??""))return null;
  const reader=request.body?.getReader();if(!reader)return null;
  let length=0;const chunks:Uint8Array[]=[];
  try{
    while(true){const part=await reader.read();if(part.done)break;length+=part.value.byteLength;
      if(length>8192)return null;chunks.push(part.value);}
    const raw=new Uint8Array(length);let offset=0;
    for(const chunk of chunks){raw.set(chunk,offset);offset+=chunk.length;}
    const form=new URLSearchParams(new TextDecoder("utf-8",{fatal:true}).decode(raw));
    if([...form.keys()].some(k=>!["handle","decision","agent","agentName"].includes(k))||
      form.getAll("handle").length!==1||form.getAll("decision").length!==1||
      form.getAll("agent").length>1||form.getAll("agentName").length>1)return null;
    const agent=form.get("agent");
    // An approval names exactly one agent: an existing account UUID, or "new" with its name.
    if(agent!==null&&agent!=="new"&&!accountId.test(agent))return null;
    if(form.has("agentName")!==(agent==="new"))return null;
    if(form.get("decision")==="approve"&&agent===null)return null;
    return form;
  }catch{return null;}finally{await reader.cancel().catch(()=>{});reader.releaseLock();}
}
