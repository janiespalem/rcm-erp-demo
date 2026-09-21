import io
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tarfile

import pytest

ROOT = Path(__file__).resolve().parents[1]


@pytest.mark.parametrize('revision', ['20260916_0001', '20260917_0001', '20260917_0002'])
def test_irreversible_downgrade_does_not_move_revision(tmp_path, revision):
    database = tmp_path / 'migration.db'
    env = {**os.environ, 'APP_ENV':'test', 'DISABLE_STARTUP_SEED':'1', 'DATABASE_URL':f'sqlite:///{database}'}
    subprocess.run([sys.executable, '-m', 'alembic', 'stamp', revision], cwd=ROOT, env=env, capture_output=True, check=True)
    result = subprocess.run([sys.executable, '-m', 'alembic', 'downgrade', '-1'], cwd=ROOT, env=env, capture_output=True, text=True)
    assert result.returncode != 0 and 'irreversible' in result.stderr
    with sqlite3.connect(database) as db:
        assert db.execute('SELECT version_num FROM alembic_version').fetchone()[0] == revision


def test_schema_migration_preserves_legacy_json_and_defaults_old_writers(tmp_path):
    database = tmp_path / 'schema.db'
    with sqlite3.connect(database) as db:
        db.execute('CREATE TABLE shift_reports (id INTEGER PRIMARY KEY, fields TEXT NOT NULL)')
        db.execute('INSERT INTO shift_reports (fields) VALUES (?)', ('{"checks":["NIE","OK"]}',))
    env = {**os.environ,'APP_ENV':'test','DISABLE_STARTUP_SEED':'1','DATABASE_URL':f'sqlite:///{database}'}
    subprocess.run([sys.executable,'-m','alembic','stamp','20260917_0001'],cwd=ROOT,env=env,capture_output=True,check=True)
    subprocess.run([sys.executable,'-m','alembic','upgrade','head'],cwd=ROOT,env=env,capture_output=True,check=True)
    with sqlite3.connect(database) as db:
        assert db.execute('SELECT fields,schema_version FROM shift_reports').fetchone() == ('{"checks":["NIE","OK"]}',1)
        db.execute('INSERT INTO shift_reports(fields) VALUES (?)', ('{}',))
        assert db.execute('SELECT schema_version FROM shift_reports WHERE id=2').fetchone()[0] == 1


@pytest.mark.parametrize('use_bundle', [False, True])
def test_release_archive_uses_commit_not_dirty_workspace(tmp_path, use_bundle):
    repo = tmp_path / 'repo'
    repo.mkdir()
    def git(*args):
        return subprocess.check_output(['git', *args], cwd=repo, stderr=subprocess.DEVNULL).decode().strip()
    git('init')
    git('config', 'user.email', 'test@example.invalid')
    git('config', 'user.name', 'Release test')
    (repo / 'tracked.txt').write_text('committed')
    git('add', 'tracked.txt')
    git('commit', '-m', 'fixture')
    sha = git('rev-parse', 'HEAD')
    (repo / 'tracked.txt').write_text('dirty')
    (repo / 'untracked.txt').write_text('must not ship')
    args = ['bash', str(ROOT / 'scripts/archive-commit.sh'), sha]
    cwd = repo
    if use_bundle:
        bundle = tmp_path / 'source.bundle'
        git('bundle', 'create', str(bundle), 'HEAD')
        args.append(str(bundle))
        cwd = tmp_path  # production is not a Git checkout
    data = subprocess.check_output(args, cwd=cwd)
    with tarfile.open(fileobj=io.BytesIO(data)) as archive:
        assert archive.getnames() == ['tracked.txt']
        assert archive.extractfile('tracked.txt').read() == b'committed'
    args[2] = '0' * 40
    bad = subprocess.run(args, cwd=cwd, capture_output=True)
    assert bad.returncode != 0 and bad.stdout == b''
