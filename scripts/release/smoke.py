#!/usr/bin/env python3
"""Exercise only the supplied release artifacts, in a disposable local deployment.

The reviewer below is a separate capability identity, not a human approval claim.
No model, public package feed, source Host, or existing operator credentials are used.
"""
import argparse
import http.client
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import uuid
import xml.etree.ElementTree as ET

PACKAGE_ID = 'KoalaFacts.Weave.Cli'
MAX_OUTPUT = 1_048_576
spec = importlib.util.spec_from_file_location('release_guard', Path(__file__).with_name('guard.py'))
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class SmokeError(Exception):
    """A fixed diagnostic; never includes child output or HTTP response bodies."""


def require(condition, message):
    if not condition:
        raise SmokeError(message)


def isolated_environment(root):
    allowed = {'path', 'systemroot', 'windir', 'comspec', 'pathext', 'lang', 'lc_all',
               'dotnet_root', 'dotnet_root_x64', 'dotnet_root_arm64', 'ld_library_path',
               'programfiles', 'programfiles(x86)'}
    env = {key: value for key, value in os.environ.items() if key.lower() in allowed}
    for name in ('home', 'tmp', 'packages', 'http-cache', 'plugin-cache', 'appdata', 'localappdata'):
        (root / name).mkdir()
    env.update({
        'HOME': str(root / 'home'), 'USERPROFILE': str(root / 'home'),
        'APPDATA': str(root / 'appdata'), 'LOCALAPPDATA': str(root / 'localappdata'),
        'TEMP': str(root / 'tmp'), 'TMP': str(root / 'tmp'), 'TMPDIR': str(root / 'tmp'),
        'DOTNET_CLI_HOME': str(root / 'home'), 'NUGET_PACKAGES': str(root / 'packages'),
        'NUGET_HTTP_CACHE_PATH': str(root / 'http-cache'),
        'NUGET_PLUGINS_CACHE_PATH': str(root / 'plugin-cache'),
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
        'DOTNET_NOLOGO': '1', 'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE': '1',
        'WEAVE_NO_UPDATE_CHECK': '1',
    })
    return env


def windows_job():
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.AssignProcessToJobObject.restype = wintypes.BOOL
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.SetInformationJobObject.restype = wintypes.BOOL
    kernel.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel.TerminateJobObject.restype = wintypes.BOOL
    kernel.QueryInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                               wintypes.DWORD, ctypes.c_void_p]
    kernel.QueryInformationJobObject.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]

    class BasicLimits(ctypes.Structure):
        _fields_ = [('process_time', ctypes.c_int64), ('job_time', ctypes.c_int64),
                    ('flags', wintypes.DWORD), ('minimum', ctypes.c_size_t), ('maximum', ctypes.c_size_t),
                    ('active_processes', wintypes.DWORD), ('affinity', ctypes.c_size_t),
                    ('priority', wintypes.DWORD), ('scheduling', wintypes.DWORD)]

    class ExtendedLimits(ctypes.Structure):
        _fields_ = [('basic', BasicLimits), ('io', ctypes.c_uint64 * 6),
                    ('process_memory', ctypes.c_size_t), ('job_memory', ctypes.c_size_t),
                    ('peak_process_memory', ctypes.c_size_t), ('peak_job_memory', ctypes.c_size_t)]

    job = kernel.CreateJobObjectW(None, None)
    require(job, 'Could not own the Windows process tree.')
    limits = ExtendedLimits()
    limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    if not kernel.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
        kernel.CloseHandle(job)
        raise SmokeError('Could not bound the Windows process tree.')
    return kernel, job


def wait_for_empty_job(kernel, job):
    import ctypes
    from ctypes import wintypes

    class Accounting(ctypes.Structure):
        _fields_ = [('user_time', ctypes.c_int64), ('kernel_time', ctypes.c_int64),
                    ('period_user_time', ctypes.c_int64), ('period_kernel_time', ctypes.c_int64),
                    ('page_faults', wintypes.DWORD), ('total', wintypes.DWORD),
                    ('active', wintypes.DWORD), ('terminated', wintypes.DWORD)]

    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        info = Accounting()
        if not kernel.QueryInformationJobObject(job, 1, ctypes.byref(info), ctypes.sizeof(info), None):
            return False
        if info.active == 0:
            return True
        time.sleep(0.02)
    return False


