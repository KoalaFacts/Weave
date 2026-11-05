// Documentation checker: broken relative links and anchors, docs unreachable from the docs index,
// and repository paths or npm scripts named in current docs that do not exist.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, normalize, posix } from 'node:path';
import { fileURLToPath } from 'node:url';

export type Problem = { file: string; line: number; message: string };

export type Repo = {
    /** Repository-relative POSIX paths of tracked Markdown files with their content. */
    docs: Map<string, string>;
    /** Whether a repository-relative path (file or directory) exists. */
    exists: (path: string) => boolean;
    /** npm script names declared by each package.json, keyed by the package directory. */
    npmScripts: Map<string, Set<string>>;
};

export type Allowance = { file: string; target: string; reason: string };

export const docsIndex = 'docs/README.md';

// Paths in inline code are checked only when they start with one of these tracked top-level folders.
const checkedRoots = ['csharp/', 'typescript/', 'scripts/', 'docs/', 'protocol/', 'examples/', 'plugins/', '.github/', 'python/'];

/** Records and guides marked historical keep the paths of their time; links in them are still checked. */
export function isHistorical(file: string, content: string): boolean {
    return /^docs\/implementation\/\d{4}-\d{2}-\d{2}-/.test(file) || /^> \*\*Historical guide\b/m.test(content);
}

const isTagStart = (text: string, at: number) => text[at] === '<' && /[A-Za-z/]/.test(text[at + 1] ?? '');

/**
 * Heading text without inline HTML tags (`<code>x</code>` → `x`). As on GitHub, `<` starts a tag only before a letter or
 * `/`, so `x < y > z` keeps its text; the slug filter then drops the punctuation.
 */
function withoutTags(text: string): string {
    let out = '';
    let i = 0;
    while (i < text.length) {
        if (!isTagStart(text, i)) { out += text[i++]; continue; }
        const close = text.indexOf('>', i + 1);
        if (close === -1) return out + text.slice(i);
        i = close + 1;
    }
    return out;
}

/** Inline links and images reduced to their text: `[a](b)` → `a`. Single forward scan. */
function linkTexts(text: string): string {
    let out = '';
    let i = 0;
    while (i < text.length) {
        const open = text.indexOf('[', i);
        if (open === -1) return out + text.slice(i);
        const close = text.indexOf(']', open + 1);
        if (close === -1) return out + text.slice(i);
        const inner = text.slice(open + 1, close);
        if (inner.includes('[') || text[close + 1] !== '(') { out += text.slice(i, close + 1); i = close + 1; continue; }
        const end = text.indexOf(')', close + 2);
        if (end === -1) return out + text.slice(i);
        out += text.slice(i, open > 0 && text[open - 1] === '!' ? open - 1 : open) + inner;
        i = end + 1;
    }
    return out;
}

/** GitHub's heading anchor: lower case, punctuation other than '-' and '_' removed, spaces become '-'. */
export function slug(heading: string): string {
    const text = linkTexts(withoutTags(heading))
        .replace(/[`*]/g, '')
        .trim()
        .toLowerCase();
    return text.replace(/[^\p{L}\p{N}\p{M} _-]/gu, '').replace(/ /g, '-');
}

type Line = { text: string; line: number };

/** Lines outside fenced code blocks, with inline code kept (callers decide what to do with it). */
function proseLines(content: string): Line[] {
    const lines: Line[] = [];
    let fence: string | null = null;
    content.split('\n').forEach((text, index) => {
        const marker = /^\s*(`{3,}|~{3,})/.exec(text);
        if (marker) {
            if (fence === null) fence = marker[1][0].repeat(marker[1].length);
            else if (marker[1].startsWith(fence)) fence = null;
            return;
        }
        if (fence === null) lines.push({ text, line: index + 1 });
    });
    return lines;
}

/**
 * Blanks inline code spans on one line, keeping column positions: a backtick run opens a span closed by the next run of
 * exactly the same length. Runs are measured once, so the scan is linear in the line plus its runs.
 */
