/** Explicit anonymous probe; prints only allowlisted diagnostics, never tokens. */
import {inspectOAuthDiscovery} from './discovery.ts';
const args=process.argv.slice(2);
if(args.length<1||args.length>2){console.error('Usage: node probe.ts <HTTPS resource URL> [exact expected issuer]');process.exitCode=2;}
else{
    try{
        const report=await inspectOAuthDiscovery(args[0],args[1]);
        console.log(JSON.stringify({experiment:'oauth-provider-agnostic',...report},null,2));
        // Absence is a measured outcome, not an auth success or a failing unit test.
        if(report.outcome.endsWith('unavailable')||report.outcome==='metadata-rejected')process.exitCode=1;
    }catch{console.error('OAuth discovery probe rejected its target or failed.');process.exitCode=1;}
}
