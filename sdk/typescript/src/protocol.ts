export interface Invocation {
  readonly invocationId: string;
  readonly toolName: string;
  readonly method: string;
  readonly parameters: Readonly<Record<string, string>>;
  readonly rawInput?: string | null;
}
export type Outcome = 'Succeeded' | 'Failed' | 'Denied' | 'Cancelled' | 'OutcomeUnknown' | 'NotDispatched';
export type ApprovalState = 'Pending' | 'Approved' | 'Rejected' | 'Expired' | 'Cancelled' | 'Consumed';
export interface InvocationResult {
  success: boolean; output: string; duration: string; toolName: string;
  invocationId?: string; attemptId?: string; outcome?: Outcome | null;
  outcomeRecorded: boolean; isReplay: boolean; errorCode?: string; error?: string;
  approvalState?: ApprovalState; approvalPlanDigest?: string; approvalExpiresAt?: string;
}
export interface ApprovalStatus { invocationId: string; approvalState: ApprovalState; expiresAt: string }
export interface ApprovalReview {
  invocationId: string; workspaceId: string; subject: string; toolName: string;
  operation: string; parameters: Record<string, string>; rawInput?: string | null;
  targetDescription: string; planDigest: string; expiresAt: string;
}
export interface ApiError { errorCode: string }
export interface Reply<T> { status: number; body: T | ApiError }
export type ErrorKind = 'invalid-input' | 'transport' | 'protocol';
export class ClientError extends Error {
  readonly kind: ErrorKind;
  readonly invocationId: string | undefined;
  readonly delivery: 'not-sent' | 'unconfirmed';
  constructor(kind: ErrorKind, id?: string) {
    super(kind === 'invalid-input' ? 'Invalid client input; request not sent.' :
      'Operation not confirmed; retain the invocation ID and query status. No automatic retry.');
    this.name = 'ClientError'; this.kind = kind; this.invocationId = id;
    this.delivery = kind === 'invalid-input' ? 'not-sent' : 'unconfirmed';
  }
}
export function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}
export function component(value: string): string {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_.-]{1,128}$/.test(value) || value === '.' || value === '..') throw new ClientError('invalid-input');
  return value;
}
export function invocationId(value: string): string {
  if (typeof value !== 'string' || !/^[0-9a-fA-F]{32}$/.test(value) || /^0+$/.test(value)) throw new ClientError('invalid-input');
  return value.toLowerCase();
}
export function snapshot(value: Invocation): Invocation {
  if (!object(value) || Object.keys(value).some(k => !['invocationId','toolName','method','parameters','rawInput'].includes(k))
    || typeof value.toolName !== 'string' || typeof value.method !== 'string' || !value.method.trim()
    || !object(value.parameters) || Object.values(value.parameters).some(v => typeof v !== 'string')
    || (value.rawInput !== undefined && value.rawInput !== null && typeof value.rawInput !== 'string')) throw new ClientError('invalid-input');
  return {invocationId: invocationId(value.invocationId), toolName:component(value.toolName), method:value.method,
    parameters:Object.fromEntries(Object.entries(value.parameters)),
    ...(value.rawInput === undefined ? {} : {rawInput:value.rawInput})};
}
const outcomes = new Set(['Succeeded','Failed','Denied','Cancelled','OutcomeUnknown','NotDispatched']);
const states = new Set(['Pending','Approved','Rejected','Expired','Cancelled','Consumed']);
export function decode<T>(value: unknown, shape: 'invocation'|'approval'|'review', id: string, tool: string, workspace: string): T | ApiError {
  if (!object(value)) throw new ClientError('protocol',id);
  if (!('success' in value) && !('invocationId' in value) && typeof value.errorCode === 'string'
      && /^[a-z][a-z0-9-]{0,95}$/.test(value.errorCode)) return {errorCode:value.errorCode};
  if (value.invocationId !== id) throw new ClientError('protocol',id);
  if (shape === 'invocation') {
    if (typeof value.success !== 'boolean' || typeof value.output !== 'string' || typeof value.duration !== 'string'
      || value.toolName !== tool || typeof value.outcomeRecorded !== 'boolean' || typeof value.isReplay !== 'boolean'
      || (value.outcome != null && !outcomes.has(value.outcome as string))
      || (value.approvalState != null && !states.has(value.approvalState as string))
      || (value.success && value.outcome !== 'Succeeded')) throw new ClientError('protocol',id);
  } else if (shape === 'approval') {
    if (!states.has(value.approvalState as string) || typeof value.expiresAt !== 'string') throw new ClientError('protocol',id);
  } else if (value.workspaceId !== workspace || value.toolName !== tool || !object(value.parameters)
      || Object.values(value.parameters).some(v=>typeof v!=='string')
      || ['subject','operation','targetDescription','planDigest','expiresAt'].some(k=>typeof value[k]!=='string')
      || !/^approval-v1:[0-9A-F]{64}$/.test(value.planDigest as string)
      || (value.rawInput != null && typeof value.rawInput !== 'string')) throw new ClientError('protocol',id);
  return value as T;
}
