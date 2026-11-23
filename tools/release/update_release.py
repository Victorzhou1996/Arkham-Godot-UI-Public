"""In-game update releases (docs/HOT-UPDATE-DESIGN.md).

  keygen                      make the signing key (once). The private key goes to ~/.arkham/update-signing-key.pem and
                              never anywhere else; the public key to tools/release/update-public-key.pem (it is also
                              compiled into the game, app/Scripts/Update/UpdatePolicy.cs).
  build  --package DIR --release R --server DIR [--platform win64] [--note TEXT ...] [--minimum R] [--save-compat X]
                              hash every file of an exported package, copy the new ones into the server's object store
                              (objects/ab/abcdef...), write and sign <server>/<platform>/manifest.json, and write the
                              package's own game/version.json (what an installer of this package starts from).
  verify --server DIR [--platform win64]
                              check the manifest's signature with the public key and every object's hash.

The server folder is what goes to both servers unchanged (国内 and 海外, Yunke 11-23). Nothing here prints the key.
"""
import argparse
import base64
import hashlib
import json
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PRIVATE = os.path.join(os.path.expanduser('~'), '.arkham', 'update-signing-key.pem')
PUBLIC = os.path.join(HERE, 'update-public-key.pem')
PRODUCT = 'arkham-ui'
SKIP_DIRS = ('game/updates/', 'updates/')   # (android: the install root is the unpacked files folder)
SKIP_FILES = ('game/version.json', 'version.json', '.bundle-id')


def layer_of(path, platform):
    """How a file is switched: data on the game's own next start; engine/code/core by the update helper after it exits."""
    if platform == 'android':
        return 'data'
    if '/' not in path and (path.endswith('.exe') or path.endswith('.pck')):
        return 'engine'
    if path.startswith('data_'):
        return 'code'
    if path.startswith('game/runtime/'):
        return 'core'
    return 'data'


def vkey(release):
    """2026.11.30.2 < 2026.11.30.10: compared as numbers"""
    return tuple(int(x) for x in release.split('.') if x.isdigit())


def sha256_file(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def keygen(_):
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.hazmat.primitives import serialization
    if os.path.exists(PRIVATE):
        sys.exit('key exists already: ' + PRIVATE + ' (not replaced)')
    key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    os.makedirs(os.path.dirname(PRIVATE), exist_ok=True)
    with open(PRIVATE, 'wb') as f:
        f.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    pub = key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo)
    with open(PUBLIC, 'wb') as f:
        f.write(pub)
    print('key made; public key ->', PUBLIC, 'id', key_id(pub))


def key_id(pub_pem):
    return 'arkham-ui-' + hashlib.sha256(pub_pem).hexdigest()[:12]


def sign(payload_bytes):
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import padding
    with open(PRIVATE, 'rb') as f:
        key = serialization.load_pem_private_key(f.read(), password=None)
    # PSS with a 32-byte salt: what .NET's RSASignaturePadding.Pss checks
    return key.sign(payload_bytes, padding.PSS(mgf=padding.MGF1(hashes.SHA256()), salt_length=32), hashes.SHA256())


def check_signature(doc):
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import padding
    with open(PUBLIC, 'rb') as f:
        pub_pem = f.read()
    pub = serialization.load_pem_public_key(pub_pem)
    payload = base64.b64decode(doc['payload'])
    pub.verify(base64.b64decode(doc['signature']), payload, padding.PSS(mgf=padding.MGF1(hashes.SHA256()), salt_length=32), hashes.SHA256())
    if doc.get('keyId') != key_id(pub_pem):
        raise ValueError('key id differs')
    return json.loads(payload.decode('utf-8'))


def scan(package):
    files = []
    for dirpath, dirnames, filenames in os.walk(package):
        dirnames.sort()
        for name in sorted(filenames):
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, package).replace(os.sep, '/')
            if rel in SKIP_FILES or any(rel.startswith(d) for d in SKIP_DIRS) or rel.endswith('.log'):
                continue
            files.append((rel, full))
    return files


def core_source(package, platform):
    p = os.path.join(package, 'game', 'runtime', 'core.json') if platform == 'win64' else os.path.join(package, 'runtime', 'core.json')
    try:
        with open(p, encoding='utf-8') as f:
            return json.load(f).get('sourceId', '')
    except OSError:
        return ''


