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

/** GitHub's heading anchor: lower case, punctuation other than '-' and '_' removed, spaces become '-'. */
export function slug(heading: string): string {
    const text = heading
        .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
        .replace(/<[^>]+>/g, '')
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

const stripInlineCode = (text: string) => text.replace(/(`+)(?:(?!\1).)+\1/g, (match) => ' '.repeat(match.length));

export function anchors(content: string): Set<string> {
    const found = new Set<string>();
    const counts = new Map<string, number>();
    for (const { text } of proseLines(content)) {
        for (const match of text.matchAll(/<a\s+(?:[^>]*\s)?(?:id|name)="([^"]+)"/g)) found.add(match[1]);
        const heading = /^#{1,6}\s+(.+?)\s*#*\s*$/.exec(text);
        if (!heading) continue;
        const base = slug(heading[1]);
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
        for (const match of prose.matchAll(/\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g)) found.push({ target: match[1], line });
        const definition = /^\s{0,3}\[[^\]]+\]:\s*<?(\S+?)>?(?:\s+.*)?$/.exec(prose);
        if (definition) found.push({ target: definition[1], line });
        for (const match of prose.matchAll(/<a\s+[^>]*href="([^"]+)"/g)) found.push({ target: match[1], line });
    }
    return found;
}

const isExternal = (target: string) => /^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith('//');

function resolveTarget(file: string, target: string): { path: string; anchor: string | null } {
    const hash = target.indexOf('#');
    const rawPath = hash === -1 ? target : target.slice(0, hash);
    const anchor = hash === -1 ? null : decodeURIComponent(target.slice(hash + 1));
    const decoded = decodeURIComponent(rawPath.split('?')[0]);
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
                const candidate = match[1].replace(/[),.:;]+$/, '');
                if (!checkedRoots.some((root) => candidate.startsWith(root))) continue;
                if (/[*{}<>$]|\.\.\./.test(candidate)) continue; // Globs and placeholders are not concrete paths.
                const path = candidate.replace(/#.*$/, '').replace(/:\d+(-\d+)?$/, '').replace(/\/$/, '');
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