function stripInlineCode(text: string): string {
    const runs: { start: number; length: number }[] = [];
    for (let i = 0; i < text.length;) {
        if (text[i] !== '`') { i++; continue; }
        const start = i;
        while (text[i] === '`') i++;
        runs.push({ start, length: i - start });
    }
    // For each run, the index of the next run of the same length.
    const next = new Array<number>(runs.length).fill(-1);
    const latest = new Map<number, number>();
    for (let r = runs.length - 1; r >= 0; r--) {
        next[r] = latest.get(runs[r].length) ?? -1;
        latest.set(runs[r].length, r);
    }
    const chars = text.split('');
    for (let r = 0; r < runs.length;) {
        const close = next[r];
        if (close === -1) { r++; continue; }
        const end = runs[close].start + runs[close].length;
        for (let c = runs[r].start; c < end; c++) chars[c] = ' ';
        r = close + 1;
    }
    return chars.join('');
}

/** Attribute text of each `<a …>` tag on a line. */
function anchorTags(text: string): string[] {
    const tags: string[] = [];
    for (let i = text.indexOf('<a'); i !== -1; i = text.indexOf('<a', i + 2)) {
        if (text[i + 2] !== ' ' && text[i + 2] !== '\t') continue;
        const close = text.indexOf('>', i + 3);
        if (close === -1) break;
        tags.push(text.slice(i + 3, close));
        i = close - 1;
    }
    return tags;
}

/** The double-quoted value of one attribute in a tag's attribute text, or null. */
function attribute(attributes: string, name: string): string | null {
    const key = name + '="';
    for (let at = attributes.indexOf(key); at !== -1; at = attributes.indexOf(key, at + 1)) {
        if (at > 0 && attributes[at - 1] !== ' ' && attributes[at - 1] !== '\t') continue;
        const end = attributes.indexOf('"', at + key.length);
        return end === -1 ? null : attributes.slice(at + key.length, end);
    }
    return null;
}

/** A heading line's text without its marker and closing hashes, or null when the line is not an ATX heading. */
function headingText(line: string): string | null {
    const marker = /^#{1,6}[ \t]/.exec(line);
    if (!marker) return null;
    let text = line.slice(marker[0].length).trim();
    let end = text.length;
    while (end > 0 && text[end - 1] === '#') end--;
    if (end < text.length && (end === 0 || text[end - 1] === ' ' || text[end - 1] === '\t')) text = text.slice(0, end).trim();
    return text === '' ? null : text;
}

/** A Markdown link destination without angle brackets or a title. */
function destination(inner: string): string | null {
    const trimmed = inner.trim();
    if (trimmed.startsWith('<')) {
        const close = trimmed.indexOf('>');
        return close > 1 ? trimmed.slice(1, close) : null;
    }
    const target = trimmed.split(/[ \t]/)[0];
    return target === '' ? null : target;
}

export function anchors(content: string): Set<string> {
    const found = new Set<string>();
    const counts = new Map<string, number>();
    for (const { text } of proseLines(content)) {
        for (const tag of anchorTags(stripInlineCode(text))) {
            const id = attribute(tag, 'id') ?? attribute(tag, 'name');
            if (id) found.add(id);
        }
        const heading = headingText(text);
        if (heading === null) continue;
        const base = slug(heading);
        const seen = counts.get(base) ?? 0;
        counts.set(base, seen + 1);
        found.add(seen === 0 ? base : `${base}-${seen}`);
    }
    return found;
}

export function links(content: string): { target: string; line: number }[] {
    const found: { target: string; line: number }[] = [];
    for (const { text, line } of proseLines(content)) {
        const prose = stripInlineCode(text);
        for (let at = prose.indexOf(']('); at !== -1; at = prose.indexOf('](', at + 2)) {
            const end = prose.indexOf(')', at + 2);
            if (end === -1) break;
            const target = destination(prose.slice(at + 2, end));
            if (target) found.push({ target, line });
            at = end - 1;
        }
        const definition = /^ {0,3}\[[^\]]+\]:([^\n]*)$/.exec(prose);
        const defined = definition && destination(definition[1]);
        if (defined) found.push({ target: defined, line });
        for (const tag of anchorTags(prose)) {
            const href = attribute(tag, 'href');
            if (href) found.push({ target: href, line });
        }
    }
    return found;
}