def resume_windows_process(process):
    """Resume the owned primary thread only after the suspended target joins its Job.

    Popen closes CreateProcess's thread handle, so reopen that exact process's sole
    initial thread. A Python helper is unsuitable: Store Python can break native
    children out of its Job even when the helper itself is successfully assigned.
    """
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)

    class ThreadEntry(ctypes.Structure):
        _fields_ = [('size', wintypes.DWORD), ('usage', wintypes.DWORD), ('thread_id', wintypes.DWORD),
                    ('process_id', wintypes.DWORD), ('base_priority', wintypes.LONG),
                    ('delta_priority', wintypes.LONG), ('flags', wintypes.DWORD)]

    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Thread32First.argtypes = [wintypes.HANDLE, ctypes.POINTER(ThreadEntry)]
    kernel.Thread32Next.argtypes = [wintypes.HANDLE, ctypes.POINTER(ThreadEntry)]
    kernel.OpenThread.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenThread.restype = wintypes.HANDLE
    kernel.ResumeThread.argtypes = [wintypes.HANDLE]
    kernel.ResumeThread.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    snapshot = kernel.CreateToolhelp32Snapshot(4, 0)  # TH32CS_SNAPTHREAD
    require(snapshot != ctypes.c_void_p(-1).value, 'Could not inspect the suspended process thread.')
    try:
        entry = ThreadEntry()
        entry.size = ctypes.sizeof(entry)
        found = []
        available = kernel.Thread32First(snapshot, ctypes.byref(entry))
        while available:
            if entry.process_id == process.pid:
                found.append(entry.thread_id)
            available = kernel.Thread32Next(snapshot, ctypes.byref(entry))
        require(len(found) == 1, 'Suspended process did not have exactly one initial thread.')
        thread = kernel.OpenThread(2, False, found[0])  # THREAD_SUSPEND_RESUME
        require(thread, 'Could not open the suspended process thread.')
        try:
            require(kernel.ResumeThread(thread) == 1, 'Could not release the owned suspended process.')
        finally:
            kernel.CloseHandle(thread)
    finally:
        kernel.CloseHandle(snapshot)


