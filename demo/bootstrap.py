import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.error
import urllib.request

import bcrypt
from psycopg import sql
from sqlalchemy import make_url

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'backend'))
from database import engine


def call_helper(filename, name, *arguments):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return getattr(module, name)(*arguments)


def run(*args, **env):
    subprocess.run(args, cwd=ROOT, env={**os.environ, **env}, check=True)


def main():
    url = make_url(os.environ['DATABASE_URL'])
    if url.database != 'factoryflow_demo' or url.username != 'factoryflow_admin' or url.host not in ('db', '127.0.0.1', 'localhost'):
        raise SystemExit('Bootstrap accepts only the isolated local factoryflow_demo database and administrator.')
    host_dll = os.environ.get('FACTORYFLOW_HOST_DLL', '/app/Rcm.Host.dll')
    host_command = [os.environ.get('DOTNET_HOST_PATH', 'dotnet'), host_dll]
    admin = f'Host={url.host};Port={url.port or 5432};Database={url.database};Username={url.username};Password={url.password}'
    run(sys.executable, '-m', 'alembic', 'upgrade', 'head')
    run(*host_command, '--migrate', ConnectionStrings__Crm=admin, Logging__LogLevel__Default='Warning')
    run(*host_command, '--migrate-production', ConnectionStrings__ProductionAdmin=admin, Logging__LogLevel__Default='Warning')
    with engine.begin() as connection:
        cursor = connection.connection.driver_connection.cursor()
        cursor.execute('SELECT pg_advisory_xact_lock(638284762)')
        cursor.execute('REVOKE CREATE ON SCHEMA public FROM PUBLIC')
        for module in ['crm', 'identity', 'orders', 'catalog', 'shiftreports', 'production']:
            role = f'factoryflow_{module}'
            cursor.execute('SELECT 1 FROM pg_roles WHERE rolname=%s', (role,))
            if not cursor.fetchone():
                cursor.execute(sql.SQL('CREATE ROLE {} LOGIN PASSWORD {} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS NOREPLICATION').format(sql.Identifier(role), sql.Literal(f'demo-only-{module}')))
        cursor.execute('GRANT USAGE ON SCHEMA crm TO factoryflow_crm')
        cursor.execute('GRANT SELECT ON ALL TABLES IN SCHEMA crm TO factoryflow_crm')
        cursor.execute('GRANT INSERT, UPDATE ON crm."Customers", crm."Topics" TO factoryflow_crm')
        cursor.execute('GRANT INSERT ON crm."Events", crm."CustomerChanges", crm."Receipts" TO factoryflow_crm')
        for filename, function, role in [
            ('configure-crm-database.py', 'grant_orders', 'orders'),
            ('configure-crm-database.py', 'grant_catalog', 'catalog'),
            ('configure-identity-database.py', 'grant_identity', 'identity'),
            ('configure-shift-reports-database.py', 'grant_shift_reports', 'shiftreports'),
            ('configure-production-database.py', 'grant_production', 'production'),
            ('configure-production-review.py', 'grant_reviews', 'shiftreports'),
        ]:
            call_helper(filename, function, cursor, 'factoryflow_' + role)
        for key in ['identity_writer', 'orders_writer', 'catalog_writer', 'shift_reports_writer']:
            cursor.execute("UPDATE public.settings SET value='dotnet' WHERE key=%s", (key,))
            if cursor.rowcount != 1:
                raise RuntimeError('Missing writer ownership setting: ' + key)
        settings = {'company_name': 'FactoryFlow Demo', 'company_address': 'Synthetic Avenue 1, Demo City',
                    'company_nip': 'DEMO-NOT-A-TAX-ID', 'company_regon': 'DEMO', 'company_tagline': '',
                    'labor_rate_pln': '100', 'default_margin_pct': '0.25', 'default_overhead_pct': '0.10', 'vat_rate': '0.23'}
        for key, value in settings.items():
            cursor.execute('INSERT INTO public.settings(key,value) VALUES(%s,%s) ON CONFLICT(key) DO NOTHING', (key, value))
        accounts = [(1, 'office', 'Demo Office', 'biuro'), (2, 'engineer', 'Demo Engineer', 'technolog'),
                    (3, 'production', 'Demo Production', 'produkcja'), (4, 'director', 'Demo Director', 'ceo'),
                    (5, 'sales.north', 'Demo North Sales', 'crm'), (6, 'sales.south', 'Demo South Sales', 'crm')]
        password_hash = bcrypt.hashpw(b'FactoryFlow-Demo-2026!', bcrypt.gensalt()).decode()
        for identity, username, name, role in accounts:
            cursor.execute('INSERT INTO public.users(id,name,role,username,password_hash,password_version) VALUES(%s,%s,%s,%s,%s,1) ON CONFLICT(id) DO NOTHING', (identity, name, role, username, password_hash))
        cursor.execute("SELECT setval(pg_get_serial_sequence('public.users','id'),GREATEST((SELECT max(id) FROM public.users),1))")
        for suffix, name in [('1', 'Demo North'), ('2', 'Demo South')]:
            team = '10000000-0000-0000-0000-00000000000' + suffix
            cursor.execute('INSERT INTO crm."Teams"("Id","Name") VALUES(%s,%s) ON CONFLICT DO NOTHING', (team, name))
        for identity, suffix in [(1, '1'), (2, '1'), (5, '1'), (6, '2')]:
            cursor.execute('INSERT INTO crm."Memberships"("UserId","TeamId") VALUES(%s,%s) ON CONFLICT DO NOTHING', (identity, '10000000-0000-0000-0000-00000000000' + suffix))
        cursor.execute('CREATE SCHEMA IF NOT EXISTS factoryflow_demo')
        cursor.execute('CREATE TABLE IF NOT EXISTS factoryflow_demo.bootstrap_steps(name text PRIMARY KEY, result jsonb NOT NULL)')
        call_helper('configure-production-review.py', 'assign_reviewer', cursor, 1, True, 'FactoryFlow bootstrap', 'Synthetic demo reviewer')
    configuration = json.loads((ROOT / 'demo/appsettings.Demo.json').read_text())
    runtime_env = {**os.environ, 'ASPNETCORE_URLS': 'http://127.0.0.1:18082', 'ASPNETCORE_ENVIRONMENT': 'Demo', 'Logging__LogLevel__Default': 'Warning'}
    for module, connection in configuration['ConnectionStrings'].items():
        runtime_env['ConnectionStrings__' + module] = connection.replace('Host=db;', f'Host={url.host};Port={url.port or 5432};')
    for section in ['Identity', 'Orders', 'Catalog', 'ShiftReports', 'Legacy']:
        for key, value in configuration[section].items():
            runtime_env[f'{section}__{key}'] = str(value)
    uploads = Path(os.environ.get('FACTORYFLOW_UPLOADS', '/app/uploads'))
    uploads.mkdir(parents=True, exist_ok=True)
    if os.geteuid() == 0:
        os.chown(uploads, 1654, 1654)
    runtime_env['Orders__UploadRoot'] = str(uploads)
    runtime_env['Catalog__UploadRoot'] = str(uploads)
    identity = {'user': 1654, 'group': 1654} if os.geteuid() == 0 else {}
    process = subprocess.Popen(host_command, env=runtime_env, cwd=ROOT, **identity)
    try:
        for _ in range(90):
            if process.poll() is not None:
                raise RuntimeError('Bootstrap API exited before becoming healthy.')
            try:
                with urllib.request.urlopen('http://127.0.0.1:18082/health', timeout=10) as response:
                    if response.status == 200:
                        break
            except (urllib.error.URLError, TimeoutError):
                time.sleep(1)
        else:
            raise RuntimeError('Bootstrap API did not become healthy.')
        from fixtures import seed
        seed('http://127.0.0.1:18082', engine)
    finally:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
    print('FactoryFlow bootstrap complete: native writers, synthetic data and two independent CRM teams.')


if __name__ == '__main__':
    main()
