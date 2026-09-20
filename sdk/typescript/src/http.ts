import {ClientError, component, decode, type Reply} from './protocol.js';
export interface ClientOptions { baseUrl: string; workspace: string; capability: string; timeoutMs?: number; globalBearer?: string }
export interface CallOptions { signal?: AbortSignal }
const maxRequest = 1_048_576, maxResponse = 8_388_608;
export class HttpTransport {
  readonly workspace: string;
  #base: string; #capability: string; #bearer: string | undefined; #timeout: number;
  constructor(options: ClientOptions) {
    this.workspace = component(options.workspace);
    let url: URL;
    try { url = new URL(options.baseUrl); } catch { throw new ClientError('invalid-input'); }
    if (/[\s\\]/.test(options.baseUrl) || url.username || url.password || url.search || url.hash
      || !/^https?:$/.test(url.protocol) || !/^[A-Za-z0-9._~/-]*$/.test(url.pathname)
      || /(?:^|\/)\.{1,2}(?:\/|$)/.test(options.baseUrl.replace(/^https?:\/\/[^/]+/, ''))
      || (url.protocol === 'http:' && !['127.0.0.1','[::1]'].includes(url.hostname))
      || !/^[A-Za-z0-9_-]{1,16384}$/.test(options.capability)
      || (options.globalBearer !== undefined && !/^[\x21-\x7e]{1,16384}$/.test(options.globalBearer))) throw new ClientError('invalid-input');
    this.#base = url.href.replace(/\/$/,''); this.#capability = options.capability; this.#bearer = options.globalBearer;
    this.#timeout = options.timeoutMs ?? 30_000;
    if (!Number.isInteger(this.#timeout) || this.#timeout < 1 || this.#timeout > 300_000) throw new ClientError('invalid-input');
  }
  async send<T>(tool: string, id: string, suffix: string, body: unknown, shape: 'invocation'|'approval'|'review', options: CallOptions): Promise<Reply<T>> {
    const text = body === undefined ? undefined : JSON.stringify(body);
    if (text !== undefined && new TextEncoder().encode(text).length > maxRequest) throw new ClientError('invalid-input',id);
    const headers: Record<string,string> = {'X-Weave-Capability':this.#capability,'Accept':'application/json'};
    if (text !== undefined) headers['Content-Type']='application/json';
    if (this.#bearer !== undefined) headers.Authorization=`Bearer ${this.#bearer}`;
    const timeout=AbortSignal.timeout(this.#timeout);
    const signal=options.signal ? AbortSignal.any([options.signal,timeout]) : timeout;
    const url=`${this.#base}/api/workspaces/${this.workspace}/tools/${component(tool)}/invocations${suffix}`;
    try {
      const response=await fetch(url,{method:text===undefined?'GET':'POST',headers,...(text===undefined?{}:{body:text}),
        signal,redirect:'error',credentials:'omit',cache:'no-store'});
      if (response.status < 200 || response.status >= 600 || response.status >= 300 && response.status < 400
        || response.headers.get('content-type')?.split(';')[0]?.trim() !== 'application/json' || !response.body) throw new ClientError('protocol',id);
      const reader=response.body.getReader(), chunks:Uint8Array[]=[]; let count=0;
      try {
        while (true) {
          const {done,value}=await reader.read(); if (done) break;
          count+=value.length; if (count>maxResponse) throw new ClientError('protocol',id); chunks.push(value);
        }
      } finally { await reader.cancel().catch(()=>undefined); reader.releaseLock(); }
      const bytes=new Uint8Array(count); let offset=0; for (const chunk of chunks) {bytes.set(chunk,offset);offset+=chunk.length;}
      let value:unknown;
      try {value=JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes));} catch {throw new ClientError('protocol',id);}
      return {status:response.status,body:decode<T>(value,shape,id,tool,this.workspace)};
    } catch (error) { if (error instanceof ClientError) throw error; throw new ClientError('transport',id); }
  }
}
