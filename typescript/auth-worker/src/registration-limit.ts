/** Rate-limit bindings for open dynamic client registration; both are required for /register to be open. */
export interface RegistrationLimitEnv{
  /** Per-address budget, keyed by registrationLimiterKey. */
  REGISTRATION_LIMITER?:RateLimit;
  /** One shared budget across all addresses, keyed "global". */
  REGISTRATION_GLOBAL_LIMITER?:RateLimit;
}

const ipv4=/^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$/;
const hextet=/^[0-9a-f]{1,4}$/i;
/** The eight expanded IPv6 groups as numbers, or null when the text is not a plain IPv6 address. */
function ipv6Groups(address:string):number[]|null{
  const halves=address.split("::");
  if(halves.length>2)return null;
  const parse=(part:string)=>{
    if(!part)return [];
    const groups:number[]=[];
    const pieces=part.split(":");
    for(const [index,piece] of pieces.entries()){
      if(index===pieces.length-1&&piece.includes(".")){
        if(!ipv4.test(piece))return null;
        const [a,b,c,d]=piece.split(".").map(Number);
        groups.push(a*256+b,c*256+d);
      }else if(hextet.test(piece))groups.push(parseInt(piece,16));
      else return null;
    }
    return groups;
  };
  const head=parse(halves[0]),tail=halves.length===2?parse(halves[1]):[];
  if(!head||!tail)return null;
  if(halves.length===1)return head.length===8?head:null;
  // A dotted IPv4 tail may only end the address.
  if(halves[0].includes("."))return null;
  const missing=8-head.length-tail.length;
  return missing>=1?[...head,...Array<number>(missing).fill(0),...tail]:null;
}
const v4Key=(high:number,low:number)=>"v4:"+[high>>8,high&255,low>>8,low&255].join(".");

/**
 * The per-address limiter key for a CF-Connecting-IP value. IPv4 keeps the
 * address; IPv6 collapses to its /64, the smallest block one subscriber is
 * normally given, so rotating addresses inside it share one budget.
 */
export function registrationLimiterKey(connectingIp:string|null):string{
  if(!connectingIp)return "unknown";
  if(ipv4.test(connectingIp))return "v4:"+connectingIp;
  const groups=ipv6Groups(connectingIp);
  if(!groups)return "unknown";
  if(groups.slice(0,5).every(x=>x===0)&&groups[5]===0xffff)return v4Key(groups[6],groups[7]);
  return "v6:"+groups.slice(0,4).map(x=>x.toString(16).padStart(4,"0")).join(":")+"::/64";
}

/** Registration is offered (and advertised) only when both budgets are bound. */
export const registrationOpen=(env:RegistrationLimitEnv)=>!!env.REGISTRATION_LIMITER&&!!env.REGISTRATION_GLOBAL_LIMITER;

const tooManyRegistrations=()=>new Response("Too many registrations",
  {status:429,headers:{"Cache-Control":"no-store","Retry-After":"60"}});
const registrationUnavailable=()=>new Response("Authorization not configured",{status:503,headers:{"Cache-Control":"no-store"}});

/** Fail closed: a missing binding or a limiter fault means no open registration. Null lets the request through. */
export async function limitRegistration(env:RegistrationLimitEnv,req:Request):Promise<Response|null>{
  if(!env.REGISTRATION_LIMITER||!env.REGISTRATION_GLOBAL_LIMITER)return registrationUnavailable();
  if(req.method!=="POST")return null;
  try{
    // Per-address first, so one address over its own budget cannot spend the shared one.
    const perAddress=await env.REGISTRATION_LIMITER.limit({key:registrationLimiterKey(req.headers.get("CF-Connecting-IP"))});
    if(!perAddress.success)return tooManyRegistrations();
    const global=await env.REGISTRATION_GLOBAL_LIMITER.limit({key:"global"});
    return global.success?null:tooManyRegistrations();
  }catch{return registrationUnavailable();}
}