def build(a):
    package, server, platform = os.path.abspath(a.package), os.path.abspath(a.server), a.platform
    if not os.path.isdir(package):
        sys.exit('no package folder ' + package)
    with open(PUBLIC, 'rb') as f:
        kid = key_id(f.read())
    previous = None
    mpath = os.path.join(server, platform, 'manifest.json')
    if os.path.exists(mpath):
        with open(mpath, encoding='utf-8') as f:
            previous = check_signature(json.load(f))
        if vkey(previous['release']) >= vkey(a.release) and not a.allow_same:
            sys.exit('release %s is not newer than the published %s' % (a.release, previous['release']))
    entries, copied, t0 = [], 0, time.time()
    for rel, full in scan(package):
        sha = sha256_file(full)
        size = os.path.getsize(full)
        obj = os.path.join(server, 'objects', sha[:2], sha)
        if not os.path.exists(obj):
            os.makedirs(os.path.dirname(obj), exist_ok=True)
            shutil.copyfile(full, obj + '.part')
            os.replace(obj + '.part', obj)
            copied += 1
        entries.append({'path': rel, 'bytes': size, 'sha256': sha, 'layer': layer_of(rel, platform),
                        'mtime': int(os.path.getmtime(full) * 1000)})
    core = core_source(package, platform)
    accepted_cores = []
    if previous:
        accepted_cores = list(previous.get('acceptedPreviousCoreSourceIds', []))
        if previous.get('coreSourceId') and previous['coreSourceId'] != core and previous['coreSourceId'] not in accepted_cores:
            accepted_cores.append(previous['coreSourceId'])
    compat = a.save_compat or ('same-core' if not previous or previous.get('coreSourceId') == core else 'reviewed')
    payload = {
        'schemaVersion': 1, 'product': PRODUCT, 'platform': platform, 'release': a.release,
        'minimumRelease': a.minimum or '', 'coreSourceId': core, 'acceptedPreviousCoreSourceIds': accepted_cores,
        'saveCompatibility': compat,
        'files': [{k: e[k] for k in ('path', 'bytes', 'sha256', 'layer')} for e in entries],
        'releaseNotes': a.note or [],
        'published': time.strftime('%Y-%m-%dT%H:%M:%S%z'),
    }
    # Android: the APK of this release (the program and the rules core live in it); a phone whose APK is older downloads it
    # whole through the browser and the system installs it (same signing key)
    if a.apk:
        if platform != 'android': sys.exit('--apk is for --platform android')
        apk_rel = 'full/android/arkham-ui-%s.apk' % (a.apk_release or a.release)
        dst = os.path.join(server, apk_rel)
        if not os.path.exists(dst):
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copyfile(a.apk, dst + '.part'); os.replace(dst + '.part', dst)
        payload['apk'] = {'release': a.apk_release or a.release, 'path': apk_rel, 'bytes': os.path.getsize(dst), 'sha256': sha256_file(dst)}
    payload_bytes = json.dumps(payload, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    doc = {'schemaVersion': 1, 'keyId': kid, 'payload': base64.b64encode(payload_bytes).decode('ascii'),
           'signature': base64.b64encode(sign(payload_bytes)).decode('ascii')}
    os.makedirs(os.path.join(server, platform, 'releases'), exist_ok=True)
    with open(os.path.join(server, platform, 'releases', a.release + '.json'), 'w', encoding='utf-8') as f:
        json.dump(doc, f)
    # the manifest last: a player never sees a release whose objects are not all there
    with open(mpath + '.part', 'w', encoding='utf-8') as f:
        json.dump(doc, f)
    os.replace(mpath + '.part', mpath)
    # the package's own version.json: an installer of this package starts as this release, hashes already known
    meta = os.path.join(package, 'game') if platform == 'win64' else package
    os.makedirs(meta, exist_ok=True)
    with open(os.path.join(meta, 'version.json'), 'w', encoding='utf-8') as f:
        json.dump({'schemaVersion': 1, 'release': a.release, 'platform': platform, 'coreSourceId': core,
                   'files': {e['path']: {'bytes': e['bytes'], 'sha256': e['sha256'], 'mtime': e['mtime']} for e in entries}},
                  f, ensure_ascii=False)
    total = sum(e['bytes'] for e in entries)
    print('release %s (%s): %d files, %.1f MB, %d new objects, %.1f s' % (a.release, platform, len(entries), total / 1e6, copied, time.time() - t0))


def verify(a):
    server = os.path.abspath(a.server)
    with open(os.path.join(server, a.platform, 'manifest.json'), encoding='utf-8') as f:
        payload = check_signature(json.load(f))
    bad = 0
    for e in payload['files']:
        obj = os.path.join(server, 'objects', e['sha256'][:2], e['sha256'])
        if not os.path.exists(obj) or os.path.getsize(obj) != e['bytes'] or sha256_file(obj) != e['sha256']:
            bad += 1
            print('BAD', e['path'])
    print('verify %s %s: signature ok, %d files, %d bad' % (payload['release'], a.platform, len(payload['files']), bad))
    sys.exit(1 if bad else 0)


def main():
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest='cmd', required=True)
    sub.add_parser('keygen')
    b = sub.add_parser('build')
    b.add_argument('--package', required=True)
    b.add_argument('--release', required=True)
    b.add_argument('--server', required=True)
    b.add_argument('--platform', default='win64', choices=['win64', 'android'])
    b.add_argument('--note', action='append')
    b.add_argument('--minimum')
    b.add_argument('--save-compat', choices=['same-core', 'reviewed', 'breaks'])
    b.add_argument('--allow-same', action='store_true')
    b.add_argument('--apk', help='android: the APK of this release (copied to <server>/full/android/)')
    b.add_argument('--apk-release', help='android: the release that APK was built as (default: --release)')
    v = sub.add_parser('verify')
    v.add_argument('--server', required=True)
    v.add_argument('--platform', default='win64', choices=['win64', 'android'])
    a = p.parse_args()
    {'keygen': keygen, 'build': build, 'verify': verify}[a.cmd](a)


if __name__ == '__main__':
    main()
