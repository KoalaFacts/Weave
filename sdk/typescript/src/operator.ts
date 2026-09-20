import {HttpTransport, type ClientOptions, type CallOptions} from './http.js';
import {ClientError, snapshot, type Invocation, type ApprovalReview, type ApprovalStatus, type Reply} from './protocol.js';
export type {ApprovalReview} from './protocol.js';
/** Construct separately with reviewer credentials, never an Agent's shared administrator credential. */
export class WeaveOperator {
  #http: HttpTransport;
  constructor(options: ClientOptions) {this.#http=new HttpTransport(options);}
  review(request: Invocation, options: CallOptions = {}): Promise<Reply<ApprovalReview>> {
    const owned=snapshot(request);
    return this.#http.send(owned.toolName,owned.invocationId,`/${owned.invocationId}/approval/review`,owned,'review',options);
  }
  decide(request: Invocation, planDigest: string, decision: 'approve'|'reject', options: CallOptions = {}): Promise<Reply<ApprovalStatus>> {
    const owned=snapshot(request);
    if (!/^approval-v1:[0-9A-F]{64}$/.test(planDigest) || !['approve','reject'].includes(decision)) throw new ClientError('invalid-input',owned.invocationId);
    return this.#http.send(owned.toolName,owned.invocationId,`/${owned.invocationId}/approval/decision`,
      {invocation:owned,planDigest,decision},'approval',options);
  }
}
