import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync, spawnSync} from 'node:child_process';
import {mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';

// Exercise the actual shell gate from the reviewed manual workflow, not an
// independent reimplementation that could diverge from deployment behavior.
function releaseGate() {
  const lines=readFileSync(new URL('../../.github/workflows/provision-weave-access.yml',import.meta.url),'utf8').split(/\r?\n/);
  const at=lines.findIndex(line=>line.trim()==='- name: Ensure the checked out release is the reviewed main commit');
  assert.notEqual(at,-1,'the manual provisioning gate must exist');
  assert.equal(lines[at+1]?.trim(),'run: |');
  const commands:string[]=[];
  for(let i=at+2;i<lines.length&&lines[i].startsWith('          ');i++)
    commands.push(lines[i].slice(10));
  assert.ok(commands.length>=2,'the gate must validate both checked-out SHA and current main');
  return commands.join('\n');
}

test('manual provisioning refuses an outdated dispatch SHA even if it is an ancestor of main',()=>{
  const cwd=mkdtempSync(join(tmpdir(),'weave-access-dispatch-'));
  const git=(...args:string[])=>execFileSync('git',args,{cwd,encoding:'utf8'}).trim();
  const run=(sha:string)=>spawnSync('bash',['-eu','-c',releaseGate()],{
    cwd,encoding:'utf8',env:{...process.env,GITHUB_SHA:sha}
  });
  try {
    git('init','-q');
    git('config','user.name','Weave CI Regression');
    git('config','user.email','weave-test@example.invalid');
    writeFileSync(join(cwd,'marker'),'first\n');
    git('add','marker');
    git('commit','-qm','first');
    const old=git('rev-parse','HEAD');
    writeFileSync(join(cwd,'marker'),'second\n');
    git('add','marker');
    git('commit','-qm','second');
    const current=git('rev-parse','HEAD');
    git('update-ref','refs/remotes/origin/main',current);
    git('checkout','-q','--detach',old);
    assert.notEqual(run(old).status,0,'an old SHA must not create an Access app after main advances');
    git('checkout','-q','--detach',current);
    assert.equal(run(current).status,0,'the current reviewed SHA must still be allowed');
  } finally {
    rmSync(cwd,{recursive:true,force:true});
  }
});