/** `file.ts:12` or `file.ts:12-20` names a file at a line. */
function withoutLineSuffix(path: string): string {
    const colon = path.lastIndexOf(':');
    return colon !== -1 && /^\d+(-\d+)?$/.test(path.slice(colon + 1)) ? path.slice(0, colon) : path;
}

const isExternal = (target: string) => /^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith('//');

const decode = (text: string) => { try { return decodeURIComponent(text); } catch { return null; } };

function resolveTarget(file: string, target: string): { path: string; anchor: string | null } {
    const hash = target.indexOf('#');
    const rawPath = hash === -1 ? target : target.slice(0, hash);
    // A malformed escape resolves to a path no repository holds, so it is reported as a broken link.
    const anchor = hash === -1 ? null : decode(target.slice(hash + 1)) ?? '\0';
    const decoded = decode(rawPath.split('?')[0]) ?? '\0malformed';
    if (decoded === '') return { path: file, anchor };
    const joined = decoded.startsWith('/') ? decoded.slice(1) : posix.join(posix.dirname(file), decoded);
    return { path: posix.normalize(joined).replace(/\/$/, ''), anchor };
}

type Reporter = (file: string, line: number, target: string, message: string) => void;

export function checkLinks(repo: Repo, report: Reporter): void {
    const anchorCache = new Map<string, Set<string>>();
    for (const [file, content] of repo.docs) {
        for (const { target, line } of links(content)) {
            if (isExternal(target)) continue;
            const { path, anchor } = resolveTarget(file, target);
            const pathMissing = path.startsWith('..') || !repo.exists(path);
            let message: string | null = null;
            if (pathMissing) message = `broken link: ${target} (no ${path})`;
            else if (anchor !== null && path.endsWith('.md') && !/^L\d+(-L\d+)?$/.test(anchor)) {
                const content = repo.docs.get(path);
                if (content !== undefined) {
                    let known = anchorCache.get(path);
                    if (!known) anchorCache.set(path, (known = anchors(content)));
                    if (!known.has(anchor)) message = `broken anchor: ${target} (no heading or id "${anchor}" in ${path})`;
                }
            }
            if (message !== null) report(file, line, target, message);
        }
    }
}

/** Every Markdown file under docs/ must be reachable from the docs index by following relative links. */
export function checkIndex(repo: Repo, rootReadme = 'README.md'): Problem[] {
    const problems: Problem[] = [];
    if (!repo.docs.has(docsIndex)) return [{ file: docsIndex, line: 0, message: 'the docs index is missing' }];
    const readme = repo.docs.get(rootReadme);
    if (readme !== undefined && !links(readme).some(({ target }) => !isExternal(target) && resolveTarget(rootReadme, target).path === docsIndex)) {
        problems.push({ file: rootReadme, line: 0, message: `does not link to the docs index ${docsIndex}` });
    }
    const reached = new Set<string>([docsIndex]);
    const queue = [docsIndex];
    while (queue.length > 0) {
        const file = queue.shift() as string;
        for (const { target } of links(repo.docs.get(file) ?? '')) {
            if (isExternal(target)) continue;
            const { path } = resolveTarget(file, target);
            if (path.startsWith('docs/') && repo.docs.has(path) && !reached.has(path)) {
                reached.add(path);
                queue.push(path);
            }
        }
    }
    for (const file of repo.docs.keys()) {
        if (file.startsWith('docs/') && !reached.has(file)) problems.push({ file, line: 0, message: `not reachable from ${docsIndex}; list it there or in a page it links to` });
    }
    return problems;
}