class OwnedProcess:
    """Bound output and lifetime, including descendants of the CLI's Host launcher."""
    def __init__(self, command, root, env, timeout=90):
        self.process = None
        self.kernel = self.job = None
        self.readers = []
        self.output = [bytearray(), bytearray()]
        self.overflow = threading.Event()
        self.deadline = time.monotonic() + timeout
        try:
            if os.name == 'nt':
                self.kernel, self.job = windows_job()
            self.process = subprocess.Popen(command, cwd=root, env=env, shell=False,
                                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                            start_new_session=os.name != 'nt',
                                            creationflags=(subprocess.CREATE_NO_WINDOW | 4) if os.name == 'nt' else 0)
            if self.job:
                require(self.kernel.AssignProcessToJobObject(self.job, int(self.process._handle)),
                        'Could not assign the Windows process tree.')
            for stream, buffer in zip((self.process.stdout, self.process.stderr), self.output):
                reader = threading.Thread(target=self._drain, args=(stream, buffer), daemon=True)
                reader.start()
                self.readers.append(reader)
            if self.job:
                resume_windows_process(self.process)
            self.process.stdin.close()
        except BaseException:
            self.close()
            raise

    def _drain(self, stream, buffer):
        try:
            while chunk := stream.read(4096):
                room = MAX_OUTPUT - len(buffer)
                buffer.extend(chunk[:room])
                if len(chunk) > room:
                    self.overflow.set()
        except OSError:
            self.overflow.set()
        finally:
            stream.close()

    def check(self):
        require(not self.overflow.is_set(), 'Release process exceeded its output limit.')
        require(time.monotonic() < self.deadline, 'Release process exceeded its time limit.')

    def wait(self):
        while self.process.poll() is None or any(reader.is_alive() for reader in self.readers):
            self.check()
            time.sleep(0.02)
        self.check()
        require(self.process.returncode == 0, 'Release command failed; child diagnostics were withheld.')
        return bytes(self.output[0])

    def close(self):
        clean = True
        try:
            if self.process is not None:
                if self.job:
                    clean = bool(self.kernel.TerminateJobObject(self.job, 1))
                elif os.name != 'nt':
                    try:
                        os.killpg(self.process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                if self.process.poll() is None:
                    self.process.kill()
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    clean = False
                if self.process.stdin and not self.process.stdin.closed:
                    self.process.stdin.close()
                for reader in self.readers:
                    reader.join(timeout=3)
                readers_finished = not any(reader.is_alive() for reader in self.readers)
                clean = clean and readers_finished
                if readers_finished:
                    for stream in (self.process.stdout, self.process.stderr):
                        if stream and not stream.closed:
                            stream.close()
        finally:
            if self.job:
                try:
                    clean = wait_for_empty_job(self.kernel, self.job) and clean
                finally:
                    self.kernel.CloseHandle(self.job)
                    self.job = None
        require(clean, 'Release process tree cleanup could not be confirmed.')

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def run(command, root, env, timeout=90):
    with OwnedProcess(command, root, env, timeout) as child:
        return child.wait()


def run_stage(stage, command, root, env, runner=run, timeout=90):
    try:
        return runner(command, root, env, timeout=timeout)
    except SmokeError as error:
        raise SmokeError(stage + ': ' + str(error)) from None


def check_version(output, version):
    text = output.decode('utf-8', errors='strict').strip()
    require(re.fullmatch(re.escape(version) + r'(?:\+[0-9A-Za-z.-]+)?', text) is not None,
            'Installed CLI version does not match the release version.')


def install_tool(package, version, root, env, runner=run):
    guard.validate_version(version)
    require(package.name == f'{PACKAGE_ID}.{version}.nupkg' and package.is_file() and not package.is_symlink(),
            'Expected the exact release tool package file.')
    feed, tools = root / 'feed', root / 'tools'
    feed.mkdir()
    shutil.copyfile(package, feed / package.name)
    guard.validate_packages(feed, version)
    config = ET.Element('configuration')
    for section in ('packageSources', 'auditSources'):
        sources = ET.SubElement(config, section)
        ET.SubElement(sources, 'clear')
        ET.SubElement(sources, 'add', key='release-local', value=str(feed))
    ET.SubElement(ET.SubElement(config, 'fallbackPackageFolders'), 'clear')
    settings = ET.SubElement(config, 'config')
    ET.SubElement(settings, 'add', key='globalPackagesFolder', value=str(root / 'packages'))
    config_path = root / 'NuGet.Config'
    ET.ElementTree(config).write(config_path, encoding='utf-8', xml_declaration=True)
    dotnet = shutil.which('dotnet', path=env.get('PATH', env.get('Path')))
    require(dotnet is not None, 'A .NET SDK is required for the tool installation check.')
    run_stage('NuGet tool installation',
              [dotnet, 'tool', 'install', PACKAGE_ID, '--version', version, '--tool-path', str(tools),
               '--configfile', str(config_path), '--no-cache'], root, env, runner, timeout=120)
    executable = tools / ('weave.exe' if os.name == 'nt' else 'weave')
    require(executable.is_file() and not executable.is_symlink(), 'Tool installation did not create the weave command.')
    check_version(run_stage('Installed tool version', [str(executable), '--version'], root, env, runner), version)


class LocalApi:
    def __init__(self, port):
        require(type(port) is int and 1024 <= port <= 65535, 'Invalid local HTTP port.')
        self.port = port

    def call(self, method, path, status, *, capability=None, operator=None, body=None, credential=False, status_only=False):
        require(path.startswith('/api/') and not any(value in path for value in ('?', '#', '\r', '\n')),
                'Invalid local API route.')
        headers = {'Accept': 'application/json'}
        if capability:
            headers['X-Weave-Capability'] = capability
        if operator:
            headers['X-Weave-Operator-Key'] = operator
        data = None if body is None else json.dumps(body).encode()
        if data is not None:
            headers['Content-Type'] = 'application/json'
        connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=10)
        try:
            connection.request(method, path, body=data, headers=headers)
            response = connection.getresponse()
            require(response.status == status,
                    f'Local API returned HTTP {response.status}; expected HTTP {status}.')
            raw = response.read(MAX_OUTPUT + 1)
            require(len(raw) <= MAX_OUTPUT, 'Local API response exceeded its size limit.')
            if status_only:
                return None
            if credential:
                require(response.getheader('Content-Type', '').split(';')[0] == 'text/plain'
                        and 'no-store' in response.getheader('Cache-Control', ''),
                        'Credential response did not preserve the private response contract.')
                token = raw.decode('utf-8').strip()
                require(re.fullmatch(r'[A-Za-z0-9_-]+', token) is not None, 'Invalid issued credential.')
                return token
            if not raw:
                return None
            require(response.getheader('Content-Type', '').split(';')[0] == 'application/json',
                    'Local API did not return JSON.')
            value = json.loads(raw)
            require(isinstance(value, dict), 'Local API returned an unexpected JSON shape.')
            return value
        finally:
            connection.close()


