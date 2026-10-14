// Real child lifecycle regressions; replace only the selected fixture endpoint/host.
import assert from 'node:assert/strict';
import cp from 'node:child_process';
import { existsSync, rmSync } from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import { fileURLToPath } from 'node:url';
const [stage, demo] = process.argv.slice(2);
assert(['readiness', 'rpc', 'cooperative-error', 'cooperative-result', 'unknown', 'duplicate'].includes(stage));
const cooperative = !['readiness', 'rpc'].includes(stage);
const original = cp.spawn, children = [], closed = new Set();
let selected, cancelled = false, rescued = false, started, root, commandsAfterCancel = 0;
function alive(pid) {
    try { process.kill(pid, 0); return true; }
    catch (error) { if (error.code === 'ESRCH') return false; throw error; }
}
const cancel = () => {
    if (cancelled) return;
    assert(alive(selected.pid), 'Live signal-zero positive control failed');
    cancelled = true; started = Date.now(); process.emit('SIGTERM');
};
cp.spawn = (executable, args, options) => {
    root ??= options.cwd;
    const replace = !selected && (stage === 'readiness' ? executable === 'dotnet' : args[0]?.endsWith('/agent.ts') || args[0]?.endsWith('\\agent.ts'));
    let child;
    if (replace) {
        const ready = stage !== 'readiness' ? 'console.log(JSON.stringify({ready:true,pid:process.pid,mailboxId:process.env.WEAVE_DEMO_MAILBOX,endpoint:"http://127.0.0.1:1/direct"}));' : 'console.log("probe ready");';
        const marker = stage === 'rpc' ? 'process.stdin.on("data",()=>process.stderr.write("probe RPC pending\\n"));' : '';
        const closing = `let input, closing=false;
            const close=()=>{if(closing)return;closing=true;
                if(input){const response=${stage === 'cooperative-result' ? '{id:input.id,result:{status:200,value:"cancelled fixture result"}}' : '{id:input.id,error:"Endpoint fixture command failed"}'};
                    if(${stage === 'unknown'})response.id++;console.log(JSON.stringify(response));
                    if(${stage === 'duplicate'})console.log(JSON.stringify(response));}
                setTimeout(()=>process.exit(0),20);};
            process.on("SIGTERM",close);
            require("node:readline").createInterface({input:process.stdin}).on("line",line=>{
                const value=JSON.parse(line);if(value.fixtureClose){close();return;}
                input=value;process.stderr.write("probe RPC pending\\n");});`;
        child = original(process.execPath, ['-e', `${cooperative ? closing : 'process.on("SIGTERM",()=>{});'}${ready}${marker}setInterval(()=>{},1000);`], options);
        selected = child;
        (stage === 'readiness' ? child.stdout : child.stderr).once('data', () => setTimeout(cancel, 50));
        // Windows SIGTERM is forceful; use real fixture IPC for the cooperative reply boundary there.
        if (cooperative && process.platform === 'win32') {
            const kill = child.kill.bind(child);
            child.kill = signal => signal === 'SIGTERM' ? child.stdin.write('{"fixtureClose":true}\n') : kill(signal);
        }
    } else child = original(executable, args, options);
    const write = child.stdin.write.bind(child.stdin);
    child.stdin.write = (...values) => {
        const value = JSON.parse(String(values[0]));
        if (cancelled && value.id !== undefined) commandsAfterCancel++;
        return write(...values);
    };
    children.push(child); child.once('close', () => closed.add(child.pid)); return child;
};
syncBuiltinESMExports();
process.argv = [process.execPath, fileURLToPath(demo), '--json', ...(cooperative ? [] : ['--state-directory', 'retained'])];
const watchdog = setTimeout(() => {
    rescued = true;
    for (const child of children) if (alive(child.pid)) child.kill('SIGKILL');
}, 11000);
let outcome, errorMessage;
try { await import(demo); outcome = 'unexpected success'; }
catch (error) { outcome = error.name; errorMessage = error.message; }
finally { clearTimeout(watchdog); cp.spawn = original; syncBuiltinESMExports(); }
assert(selected && cancelled, 'Probe never reached its selected cancellation boundary');
const result = { stage, livePositiveControl: true, settledWithoutWatchdog: !rescued,
    allChildrenClosed: children.every(child => closed.has(child.pid)),
    drainsObserved: children.every(child => child.stdout.readableEnded && child.stderr.readableEnded),
    stoppedPids: children.map(child => child.pid), stoppedSignalControls: children.every(child => !alive(child.pid)),
    noncooperativeSignal: selected.signalCode, selectedExitCode: selected.exitCode,
    elapsedAfterCancelMs: Date.now() - started, commandsAfterCancel, outcome, errorMessage,
    defaultStateRemoved: cooperative && !existsSync(root) };
if (cooperative && existsSync(root) && result.allChildrenClosed && result.stoppedSignalControls) rmSync(root, { recursive: true });
console.log(JSON.stringify(result));