/** Repository paths in inline code and `npm run` scripts named in current (non-historical) docs must exist. */
export function checkNamedPaths(repo: Repo, report: Reporter): void {
    for (const [file, content] of repo.docs) {
        if (isHistorical(file, content)) continue;
        const lines = content.split('\n');
        lines.forEach((text, index) => {
            for (const match of text.matchAll(/`([^`\s]+)`/g)) {
                let candidate = match[1];
                while (candidate !== '' && '),.:;'.includes(candidate[candidate.length - 1])) candidate = candidate.slice(0, -1);
                if (!checkedRoots.some((root) => candidate.startsWith(root))) continue;
                if (/[*{}<>$]|\.\.\./.test(candidate)) continue; // Globs and placeholders are not concrete paths.
                const path = withoutLineSuffix(candidate.split('#')[0]).replace(/\/$/, '');
                // A package README may name its own files relative to the package (`scripts/deploy.ts`).
                if (repo.exists(path) || repo.exists(posix.join(posix.dirname(file), path))) continue;
                report(file, index + 1, candidate, `named path does not exist: ${candidate}`);
            }
        });
        const packageDir = posix.dirname(file);
        lines.forEach((text, index) => {
            for (const match of text.matchAll(/\bnpm run ([a-z0-9:_-]+)/gi)) {
                const name = match[1];
                const own = repo.npmScripts.get(packageDir);
                const known = own ? own.has(name) : [...repo.npmScripts.values()].some((scripts) => scripts.has(name));
                if (!known) report(file, index + 1, `npm run ${name}`, `npm script does not exist: ${name}${own ? ` in ${packageDir}/package.json` : ''}`);
            }
        });
    }
}

/**
 * Runs every check. An allowance excuses one exact (file, target) finding, such as a dead link a historical record
 * keeps or a path named only to say it no longer exists; an allowance that excuses nothing is itself a problem.
 */
export function check(repo: Repo, allowances: Allowance[]): Problem[] {
    const problems: Problem[] = [];
    const used = new Set<Allowance>();
    const report: Reporter = (file, line, target, message) => {
        const allowance = allowances.find((entry) => entry.file === file && entry.target === target);
        if (allowance) used.add(allowance);
        else problems.push({ file, line, message });
    };
    checkLinks(repo, report);
    checkNamedPaths(repo, report);
    problems.push(...checkIndex(repo));
    for (const allowance of allowances) {
        if (!used.has(allowance)) problems.push({ file: allowance.file, line: 0, message: `stale allowance: ${allowance.target} is no longer reported here; remove it` });
    }
    return problems;
}

export function loadRepo(root: string): Repo {
    const tracked = execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' }).split('\0').filter(Boolean);
    const docs = new Map<string, string>();
    const npmScripts = new Map<string, Set<string>>();
    for (const file of tracked) {
        if (file.includes('node_modules/')) continue;
        if (file.endsWith('.md')) docs.set(file, readFileSync(join(root, file), 'utf8'));
        if (file.endsWith('package.json') && !file.endsWith('/.claude-plugin/package.json')) {
            try {
                const scripts = (JSON.parse(readFileSync(join(root, file), 'utf8')) as { scripts?: Record<string, string> }).scripts ?? {};
                npmScripts.set(posix.dirname(file) === '.' ? '' : posix.dirname(file), new Set(Object.keys(scripts)));
            } catch {
                // A package.json that is not JSON has no scripts to name; other tooling reports it.
            }
        }
    }
    const trackedSet = new Set(tracked);
    const exists = (path: string) => trackedSet.has(path) || (existsSync(join(root, path)) && (statSync(join(root, path)).isDirectory() ? tracked.some((file) => file.startsWith(`${path}/`)) : false));
    return { docs, exists, npmScripts };
}

export function loadAllowances(path: string): Allowance[] {
    const parsed = JSON.parse(readFileSync(path, 'utf8')) as { allowances: Allowance[] };
    for (const entry of parsed.allowances) {
        if (typeof entry.file !== 'string' || typeof entry.target !== 'string' || typeof entry.reason !== 'string' || entry.reason.trim() === '') {
            throw new Error(`every allowance needs file, target and a reason: ${JSON.stringify(entry)}`);
        }
    }
    return parsed.allowances;
}

if (process.argv[1] && normalize(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const here = dirname(fileURLToPath(import.meta.url));
    const root = normalize(join(here, '..', '..'));
    const problems = check(loadRepo(root), loadAllowances(join(here, 'allowances.json')));
    for (const problem of problems) console.error(`${problem.file}:${problem.line}: ${problem.message}`);
    if (problems.length > 0) {
        console.error(`check-docs: ${problems.length} problem(s)`);
        process.exitCode = 1;
    } else {
        console.log('check-docs: links, anchors, index and named paths are consistent');
    }
}
