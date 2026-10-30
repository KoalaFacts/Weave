import { relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

function safeLine(value, limit) {
    if (typeof value !== 'string') return '';
    const line = value.slice(0, 8192).split(/[\r\n]/, 1)[0];
    // Reject obscured fields before matching credentials; partial ANSI/control stripping can expose values.
    if (/[\p{Cc}\p{Cf}\u2028\u2029]/u.test(line)) return '[nonprinting text omitted]';
    return line
        .replace(/https?:\/\/[^\s"'<>]+/gi, '[URL omitted]')
        .replace(/\b[\w-]*(?:authorization|credentials?|control|password|secret|token|api[_-]?key)[\w-]*(?:["']?\s*[:=]|\s+).+/gi, '[sensitive details omitted]')
        .replace(/\b(?:Bearer|Basic)\s+\S+/gi, '[credential omitted]')
        .replace(/[A-Za-z0-9+/_=-]{24,}/g, '[opaque value omitted]')
        .slice(0, limit);
}

function escapeCommand(value) {
    return value.replaceAll('%', '%25').replaceAll('\r', '%0D').replaceAll('\n', '%0A');
}

function ownedFrames(error, data) {
    // ESM frames use file URLs; CommonJS frames use native paths (backslashes on Windows).
    const markers = [pathToFileURL(resolve('test')).href + '/', resolve('test') + sep];
    const frames = [];
    const stack = typeof error?.stack === 'string' ? error.stack : '';
    for (const line of stack.slice(0, 16384).split('\n').slice(0, 40)) {
        const marker = markers.find(candidate => line.includes(candidate));
        if (!/^\s+at /.test(line) || !marker) continue;
        const frame = line.slice(line.indexOf(marker) + marker.length).replaceAll('\\', '/')
            .match(/^((?:browser\/[A-Za-z0-9_.-]+|harness)\.ts):(\d+):(\d+)\)?$/);
        if (frame) frames.push({ file: 'test/' + frame[1], line: frame[2], column: frame[3] });
        if (frames.length === 3) break;
    }
    if (!frames.length && typeof data.file === 'string') {
        const path = data.file.startsWith('file:') ? fileURLToPath(data.file) : data.file;
        const file = relative(process.cwd(), path).replaceAll(sep, '/');
        if (/^test\/browser\/[A-Za-z0-9_.-]+\.ts$/.test(file) && Number.isSafeInteger(data.line))
            frames.push({ file, line: data.line, column: data.column });
    }
    return frames;
}

// Standard node:test reporter: the runner retains its own failure exit status.
// Only failure metadata is annotated; stdout, headers, bodies and assertion values stay out.
export default async function* githubBrowserReporter(events) {
    let failures = 0;
    for await (const { type, data } of events) {
        if (type !== 'test:fail') continue;
        if (++failures > 10) {
            if (failures === 11) yield '::notice title=Worker browser failures::Additional failure annotations omitted after 10 failures.\n';
            continue;
        }
        let error = data.details.error;
        for (let depth = 0; depth < 4 && error?.cause && typeof error.cause === 'object'; depth++) error = error.cause;
        const frames = ownedFrames(error, data);
        const location = frames[0];
        // File names are restricted above; no arbitrary text enters command properties.
        const properties = location ? `file=typescript/mailbox-worker/${location.file},line=${location.line},` : '';
        const message = [safeLine(data.name, 160),
            [safeLine(error?.name, 40), safeLine(error?.code, 64), safeLine(error?.message, 512)].filter(Boolean).join(': '),
            ...frames.map(frame => `${frame.file}:${frame.line}:${frame.column}`)].join('\n');
        yield `::error ${properties}title=Worker browser failure::${escapeCommand(message)}\n`;
    }
}
