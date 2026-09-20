import {HttpTransport, type ClientOptions, type CallOptions} from './http.js';
import {component, invocationId, snapshot, type Invocation, type InvocationResult, type ApprovalStatus, type Reply} from './protocol.js';
export type {ClientOptions, CallOptions} from './http.js';
export {ClientError} from './protocol.js';
export type {Invocation, InvocationResult, ApprovalStatus, Reply, ApiError, Outcome, ApprovalState} from './protocol.js';
/** Agent-side operations only. No minting, approval decision, automatic ID or retry. */
export class WeaveClient {
  #http: HttpTransport;
  constructor(options: ClientOptions) {this.#http=new HttpTransport(options);}
  async invoke(request: Invocation, options: CallOptions = {}): Promise<Reply<InvocationResult>> {
    const owned=snapshot(request);
    return this.#http.send(owned.toolName,owned.invocationId,'',owned,'invocation',options);
  }
  getInvocation(tool: string, id: string, options: CallOptions = {}): Promise<Reply<InvocationResult>> {
    id=invocationId(id); return this.#http.send(component(tool),id,`/${id}`,undefined,'invocation',options);
  }
  getApproval(tool: string, id: string, options: CallOptions = {}): Promise<Reply<ApprovalStatus>> {
    id=invocationId(id); return this.#http.send(component(tool),id,`/${id}/approval`,undefined,'approval',options);
  }
}
