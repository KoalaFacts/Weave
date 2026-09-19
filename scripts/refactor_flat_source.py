"""One-shot, reviewed migration of the pre-1.0 project graph to feature-first source.

Run from a clean feature branch. Refuses an already migrated checkout. No database,
remote resource, package version, git ref, or runtime authorization is changed.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MERGED = {
    'src/Foundation/Weave.Shared/Weave.Shared.csproj',
    'src/Workspaces/Weave.Workspaces/Weave.Workspaces.csproj',
    'src/Security/Weave.Security/Weave.Security.csproj',
    'src/Tools/Weave.Tools/Weave.Tools.csproj',
}
EXTENSIONS = ('Mcp', 'CliTools', 'OpenApi', 'DirectHttp', 'FileSystem', 'Dapr')


def project_target(path: str) -> str:
    if path in MERGED:
        return 'src/Weave.csproj'
    name = Path(path).stem
    if name.endswith('.Tests'):
        return f'tests/{name}/{name}.csproj'
    if name == 'Weave.SourceGen':
        return 'tools/Weave.SourceGen/Weave.SourceGen.csproj'
    if name == 'Weave.Agents':
        return 'extensions/Weave.AgentRuntime/Weave.AgentRuntime.csproj'
    if name == 'Weave.Silo':
        return 'hosts/Weave.Host/Weave.Host.csproj'
    if name in {'Weave.AppHost', 'Weave.ServiceDefaults', 'Weave.Cli', 'Weave.Dashboard'}:
        return f'hosts/{name}/{name}.csproj'
    return f'extensions/{name}/{name}.csproj'


def slice_path(base: str, tail: str) -> str:
    """Co-locate self-contained command/query files with their use case."""
    name = Path(tail).name
    match = re.fullmatch(r'(.+?)(Command|Query)\.cs', name)
    if match:
        return f'{base}/{match[1]}/{name}'
    return f'{base}/{tail}'


def core_target(root: str, tail: str) -> str | None:
    name = Path(tail).name
    if name.endswith('.csproj') or name == 'packages.lock.json':
        return None
    if name == 'VirtualActorUsings.cs':
        return None
    head, _, rest = tail.partition('/')
    if root.endswith('Weave.Shared'):
        if head == 'Ids':
            owners = {'AgentId': 'Agents', 'AgentTaskId': 'Agents', 'WorkspaceId': 'Workspaces',
                      'TemplateId': 'Workspaces/Templates', 'ChannelId': 'Channels',
                      'UserId': 'Users', 'SkillId': 'Skills', 'EpisodeId': 'Memory',
                      'ContainerId': 'Resources', 'NetworkId': 'Resources',
                      'MarketplaceItemId': 'Plugins/Catalog'}
            return f'src/{owners[Path(name).stem]}/{name}'
        bases = {'Capabilities': 'Authority', 'Secrets': 'Credentials',
                 'Plugins': 'Plugins', 'Strings': 'Composition/Text',
                 'VirtualActors': 'Composition/VirtualActors', 'Lifecycle': 'Composition/Lifecycle',
                 'Events': 'Composition/Events', 'Cqrs': 'Composition/Cqrs'}
        return f'src/{bases[head]}/{rest}' if rest else f'src/Composition/{name}'
    if root.endswith('Weave.Workspaces'):
        if head == 'Lifecycle':
            return slice_path('src/Workspaces', rest)
        if head == 'Manifest':
            return slice_path('src/Workspaces/Manifest', rest)
        return f'src/Workspaces/{tail}'
    if root.endswith('Weave.Security'):
        bases = {'Tokens': 'Authority/Tokens', 'Audit': 'Audit', 'Events': 'Authority',
                 'Queries': 'Audit', 'Scanning': 'Credentials/Scanning', 'Vault': 'Credentials/Vault',
                 'Plugins': 'Credentials/Plugins', 'Proxy': 'Credentials/Proxy', 'Actors': 'Credentials/Proxy'}
        return slice_path(f'src/{bases[head]}', rest)
    if root.endswith('Weave.Tools'):
        if head == 'Connectors':
            if name == 'IToolConnector.cs':
                return 'src/Invocations/IToolConnector.cs'
            if name.endswith('Config.cs'):
                return f'src/Plugins/Operations/{name}'
            if 'Mcp' in name:
                extension = 'Mcp'
            elif name.startswith('Cli'):
                extension = 'CliTools'
            elif name.startswith('OpenApi'):
                extension = 'OpenApi'
            elif name.startswith('DirectHttp'):
                extension = 'DirectHttp'
            elif name.startswith('FileSystem'):
                extension = 'FileSystem'
            elif name.startswith('Dapr'):
                extension = 'Dapr'
            else:
                raise ValueError(f'unclassified connector {tail}')
            return f'extensions/Weave.{extension}/InvokeTool/{name}'
        bases = {'Tool': 'Invocations/InvokeTool', 'Builders': 'Invocations/InvokeTool',
                 'Mapping': 'Plugins/DiscoverTools', 'Discovery': 'Plugins/DiscoverTools',
                 'Marketplace': 'Plugins/Catalog'}
        if not rest:
            return f'src/Invocations/InvokeTool/{name}'
        return f'src/{bases[head]}/{rest}'
    raise ValueError(root)


def relative_reference(owner: str, target: str) -> str:
    return os.path.relpath(ROOT / target, (ROOT / owner).parent).replace(os.sep, '/')


def write_xml(path: Path, root: ET.Element) -> None:
    ET.indent(root, space='  ')
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(ET.tostring(root, encoding='unicode') + '\n', encoding='utf-8')


def main() -> None:
    if (ROOT / 'src/Weave.csproj').exists():
        raise SystemExit('Refusing to migrate an already migrated checkout.')
    projects = sorted(p.relative_to(ROOT).as_posix() for p in (ROOT / 'src').rglob('*.csproj'))
    if not MERGED.issubset(projects):
        raise SystemExit('Expected baseline projects are missing; review the migration map first.')
    project_map = {p: project_target(p) for p in projects}
    original_projects = {p: (ROOT / p).read_text(encoding='utf-8') for p in projects}
    roots = sorted((str(Path(p).parent) for p in projects), key=len, reverse=True)
    files = sorted(p for p in (ROOT / 'src').rglob('*') if p.is_file())
    moves: dict[str, str | None] = {}
    original_hashes: dict[str, str] = {}
    for path in files:
        source = path.relative_to(ROOT).as_posix()
        owner = next((r for r in roots if source.startswith(r + '/')), None)
        if owner is None:
            raise ValueError(f'Unowned source file: {source}')
        tail = source[len(owner) + 1:]
        project = owner + '/' + Path(owner).name + '.csproj'
        if project in MERGED:
            destination = core_target(owner, tail)
        else:
            destination_root = str(Path(project_map[project]).parent)
            destination = destination_root + '/' + tail
            if source == project:
                destination = project_map[project]
            if project.endswith('/Weave.Agents.csproj'):
                group, _, remainder = tail.partition('/')
                group_map = {'Lifecycle': 'Agents', 'Pipeline': 'Chat', 'ToolRegistry': 'PluginConnections'}
                if remainder:
                    destination = slice_path(destination_root + '/' + group_map.get(group, group), remainder)
        if destination and (ROOT / destination).exists():
            raise ValueError(f'Existing destination would be overwritten: {destination}')
        moves[source] = destination
        if path.suffix == '.cs':
            original_hashes[source] = hashlib.sha256(path.read_bytes()).hexdigest()
    targets = [v for v in moves.values() if v]
    if len(targets) != len(set(targets)):
        raise ValueError('Destination collision; no source files moved.')
    for source, destination in moves.items():
        if destination is None:
            (ROOT / source).unlink()
        else:
            out = ROOT / destination
            out.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(ROOT / source), str(out))
    for directory in sorted((p for p in (ROOT / 'src').rglob('*') if p.is_dir()),
                            key=lambda p: len(p.parts), reverse=True):
        if not any(directory.iterdir()):
            directory.rmdir()

    product = ET.Element('Project', Sdk='Microsoft.NET.Sdk')
    properties = ET.SubElement(product, 'PropertyGroup')
    for name, value in {
        'AssemblyName': 'Weave.Product', 'RootNamespace': 'Weave', 'PackageId': 'Weave',
        'Description': 'Composable Weave features, organized as vertical slices.',
        'GenerateCqrsRegistration': 'false',
        'NoWarn': '$(NoWarn);NU1510',
    }.items():
        ET.SubElement(properties, name).text = value
    packages: dict[str, ET.Element] = {}
    friends = set()
    for source in sorted(MERGED):
        tree = ET.fromstring(original_projects[source])
        for item in tree.iter('PackageReference'):
            packages[item.attrib['Include']] = item
        friends.update(x.attrib['Include'] for x in tree.iter('InternalsVisibleTo'))
    group = ET.SubElement(product, 'ItemGroup')
    for item in sorted(packages.values(), key=lambda p: p.attrib['Include']):
        group.append(item)
    group = ET.SubElement(product, 'ItemGroup')
    for friend in sorted(friends):
        ET.SubElement(group, 'InternalsVisibleTo', Include=friend)
    group = ET.SubElement(product, 'ItemGroup')
    ET.SubElement(group, 'CompilerVisibleProperty', Include='GenerateCqrsRegistration')
    ET.SubElement(group, 'ProjectReference', Include='../tools/Weave.SourceGen/Weave.SourceGen.csproj',
                  OutputItemType='Analyzer', ReferenceOutputAssembly='false',
                  GlobalPropertiesToRemove='PublishTrimmed;PublishAot;PublishSingleFile;PublishReadyToRun;SelfContained;RuntimeIdentifier')
    write_xml(ROOT / 'src/Weave.csproj', product)
    (ROOT / 'src/Composition/VirtualActorUsings.cs').write_text(
        'global using Weave.Shared.VirtualActors;\n', encoding='utf-8')

    extension_paths = [f'extensions/Weave.{e}/Weave.{e}.csproj' for e in EXTENSIONS]
    for extension, project_path in zip(EXTENSIONS, extension_paths):
        tree = ET.Element('Project', Sdk='Microsoft.NET.Sdk')
        props = ET.SubElement(tree, 'PropertyGroup')
        ET.SubElement(props, 'RootNamespace').text = 'Weave.Tools'
        ET.SubElement(props, 'Description').text = f'Opt-in {extension} tool adapter for Weave.'
        items = ET.SubElement(tree, 'ItemGroup')
        ET.SubElement(items, 'ProjectReference', Include='../../src/Weave.csproj')
        ET.SubElement(items, 'InternalsVisibleTo', Include='Weave.Tools.Tests')
        write_xml(ROOT / project_path, tree)
        if extension in {'OpenApi', 'DirectHttp', 'Dapr'}:
            folder = ROOT / Path(project_path).parent / 'InvokeTool'
            context = extension + 'ToolJsonContext'
            for source in folder.glob('*.cs'):
                text = source.read_text(encoding='utf-8').replace('ToolJsonContext.', context + '.')
                source.write_text(text, encoding='utf-8')
            (folder / f'{context}.cs').write_text(
                'using System.Text.Json.Serialization;\n\nnamespace Weave.Tools.Connectors;\n\n'
                '[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]\n'
                '[JsonSerializable(typeof(Dictionary<string, string>))]\n'
                f'internal sealed partial class {context} : JsonSerializerContext;\n', encoding='utf-8')

    for source in projects:
        if source in MERGED:
            continue
        target = project_map[source]
        tree = ET.fromstring(original_projects[source])
        props = tree.find('PropertyGroup')
        if props is None:
            props = ET.SubElement(tree, 'PropertyGroup')
        if tree.find('.//RootNamespace') is None:
            ET.SubElement(props, 'RootNamespace').text = Path(source).stem
        if Path(source).stem != Path(target).stem and tree.find('.//AssemblyName') is None:
            ET.SubElement(props, 'AssemblyName').text = Path(source).stem
        seen = set()
        had_tools = False
        for group in tree.findall('ItemGroup'):
            for ref in list(group.findall('ProjectReference')):
                old = (ROOT / Path(source).parent / ref.attrib['Include'].replace('\\', '/')).resolve()
                old_key = old.relative_to(ROOT).as_posix()
                had_tools |= old_key.endswith('Weave.Tools/Weave.Tools.csproj')
                new_key = project_map.get(old_key, old_key)
                if new_key in seen:
                    group.remove(ref)
                    continue
                seen.add(new_key)
                ref.set('Include', relative_reference(target, new_key))
        # Only composition roots and connector tests need concrete implementations.
        if had_tools and Path(source).stem in {'Weave.Silo', 'Weave.Tools.Tests'}:
            items = ET.SubElement(tree, 'ItemGroup')
            for extension_path in extension_paths:
                ET.SubElement(items, 'ProjectReference', Include=relative_reference(target, extension_path))
        write_xml(ROOT / target, tree)

    # The generated CQRS registry is emitted by the executable composition root only.
    generator = ROOT / 'tools/Weave.SourceGen/CqrsRegistrationGenerator.cs'
    text = generator.read_text(encoding='utf-8')
    old = '''        var handlers = context.CompilationProvider
            .Select(static (compilation, ct) => FindAllHandlers(compilation, ct));'''
    new = '''        var enabled = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            !options.GlobalOptions.TryGetValue("build_property.GenerateCqrsRegistration", out var value)
            || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase));

        var handlers = context.CompilationProvider.Combine(enabled)
            .Select(static (pair, ct) => pair.Right
                ? FindAllHandlers(pair.Left, ct)
                : ImmutableArray<HandlerInfo>.Empty);'''
    if old not in text:
        raise ValueError('Source generator changed; review registration changes before applying.')
    generator.write_text(text.replace(old, new), encoding='utf-8')

    # Rewrite exact project/directory paths, not type names or authority strings.
    replacements = dict(project_map)
    for old in projects:
        if old not in MERGED:
            replacements[str(Path(old).parent)] = str(Path(project_map[old]).parent)
    for source, destination in moves.items():
        if destination:
            replacements[source] = destination
    replacements = dict(sorted(replacements.items(), key=lambda item: len(item[0]), reverse=True))
    for path in sorted(ROOT.rglob('*')):
        if not path.is_file() or '.git' in path.parts or path.suffix not in {'.cs', '.md', '.yml', '.yaml', '.json', '.props', '.targets', '.sh', '.ps1', '.xml'}:
            continue
        if 'packages.lock.json' == path.name:
            continue
        if path.name == 'ARCHITECTURE.md' or 'superpowers' in path.parts or 'history' in path.parts:
            continue
        text = path.read_text(encoding='utf-8-sig')
        updated = text
        for old, new in replacements.items():
            updated = updated.replace(old, new).replace(old.replace('/', '\\'), new.replace('/', '\\'))
        updated = updated.replace('Path.Combine("src", "Runtime", "Weave.Silo", "Weave.Silo.csproj")',
                                  'Path.Combine("hosts", "Weave.Host", "Weave.Host.csproj")')
        updated = updated.replace('Path.Combine("src", "Runtime", "Weave.Silo")',
                                  'Path.Combine("hosts", "Weave.Host")')
        updated = updated.replace('Path.Combine(siloPath, "Weave.Silo.csproj")',
                                  'Path.Combine(siloPath, "Weave.Host.csproj")')
        updated = updated.replace('Projects.Weave_Silo', 'Projects.Weave_Host')
        if updated != text:
            path.write_text(updated, encoding='utf-8')

    solution = ET.Element('Solution')
    for area in ('src', 'hosts', 'extensions', 'tools', 'tests'):
        folder = ET.SubElement(solution, 'Folder', Name=f'/{area}/')
        for project in sorted((ROOT / area).rglob('*.csproj')):
            ET.SubElement(folder, 'Project', Path=project.relative_to(ROOT).as_posix())
    write_xml(ROOT / 'Weave.slnx', solution)

    # Build artifacts and bytecode are verification outputs, never source commits.
    ignore = ROOT / '.gitignore'
    ignore.write_text(ignore.read_text() + '\n# Local verification artifacts\n/artifacts/\n__pycache__/\n*.pyc\n', encoding='utf-8')
    coverage = ROOT / 'scripts/DevTool/CoverageCommand.cs'
    coverage.write_text(coverage.read_text().replace('var searchRoot = "src";', 'var searchRoot = "tests";'), encoding='utf-8')
    # Preserve the existing narrow formatting exclusion as exact relocated files.
    shared_files = [destination for source, destination in moves.items()
                    if source.startswith('src/Foundation/Weave.Shared/')
                    and source.endswith('.cs') and destination]
    ci = ROOT / '.github/workflows/ci.yml'
    ci.write_text(ci.read_text().replace('--exclude src/Foundation/Weave.Shared/',
                  '--exclude ' + ' '.join(shared_files)), encoding='utf-8')

    report = []
    for source, original_sha in original_hashes.items():
        destination = moves[source]
        if destination is None:
            if not source.endswith('/VirtualActorUsings.cs'):
                raise ValueError(f'Unexpected C# deletion: {source}')
            continue
        out = ROOT / destination
        report.append({'from': source, 'to': destination, 'before': original_sha,
                       'after': hashlib.sha256(out.read_bytes()).hexdigest()})
    report_path = ROOT / 'artifacts/source-migration.json'
    report_path.parent.mkdir(exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f'Moved {len(targets)} files, preserved {len(report)} C# source files; '
          f'merged {len(MERGED)} projects. Audit: {report_path.relative_to(ROOT)}')


if __name__ == '__main__':
    main()
