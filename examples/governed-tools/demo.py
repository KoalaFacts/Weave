#!/usr/bin/env python3
"""Illustrate Weave's governed operation flow without a Host or external effects."""

from dataclasses import dataclass


READ = 'tool:files:invoke:read_file'
WRITE = 'tool:files:invoke:write_file'
APPROVE = 'tool:files:approve:write_file'


@dataclass(frozen=True)
class WritePlan:
    invocation_id: str
    path: str
    content: str


class InMemorySketch:
    def __init__(self):
        self.documents = {'meeting.txt': 'Meeting: ship a small demo.'}
        self.grants = {
            'reader': {READ},
            'writer': {WRITE},
            'reviewer': {APPROVE},
        }
        self.pending = {}
        self.approved = set()
        self.completed = {}
        self.attempts = 0

    def read(self, subject, path):
        if READ not in self.grants[subject]:
            return 'denied'
        return self.documents[path]

    def submit(self, subject, plan):
        if WRITE not in self.grants[subject]:
            return 'denied'
        if plan.invocation_id in self.completed:
            return 'recorded' if self.completed[plan.invocation_id] == (subject, plan) else 'conflict'
        previous = self.pending.get(plan.invocation_id)
        if previous is not None and previous != (subject, plan):
            return 'conflict'
        self.pending[plan.invocation_id] = (subject, plan)
        return 'pending'

    def approve(self, subject, plan):
        requester, stored = self.pending[plan.invocation_id]
        if APPROVE not in self.grants[subject] or subject == requester or stored != plan:
            return 'denied'
        self.approved.add(plan.invocation_id)
        return 'approved'

    def resume(self, subject, plan):
        if plan.invocation_id in self.completed:
            return 'recorded' if self.completed[plan.invocation_id] == (subject, plan) else 'conflict'
        if (WRITE not in self.grants[subject]
                or self.pending.get(plan.invocation_id) != (subject, plan)
                or plan.invocation_id not in self.approved):
            return 'denied'
        self.attempts += 1
        self.documents[plan.path] = plan.content
        self.completed[plan.invocation_id] = (subject, plan)
        return 'executed'


def main():
    demo = InMemorySketch()
    plan = WritePlan('request-1', 'summary.md', 'Meeting summary: ship a small demo.')
    print('Weave control-flow sketch - memory only; no Host, files or network')
    print('1. Read with a read grant:', demo.read('reader', 'meeting.txt'))
    print('2. Write with only a read grant:', demo.submit('reader', plan))
    print('3. Writer submits a write:', demo.submit('writer', plan))
    print('   Target before approval:', demo.documents.get(plan.path, '<not written>'))
    print('4. Separate reviewer approves:', demo.approve('reviewer', plan))
    print('   Target after approval:', demo.documents.get(plan.path, '<not written>'))
    print('5. Original writer resumes:', demo.resume('writer', plan))
    print('   Target after resume:', demo.documents[plan.path])
    print('6. Same request ID again:', demo.resume('writer', plan))
    print('   Effects performed:', demo.attempts)
    assert demo.attempts == 1 and demo.documents[plan.path] == plan.content
    print('The real Host uses signed authority and a durable SQLite journal.')


if __name__ == '__main__':
    main()
