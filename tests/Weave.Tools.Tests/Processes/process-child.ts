const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const [mode, directory] = process.argv.slice(2);
const root = path.isAbsolute(directory) ? directory : path.join(require('node:os').tmpdir(), directory);

function publishPid() {
  fs.writeFileSync(path.join(root, 'child.pid.pending'), String(process.pid));
  fs.renameSync(path.join(root, 'child.pid.pending'), path.join(root, 'child.pid'));
}

switch (mode) {
  case 'stdout-limit':
    process.stdout.write('x'.repeat(70_000));
    break;
  case 'stderr-limit':
    process.stderr.write('x'.repeat(70_000));
    break;
  case 'dual':
    process.stdout.write('o'.repeat(50_000));
    process.stderr.write('e'.repeat(50_000));
    break;
  case 'wait':
    publishPid();
    process.stdout.write('ready');
    setTimeout(() => {}, 60_000);
    break;
  case 'overflow-wait': {
    publishPid();
    const waiting = setInterval(() => {
      if (fs.existsSync(path.join(root, 'go'))) {
        clearInterval(waiting);
        process.stdout.write('x'.repeat(70_000));
        setTimeout(() => {}, 60_000);
      }
    }, 10);
    break;
  }
  case 'descendant': {
    spawn(process.execPath, [__filename, 'wait', root], {
      stdio: ['ignore', process.stdout, process.stderr],
      windowsHide: true,
      detached: true,
    }).unref();
    const waiting = setInterval(() => {
      if (fs.existsSync(path.join(root, 'child.pid'))) clearInterval(waiting);
    }, 10);
    break;
  }
  case 'effect-overflow':
    fs.appendFileSync(path.join(root, 'effect'), 'effect');
    process.stdout.write('x'.repeat(70_000));
    break;
  default:
    throw new Error('Unknown test child mode');
}