def governed_round_trip(api, operator, documents):
    route = '/api/workspaces/release-smoke/tools/files/invocations'
    api.call('POST', '/api/operator/credentials/agent/issue', 401)
    agent = api.call('POST', '/api/operator/credentials/agent/issue', 200, operator=operator, credential=True)
    reviewer = api.call('POST', '/api/operator/credentials/reviewer/issue', 200, operator=operator, credential=True)
    require(agent != reviewer, 'Agent and reviewer credentials must be distinct.')

    def invocation(method, path, content=None):
        value = {'invocationId': uuid.uuid4().hex, 'toolName': 'files', 'method': method, 'parameters': {'path': path}}
        if content is not None:
            value['rawInput'] = content
        return value

    source = api.call('POST', route, 200, capability=agent, body=invocation('read_file', 'source.txt'))
    require(source.get('success') is True and source.get('output') == 'Release artifact smoke input.\n',
            'Published Host did not read the supplied document.')
    content = '# Release smoke\n\n' + source['output']
    write = invocation('write_file', 'result.md', content)
    retained = route + '/' + write['invocationId']
    target = documents / 'result.md'
    sentinel = target.read_bytes()
    pending = api.call('POST', route, 202, capability=agent, body=write)
    require(pending.get('invocationId') == write['invocationId'] and pending.get('success') is False
            and pending.get('errorCode') == 'approval-pending' and pending.get('approvalState') == 'Pending'
            and pending.get('outcome') == 'NotDispatched' and pending.get('outcomeRecorded') is False
            and pending.get('isReplay') is False, 'Write was not retained as an undispatched Pending proposal.')
    require(target.read_bytes() == sentinel, 'Pending proposal changed the document.')
    api.call('GET', retained + '/approval/review', 403, capability=agent, operator=operator)
    preview = api.call('GET', retained + '/approval/review', 200, capability=reviewer, operator=operator)
    digest = preview.get('planDigest')
    require(preview.get('invocationId') == write['invocationId'] and preview.get('workspaceId') == 'release-smoke'
            and preview.get('toolName') == 'files' and preview.get('operation') == 'write_file'
            and preview.get('rawInput') == content and preview.get('parameters') == write['parameters']
            and isinstance(digest, str) and re.fullmatch(r'approval-v1:[0-9A-F]{64}', digest)
            and pending.get('approvalPlanDigest') == digest, 'Review did not bind the exact pending proposal.')
    approved = api.call('POST', retained + '/decision', 200, capability=reviewer, operator=operator,
                        body={'decision': 'approve', 'planDigest': digest})
    require(approved.get('invocationId') == write['invocationId'] and approved.get('approvalState') == 'Approved',
            'Independent reviewer approval was not recorded.')
    require(target.read_bytes() == sentinel, 'Approval itself executed the write.')
    result = api.call('POST', retained + '/resume', 200, capability=agent)
    require(result.get('invocationId') == write['invocationId'] and result.get('success') is True
            and result.get('outcome') == 'Succeeded' and result.get('outcomeRecorded') is True
            and isinstance(result.get('attemptId'), str) and bool(result['attemptId']),
            'Same-UUID execution did not record a successful attempt.')
    require(target.read_bytes() == b'\xef\xbb\xbf' + content.encode('utf-8'),
            'Written document differs from the reviewed content.')
    reread = api.call('POST', route, 200, capability=agent, body=invocation('read_file', 'result.md'))
    require(reread.get('success') is True and reread.get('output') == content, 'Governed read-back failed.')
    status = api.call('GET', retained, 200, capability=agent)
    require(status.get('invocationId') == write['invocationId'] and status.get('outcome') == 'Succeeded'
            and status.get('outcomeRecorded') is True and status.get('attemptId') == result['attemptId'],
            'Retained invocation does not match the executed attempt.')
    approval = api.call('GET', retained + '/approval', 200, capability=agent)
    require(approval.get('invocationId') == write['invocationId'] and approval.get('approvalState') == 'Consumed',
            'Successful execution did not consume its approval.')
    marker = b'Duplicate invocation must not overwrite this verification marker.\n'
    target.write_bytes(marker)
    replay = api.call('POST', route, 200, capability=agent, body=write)
    require(replay.get('isReplay') is True and replay.get('invocationId') == write['invocationId']
            and replay.get('attemptId') == result['attemptId'] and target.read_bytes() == marker,
            'Same-UUID replay did not preserve the recorded outcome without a second effect.')


