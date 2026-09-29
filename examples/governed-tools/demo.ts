#!/usr/bin/env node
// An in-memory illustration of the governed flow. The real Host uses signed authority and SQLite.

import assert from 'node:assert/strict';

const READ = 'tool:files:invoke:read_file';
const WRITE = 'tool:files:invoke:write_file';
const APPROVE = 'tool:files:approve:write_file';

type WritePlan = { id: string; path: string; content: string };
type PendingWrite = { subject: string; plan: WritePlan };

const samePlan = (a: WritePlan, b: WritePlan) =>
  a.id === b.id && a.path === b.path && a.content === b.content;

class InMemorySketch {
  documents = new Map<string, string>([['meeting.txt', 'Meeting: ship a small demo.']]);
  grants = new Map<string, Set<string>>([
    ['reader', new Set([READ])],
    ['writer', new Set([WRITE])],
    ['reviewer', new Set([APPROVE])],
  ]);
  pending = new Map<string, PendingWrite>();
  approved = new Set<string>();
  completed = new Map<string, PendingWrite>();
  attempts = 0;

  read(subject: string, path: string) {
    return this.grants.get(subject)?.has(READ) ? this.documents.get(path) : 'denied';
  }

  submit(subject: string, plan: WritePlan) {
    if (!this.grants.get(subject)?.has(WRITE)) return 'denied';
    const done = this.completed.get(plan.id);
    if (done) return done.subject === subject && samePlan(done.plan, plan) ? 'recorded' : 'conflict';
    const waiting = this.pending.get(plan.id);
    if (waiting && (waiting.subject !== subject || !samePlan(waiting.plan, plan))) return 'conflict';
    this.pending.set(plan.id, { subject, plan });
    return 'pending';
  }

  approve(subject: string, plan: WritePlan) {
    const waiting = this.pending.get(plan.id);
    if (!this.grants.get(subject)?.has(APPROVE) || !waiting ||
        waiting.subject === subject || !samePlan(waiting.plan, plan)) return 'denied';
    this.approved.add(plan.id);
    return 'approved';
  }

  resume(subject: string, plan: WritePlan) {
    const done = this.completed.get(plan.id);
    if (done) return done.subject === subject && samePlan(done.plan, plan) ? 'recorded' : 'conflict';
    const waiting = this.pending.get(plan.id);
    if (!this.grants.get(subject)?.has(WRITE) || !waiting || waiting.subject !== subject ||
        !samePlan(waiting.plan, plan) || !this.approved.has(plan.id)) return 'denied';
    this.attempts++;
    this.documents.set(plan.path, plan.content);
    this.completed.set(plan.id, { subject, plan });
    return 'executed';
  }
}

const demo = new InMemorySketch();
const plan = { id: 'request-1', path: 'summary.md', content: 'Meeting summary: ship a small demo.' };
console.log('Weave control-flow sketch - memory only; no Host, files or network');
console.log('1. Read with a read grant:', demo.read('reader', 'meeting.txt'));
console.log('2. Write with only a read grant:', demo.submit('reader', plan));
console.log('3. Writer submits a write:', demo.submit('writer', plan));
console.log('   Target before approval:', demo.documents.get(plan.path) ?? '<not written>');
console.log('4. Separate reviewer approves:', demo.approve('reviewer', plan));
console.log('   Target after approval:', demo.documents.get(plan.path) ?? '<not written>');
console.log('5. Original writer resumes:', demo.resume('writer', plan));
console.log('   Target after resume:', demo.documents.get(plan.path));
console.log('6. Same request ID again:', demo.resume('writer', plan));
console.log('   Effects performed:', demo.attempts);
assert.equal(demo.attempts, 1);
assert.equal(demo.documents.get(plan.path), plan.content);
console.log('The real Host uses signed authority and a durable SQLite journal.');