def distribution_paths(directory):
    require(directory.is_dir() and not directory.is_symlink(), 'Expected an extracted distribution directory.')
    directory = directory.resolve()
    suffix = '.exe' if os.name == 'nt' else ''
    cli, host = directory / ('weave' + suffix), directory / 'host' / ('Weave.Silo' + suffix)
    require(cli.is_file() and host.is_file() and not cli.is_symlink() and not host.is_symlink()
            and not host.parent.is_symlink(), 'Distribution must contain its native CLI and native host/Weave.Silo.')
    return cli, host


def smoke_distribution(directory, version, root, env):
    cli, host = distribution_paths(directory)
    check_version(run_stage('Distribution CLI version', [str(cli), '--version'], root, env), version)
    documents, deployment = root / 'documents', root / 'deployment'
    documents.mkdir()
    (documents / 'source.txt').write_bytes(b'Release artifact smoke input.\n')
    (documents / 'result.md').write_bytes(b'Original document.\n')
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        port = probe.getsockname()[1]
    require(port not in (11111, 30000), 'Allocated HTTP port conflicts with local Orleans ports; rerun the check.')
    run_stage('Distribution local initialization',
              [str(cli), 'local', 'init', '--directory', str(deployment), '--documents', str(documents),
               '--host', str(host), '--workspace', 'release-smoke', '--port', str(port)], root, env)
    require((deployment / 'local.json').is_file() and (deployment / 'state/invocations.db').is_file(),
            'Published CLI did not initialize a durable local deployment.')
    configuration = json.loads((deployment / 'private/appsettings.json').read_text(encoding='utf-8-sig'))
    operator = configuration['Weave']['Operator']['Key']
    profiles = configuration['Weave']['Operator']['Credentials']
    require(profiles['agent']['IssuedTo'] != profiles['reviewer']['IssuedTo'], 'Reviewer identity was not independent.')
    api = LocalApi(port)
    with OwnedProcess([str(cli), 'local', 'serve', '--directory', str(deployment)], root, env, timeout=180) as child:
        deadline = time.monotonic() + 40
        while True:
            child.check()
            require(child.process.poll() is None, 'Published Host exited during startup.')
            require(time.monotonic() < deadline, 'Published Host did not become ready.')
            try:
                api.call('GET', '/api/workspaces/', 200, operator=operator, status_only=True)
                break
            except (ConnectionError, TimeoutError):
                time.sleep(0.1)
        # Idempotent connection after readiness avoids racing local serve's own connection.
        api.call('POST', '/api/operator/tools/files/connect', 204, operator=operator)
        governed_round_trip(api, operator, documents)
        child.check()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--distribution', type=Path)
    args = parser.parse_args(argv)
    try:
        guard.validate_version(args.version)
        with tempfile.TemporaryDirectory(prefix='weave-release-smoke-') as temporary:
            root = Path(temporary).resolve()
            env = isolated_environment(root)
            install_tool(args.package, args.version, root, env)
            if args.distribution is not None:
                smoke_distribution(args.distribution, args.version, root, env)
        print(json.dumps({'packageId': PACKAGE_ID, 'version': args.version, 'toolInstalled': True,
                          'distributionVerified': args.distribution is not None,
                          'reviewer': 'scripted independent identity' if args.distribution is not None else None}))
        return 0
    except SmokeError as error:
        print('FAIL release smoke: ' + str(error), file=sys.stderr)
    except (OSError, ValueError, KeyError, http.client.HTTPException):
        print('FAIL release smoke: artifact, process, or local protocol validation failed; diagnostics withheld.', file=sys.stderr)
    return 1


if __name__ == '__main__':
    sys.exit(main())
