"""Focused shift reporting contract, including real browser flow when opted in."""
import os

import bcrypt
import pytest

from tests.test_api import client, db_session, live_server_url
from models import ShiftReport, ShiftReportAudit, User, UserRole
from auth import get_current_user
from main import app


@pytest.fixture
def reporter(client, db_session):
    db_session.add(User(id=4, name="Synthetic reporter", role=UserRole.produkcja,
                        default_shift="II", pin_hash=bcrypt.hashpw(b"5849", bcrypt.gensalt(rounds=4)).decode()))
    db_session.commit()
    app.dependency_overrides[get_current_user] = lambda: {"id": "4", "role": "produkcja", "name": "Synthetic reporter"}
    return client


def draft(client):
    response = client.post('/api/shift-reports', json={"report_date": "2026-09-16", "shift": "I"})
    assert response.status_code == 200, response.text
    return response.json()


def complete_fields(report):
    fields = report['fields'].copy()
    fields.update(people=3, assembled=12, prepared=12, poured=12, checked=12, demoulded=10,
                  damaged=0, controller="Synthetic controller", checks=['OK'] * 9,
                  vibrators={"condition": "sprawne"}, extensions={"condition": "sprawne"})
    return fields


def save(client, report, fields):
    return client.put(f"/api/shift-reports/{report['id']}", json={"version_id": report['version_id'], "fields": fields})


def test_draft_autosave_finalize_lock_correction(reporter, db_session):
    r = draft(reporter)
    assert r['author_id'] == 4 and r['status'] == 'draft'
    r = save(reporter, r, complete_fields(r)).json()
    assert r['version_id'] == 2
    final = reporter.post(f"/api/shift-reports/{r['id']}/finalize", json={"version_id": r['version_id']})
    assert final.status_code == 200
    r = final.json()
    assert r['finalized_at'] and not r['validation_errors']
    assert save(reporter, r, r['fields']).status_code == 409
    assert reporter.delete(f"/api/shift-reports/{r['id']}?version_id={r['version_id']}").status_code == 409
    corrected = dict(r['fields'], poured=13)
    path = f"/api/shift-reports/{r['id']}/corrections"
    assert reporter.post(path, json={"version_id": r['version_id'], "fields": corrected, "reason": " "}).status_code == 422
    response = reporter.post(path, json={"version_id": r['version_id'], "fields": corrected, "reason": "Count verified"})
    assert response.status_code == 200
    assert response.json()['correction_count'] == 1
    assert response.json()['finalized_at'] == r['finalized_at']
    entries = reporter.get(f"/api/shift-reports/{r['id']}/audit").json()
    assert entries[-1]['before']['fields']['poured'] == 12
    assert entries[-1]['after']['fields']['poured'] == 13
    assert entries[-1]['actor_id'] == 4
    assert [e['action'] for e in entries] == ['created', 'finalized', 'corrected']
    assert [e['action'] for e in reporter.get(f"/api/shift-reports/{r['id']}/audit?include_drafts=true").json()] == ['created', 'saved', 'finalized', 'corrected']


def test_schema_v1_is_frozen_and_audit_pins_it(reporter):
    import hashlib
    import json
    from routers.shift_reports import REPORT_SCHEMAS
    # Published v1 includes the original paper wording AND its answer ordering.
    assert hashlib.sha256(json.dumps(REPORT_SCHEMAS['1'],sort_keys=True).encode()).hexdigest() == 'f6d19d49be814e7d36e4c4ad9d7f771a24029c91c5062909cd56a479dc7d6254'
    assert len({q['id'] for q in REPORT_SCHEMAS['1']}) == 9
    r = draft(reporter)
    assert r['schema_version'] == 1
    assert reporter.get(f"/api/shift-reports/{r['id']}/audit").json()[0]['after']['schema_version'] == 1


def test_draft_saves_coalesce_but_preserve_actor_boundaries(reporter, db_session):
    r = draft(reporter)
    original = r['fields']
    for i in range(20):
        r = save(reporter,r,dict(r['fields'],remarks=f'Draft {i}')).json()
    entries = reporter.get(f"/api/shift-reports/{r['id']}/audit?include_drafts=true").json()
    assert len(entries) == 2
    assert entries[-1]['before']['fields'] == original
    assert entries[-1]['after']['fields']['remarks'] == 'Draft 19'
    assert entries[-1]['after']['draft_save_count'] == 20
    assert entries[-1]['after']['last_saved_at']
    assert entries[-1]['after']['version_id'] == 21
    db_session.add(User(id=5,name='Synthetic technologist',role=UserRole.technolog))
    db_session.commit()
    app.dependency_overrides[get_current_user] = lambda: {'id':'5','role':'technolog','name':'Synthetic technologist'}
    r = save(reporter,r,dict(r['fields'],remarks='Different actor')).json()
    entries = reporter.get(f"/api/shift-reports/{r['id']}/audit?include_drafts=true").json()
    assert len(entries) == 3 and entries[-1]['actor_id'] == 5
    assert entries[-1]['before']['fields']['remarks'] == 'Draft 19'
    assert entries[-1]['after']['draft_save_count'] == 1


def test_legacy_audit_is_never_coalesced_or_rewritten(reporter, db_session):
    r = draft(reporter)
    r = save(reporter,r,dict(r['fields'],remarks='Legacy draft')).json()
    legacy = db_session.query(ShiftReportAudit).filter_by(action='saved').one()
    legacy.after = {k:v for k,v in legacy.after.items() if k not in ('schema_version','draft_save_count','last_saved_at')}
    db_session.commit()
    before = legacy.after.copy()
    r = save(reporter,r,dict(r['fields'],remarks='New draft')).json()
    db_session.refresh(legacy)
    assert legacy.after == before
    assert db_session.query(ShiftReportAudit).count() == 3


@pytest.mark.parametrize('change,error_key', [
    ({'damaged': 1}, 'damage_reason'),
    ({'vibrators': {'condition': 'niesprawne'}}, 'vibrators.reason'),
    ({'extensions': {'condition': 'niesprawne'}}, 'extensions.reason'),
    ({'checks': ['NIE'] + ['OK'] * 8}, 'corrected_work'),
    ({'remaining_work': 'Clean form'}, 'work_owner'),
])
def test_conditional_validation_on_finalization(reporter, change, error_key):
    r = draft(reporter)
    fields = complete_fields(r) | change
    response = save(reporter, r, fields)
    assert response.status_code == 200  # incomplete drafts remain saveable
    r = response.json()
    response = reporter.post(f"/api/shift-reports/{r['id']}/finalize", json={"version_id": r['version_id']})
    assert response.status_code == 422
    assert error_key in response.json()['detail']['fields']


def test_duplicate_conflict_delete_and_permissions(reporter, db_session):
    r = draft(reporter)
    assert draft(reporter)['id'] == r['id']
    assert db_session.query(ShiftReport).count() == 1
    changed = save(reporter, r, dict(r['fields'], remarks='First writer')).json()
    assert save(reporter, r, dict(r['fields'], remarks='Stale writer')).status_code == 409
    assert reporter.get(f"/api/shift-reports/{r['id']}").json()['fields']['remarks'] == 'First writer'
    assert save(reporter, changed, dict(r['fields'], poured=-1)).status_code == 422
    assert save(reporter, changed, dict(r['fields'], poured=1.5)).status_code == 422
    app.dependency_overrides[get_current_user] = lambda: {"id": "5", "role": "produkcja", "name": "Other"}
    assert reporter.get('/api/shift-reports').status_code == 200
    assert save(reporter, changed, changed['fields']).status_code == 403
    app.dependency_overrides[get_current_user] = lambda: {"id": "5", "role": "ceo", "name": "Viewer"}
    assert reporter.get('/api/shift-reports').status_code == 200
    assert save(reporter, changed, changed['fields']).status_code == 403
    app.dependency_overrides[get_current_user] = lambda: {"id": "5", "role": "biuro", "name": "Office"}
    assert reporter.get('/api/shift-reports').status_code == 200
    assert reporter.get(f"/api/shift-reports/{r['id']}").status_code == 200
    assert reporter.get(f"/api/shift-reports/{r['id']}/audit").status_code == 200
    assert save(reporter, changed, changed['fields']).status_code == 403
    assert reporter.post('/api/shift-reports', json={'report_date': '2026-09-17', 'shift': 'I'}).status_code == 403
    app.dependency_overrides[get_current_user] = lambda: {"id": "4", "role": "produkcja", "name": "Synthetic reporter"}
    assert reporter.delete(f"/api/shift-reports/{r['id']}?version_id={changed['version_id']}").status_code == 204
    assert db_session.query(ShiftReportAudit).count() == 0


def test_production_login_and_existing_erp_isolation(reporter):
    app.dependency_overrides.pop(get_current_user)
    response = reporter.post('/api/auth/login', json={'role': 'produkcja', 'pin': '5849'})
    assert response.status_code == 200
    data = response.json()
    assert data['id'] == 4 and data['default_shift'] == 'II'
    headers = {'Authorization': 'Bearer ' + data['access_token']}
    assert reporter.get('/api/shift-reports', headers=headers).status_code == 200
    assert reporter.get('/api/orders/', headers=headers).status_code == 403
    assert reporter.get('/api/shift-reports').status_code == 401


@pytest.mark.parametrize('state', ['draft', 'complete', 'finalized', 'corrected'])
@pytest.mark.parametrize('admin_role', ['ceo', 'technolog'])
def test_admin_delete_retains_audit_and_releases_shift(reporter, db_session, state, admin_role, monkeypatch):
    monkeypatch.setenv('SHIFT_REPORT_ADMIN_IPS', '192.0.2.99')
    day = reporter.get('/api/shift-reports/today').json()['date']
    r = reporter.post('/api/shift-reports', json={'report_date':day, 'shift':'I'}).json()
    if state != 'draft':
        r = save(reporter, r, complete_fields(r)).json()
    if state in ('finalized', 'corrected'):
        r = reporter.post(f"/api/shift-reports/{r['id']}/finalize", json={'version_id':r['version_id']}).json()
    if state == 'corrected':
        r = reporter.post(f"/api/shift-reports/{r['id']}/corrections", json={'version_id':r['version_id'], 'fields':r['fields'], 'reason':'Test correction'}).json()
    path = f"/api/shift-reports/{r['id']}/delete"
    payload = {'version_id':r['version_id'], 'reason':'Raport testowy'}
    for role in ('produkcja', 'biuro'):
        app.dependency_overrides[get_current_user] = lambda role=role: {'id':'4', 'role':role, 'name':'Synthetic viewer'}
        assert reporter.post(path,json=payload).status_code == 403
        assert reporter.post(path,json=payload,headers={'X-Forwarded-For':'192.0.2.99', 'Forwarded':'for=192.0.2.99'}).status_code == 403
        assert reporter.get('/api/shift-reports/today').json()['can_admin_delete'] is False
    monkeypatch.setenv('SHIFT_REPORT_ADMIN_IPS', '127.0.0.1')
    db_session.add(User(id=5, name='Synthetic admin', role=UserRole(admin_role)))
    db_session.commit()
    app.dependency_overrides[get_current_user] = lambda: {'id':'5','role':admin_role,'name':'Synthetic admin'}
    assert reporter.get('/api/shift-reports/today').json()['can_admin_delete'] is True
    assert reporter.post(path,json=dict(payload,reason=' ')).status_code == 422
    assert reporter.post(path,json=dict(payload,version_id=r['version_id']+1)).status_code == 409
    assert reporter.post(path,json=payload).status_code == 204
    archived = reporter.get(f"/api/shift-reports/{r['id']}").json()
    assert archived['deleted_at'] and archived['fields'] == r['fields']
    assert archived['finalized_at'] == r['finalized_at']
    assert reporter.get('/api/shift-reports').json() == []
    assert reporter.get('/api/shift-reports/today').json()['reports'] == []
    assert reporter.get('/api/shift-reports?deleted=true').json()[0]['id'] == r['id']
    audit = reporter.get(f"/api/shift-reports/{r['id']}/audit").json()
    assert audit[-1]['action'] == 'deleted' and audit[-1]['actor_id'] == 5
    assert audit[-1]['reason'] == 'Raport testowy'
    assert audit[-1]['before']['fields'] == r['fields']
    assert audit[-1]['before']['deleted_at'] is None and audit[-1]['after']['deleted_at']
    assert reporter.post(path,json={'version_id':archived['version_id'],'reason':'Again'}).status_code == 404
    assert save(reporter,archived,archived['fields']).status_code == (403 if admin_role in ('biuro','ceo') else 404)
    monkeypatch.setenv('SHIFT_REPORT_ADMIN_IPS', '192.0.2.99')
    for role in ('produkcja', 'biuro'):
        app.dependency_overrides[get_current_user] = lambda role=role: {'id':'4','role':role,'name':'Synthetic viewer'}
        assert reporter.get(f"/api/shift-reports/{r['id']}").status_code == 404
        assert reporter.get(f"/api/shift-reports/{r['id']}/audit").status_code == 404
        assert reporter.get('/api/shift-reports?deleted=true').status_code == 403
    app.dependency_overrides[get_current_user] = lambda: {'id':'4','role':'produkcja','name':'Synthetic reporter'}
    replacement = reporter.post('/api/shift-reports',json={'report_date':day,'shift':'I'}).json()
    assert replacement['id'] != r['id']
    assert db_session.query(ShiftReport).count() == 2


def test_admin_delete_rejects_production_regardless_of_ip_and_requires_login(reporter, monkeypatch):
    r = draft(reporter)
    path = f"/api/shift-reports/{r['id']}/delete"
    for value in ('', 'invalid', '127.0.0.1'):
        monkeypatch.setenv('SHIFT_REPORT_ADMIN_IPS', value)
        assert reporter.get('/api/shift-reports/today').json()['can_admin_delete'] is False
        assert reporter.post(path, json={'version_id':r['version_id'], 'reason':'Test'}).status_code == 403
    monkeypatch.setenv('SHIFT_REPORT_ADMIN_IPS', '127.0.0.1')
    app.dependency_overrides.pop(get_current_user)
    assert reporter.post(path, json={'version_id':r['version_id'], 'reason':'Test'}).status_code == 401


@pytest.mark.skipif(os.getenv('RUN_SHIFT_BROWSER') != '1', reason='Opt-in real Chromium workflow')
def test_empty_numbers_autosave_without_blur_and_draft_history_is_optional(reporter, live_server_url):
    from playwright.sync_api import sync_playwright, expect
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={'width':390,'height':844},is_mobile=True,has_touch=True)
        page.goto(live_server_url)
        page.locator('select').select_option('produkcja')
        page.locator('input[type=password]').fill('5849')
        page.get_by_role('button',name='Zaloguj się').click()
        page.get_by_role('button',name='Dzisiejszy raport',exact=True).click()
        fields = [('people','Ilość osób'),('assembled','Form złożonych'),('prepared','Form przygotowanych do zalania'),('poured','Gwiazdobloków zalanych na zmianie'),('checked','Form sprawdzonych po ok. 20 minutach'),('demoulded','Prefabrykatów wstępnie rozebranych'),('damaged','Uszkodzone — szt.')]
        for key,label in fields:
            control = page.get_by_label(label,exact=True)
            with page.expect_response(lambda r:r.request.method=='PUT' and '/api/shift-reports/' in r.url) as saved:
                control.fill('5')
            assert saved.value.status == 200
            with page.expect_response(lambda r:r.request.method=='PUT' and '/api/shift-reports/' in r.url) as saved:
                control.fill('')
            assert saved.value.status == 200 and saved.value.json()['fields'][key] is None
            expect(control).to_be_focused()
            expect(page.get_by_role('status')).to_contain_text('Zapisano')
        with page.expect_response(lambda r:r.request.method=='PUT' and '/api/shift-reports/' in r.url) as saved:
            page.get_by_label('Uszkodzone — szt.',exact=True).fill('0')
        assert saved.value.json()['fields']['damaged'] == 0
        expect(page.locator('.daily-summary')).not_to_contain_text('%')
        page.get_by_role('button',name='Historia zmian',exact=True).click()
        expect(page.locator('.audit-list details')).to_have_count(1)
        page.get_by_label('Pokaż zapisy szkicu',exact=True).check()
        expect(page.locator('.audit-list details')).to_have_count(2)
        page.locator('.audit-list details').last.locator('summary').click()
        expect(page.locator('.audit-list')).to_contain_text('Zapisów szkicu: 15')
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        browser.close()


@pytest.mark.skipif(os.getenv('RUN_SHIFT_BROWSER') != '1', reason='Opt-in real Chromium workflow')
def test_logout_flushes_draft_and_preserves_failed_save(reporter, live_server_url):
    from playwright.sync_api import sync_playwright, expect
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page()
        def login():
            page.locator('select').select_option('produkcja')
            page.locator('input[type=password]').fill('5849')
            page.get_by_role('button', name='Zaloguj się').click()
            page.get_by_role('button', name='Dzisiejszy raport', exact=True).click()
            expect(page.locator('.report-editor')).to_be_visible()
        page.goto(live_server_url)
        login()
        page.get_by_label('Uwagi ogólne', exact=True).fill('Saved during logout')
        page.get_by_role('button', name='Wyloguj', exact=True).click()
        expect(page.get_by_role('button', name='Zaloguj się')).to_be_visible()
        assert reporter.get('/api/shift-reports').json()[0]['fields']['remarks'] == 'Saved during logout'
        login()
        held = []
        page.route('**/api/shift-reports/*', lambda route: held.append(route) if route.request.method == 'PUT' else route.continue_())
        page.get_by_label('Uwagi ogólne', exact=True).fill('Pending save before logout')
        expect(page.get_by_role('status')).to_contain_text('Zapisywanie')
        page.get_by_role('button', name='Wyloguj', exact=True).click()
        expect(page.get_by_label('Uwagi ogólne', exact=True)).to_be_disabled()
        assert len(held) == 1
        held[0].continue_()
        expect(page.get_by_role('button', name='Zaloguj się')).to_be_visible()
        assert reporter.get('/api/shift-reports').json()[0]['fields']['remarks'] == 'Pending save before logout'
        page.unroute('**/api/shift-reports/*')
        login()
        page.route('**/api/shift-reports/*', lambda route: route.abort() if route.request.method == 'PUT' else route.continue_())
        page.get_by_label('Uwagi ogólne', exact=True).fill('Retain on failure')
        page.once('dialog', lambda dialog: dialog.dismiss())
        page.get_by_role('button', name='Wyloguj', exact=True).click()
        expect(page.get_by_role('alert')).to_be_visible()
        expect(page.get_by_label('Uwagi ogólne', exact=True)).to_have_value('Retain on failure')
        expect(page.get_by_role('button', name='Wyloguj', exact=True)).to_be_visible()
        page.unroute('**/api/shift-reports/*')
        page.get_by_role('button', name='Wyloguj', exact=True).click()
        expect(page.get_by_role('button', name='Zaloguj się')).to_be_visible()
        assert reporter.get('/api/shift-reports').json()[0]['fields']['remarks'] == 'Retain on failure'
        r = reporter.get('/api/shift-reports').json()[0]
        r = save(reporter, r, complete_fields(r)).json()
        assert reporter.post(f"/api/shift-reports/{r['id']}/finalize", json={'version_id':r['version_id']}).status_code == 200
        login()
        page.get_by_role('button', name='Koryguj raport', exact=True).click()
        page.get_by_label('Powód korekty', exact=True).fill('Unsubmitted correction')
        page.once('dialog', lambda dialog: dialog.dismiss())
        page.get_by_role('button', name='Wyloguj', exact=True).click()
        expect(page.get_by_label('Powód korekty', exact=True)).to_have_value('Unsubmitted correction')
        page.once('dialog', lambda dialog: dialog.accept())
        page.get_by_role('button', name='Wyloguj', exact=True).click()
        expect(page.get_by_role('button', name='Zaloguj się')).to_be_visible()
        assert reporter.get(f"/api/shift-reports/{r['id']}").json()['status'] == 'finalized'
        browser.close()


@pytest.mark.skipif(os.getenv('RUN_SHIFT_BROWSER') != '1', reason='Opt-in real Chromium workflow')
def test_admin_delete_finalized_report_on_phone(reporter, db_session, live_server_url, monkeypatch):
    monkeypatch.setenv('SHIFT_REPORT_ADMIN_IPS', '127.0.0.1')
    from playwright.sync_api import sync_playwright, expect
    r = draft(reporter)
    r = save(reporter,r,complete_fields(r)).json()
    assert reporter.post(f"/api/shift-reports/{r['id']}/finalize",json={'version_id':r['version_id']}).status_code == 200
    db_session.add(User(id=5,name='Synthetic admin',role=UserRole.technolog,
                        pin_hash=bcrypt.hashpw(b'6789',bcrypt.gensalt(rounds=4)).decode()))
    db_session.commit()
    app.dependency_overrides[get_current_user] = lambda: {'id':'5','role':'technolog','name':'Synthetic admin'}
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={'width':390,'height':844},is_mobile=True,has_touch=True)
        page.goto(live_server_url)
        page.locator('select').select_option('technolog')
        page.locator('input[type=password]').fill('6789')
        page.get_by_role('button',name='Zaloguj się').click()
        page.get_by_role('button',name='Raporty zmianowe',exact=True).click()
        page.get_by_role('button',name='Otwórz',exact=True).click()
        page.get_by_role('button',name='Usuń raport (administrator)',exact=True).click()
        expect(page.get_by_role('button',name='Potwierdź usunięcie')).to_be_disabled()
        page.get_by_role('button',name='Anuluj usunięcie').click()
        expect(page.locator('.delete-confirmation')).not_to_be_visible()
        page.get_by_role('button',name='Usuń raport (administrator)',exact=True).click()
        page.get_by_label('Powód usunięcia',exact=True).fill('Raport testowy')
        page.get_by_role('button',name='Potwierdź usunięcie').click()
        expect(page.locator('.report-editor')).not_to_be_visible()
        expect(page.locator('.report-history tbody tr')).to_have_count(0)
        page.get_by_role('button',name='Usunięte',exact=True).click()
        page.get_by_role('button',name='Otwórz',exact=True).click()
        expect(page.locator('.report-editor')).to_contain_text('Usunięty')
        expect(page.get_by_role('button',name='Koryguj raport')).to_have_count(0)
        page.get_by_role('button',name='Historia zmian',exact=True).click()
        expect(page.locator('.audit-list')).to_contain_text('Usunięto przez administratora')
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        browser.close()


@pytest.mark.skipif(os.getenv('RUN_SHIFT_BROWSER') != '1', reason='Opt-in real Chromium workflow')
def test_office_can_read_report_on_phone(reporter, db_session, live_server_url):
    from playwright.sync_api import sync_playwright, expect
    report = draft(reporter)
    report = save(reporter, report, complete_fields(report)).json()
    assert reporter.post(f"/api/shift-reports/{report['id']}/finalize", json={'version_id': report['version_id']}).status_code == 200
    db_session.add(User(id=5, name='Synthetic office', role=UserRole.biuro,
                        pin_hash=bcrypt.hashpw(b'6789', bcrypt.gensalt(rounds=4)).decode()))
    db_session.commit()
    # This harness shares one SQLite Session; mirror its synthetic identity
    # override while the shell performs parallel background reads.
    app.dependency_overrides[get_current_user] = lambda: {'id':'5', 'role':'biuro', 'name':'Synthetic office'}
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={'width':390, 'height':844}, is_mobile=True, has_touch=True)
        page.goto(live_server_url)
        page.locator('select').select_option('biuro')
        page.locator('input[type=password]').fill('6789')
        page.get_by_role('button', name='Zaloguj się').click()
        page.get_by_role('button', name='Raporty zmianowe', exact=True).click()
        page.get_by_role('button', name='Otwórz', exact=True).click()
        expect(page.get_by_label('Ilość osób', exact=True)).to_be_disabled()
        expect(page.get_by_role('button', name='Koryguj raport')).to_have_count(0)
        expect(page.get_by_role('button', name='Zakończ raport', exact=True)).to_have_count(0)
        page.get_by_role('button', name='Historia zmian', exact=True).click()
        expect(page.locator('.audit-list')).to_contain_text('Zakończono')
        page.get_by_role('button', name='Podgląd A4', exact=True).click()
        expect(page.locator('.shift-print')).to_be_visible()
        assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
        browser.close()


@pytest.mark.skipif(os.getenv('RUN_SHIFT_BROWSER') != '1', reason='Opt-in real Chromium workflow')
@pytest.mark.parametrize('viewport', [{'width':1366,'height':900}, {'width':390,'height':844}, {'width':360,'height':800}])
def test_real_browser_flow(reporter, live_server_url, tmp_path, viewport):
    from playwright.sync_api import sync_playwright, expect
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport=viewport, is_mobile=viewport['width'] < 600, has_touch=viewport['width'] < 600)
        page.goto(live_server_url)
        page.locator('select').select_option('produkcja')
        page.locator('input[type=password]').fill('5849')
        page.get_by_role('button', name='Zaloguj się').click()
        page.get_by_role('button', name='Dzisiejszy raport', exact=True).click()
        expect(page.get_by_role('heading', name='Raport ', exact=False).filter(has_text='Zmiana II')).to_be_visible()
        assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
        page.get_by_label('Ilość osób', exact=True).fill('3')
        for label in ['Form złożonych', 'Form przygotowanych do zalania', 'Gwiazdobloków zalanych na zmianie', 'Form sprawdzonych po ok. 20 minutach', 'Prefabrykatów wstępnie rozebranych', 'Uszkodzone — szt.']:
            page.get_by_label(label, exact=True).fill('0' if label.startswith('Uszkodzone') else '12')
        page.get_by_label('Wibratory', exact=True).select_option('sprawne')
        page.get_by_label('Przedłużacze', exact=True).select_option('sprawne')
        for group in page.locator('.check-options').all():
            if group.get_by_role('button', name='OK', exact=True).count():
                group.get_by_role('button', name='OK', exact=True).click()
        page.get_by_label('Kierownik / osoba kontrolująca', exact=True).fill('Synthetic controller')
        page.get_by_role('button', name='Zapisz teraz / spróbuj ponownie').click()
        expect(page.get_by_role('status')).to_contain_text('Zapisano')
        # Network error and conflict must leave the input untouched.
        page.route('**/api/shift-reports/*', lambda route: route.abort() if route.request.method == 'PUT' else route.continue_())
        page.get_by_label('Uwagi ogólne', exact=True).fill('Retained after network failure')
        expect(page.get_by_role('status')).to_contain_text('Błąd zapisu')
        expect(page.get_by_label('Uwagi ogólne', exact=True)).to_have_value('Retained after network failure')
        page.unroute('**/api/shift-reports/*')
        page.route('**/api/shift-reports/*', lambda route: route.fulfill(status=409, json={'detail': 'Raport został zmieniony.'}) if route.request.method == 'PUT' else route.continue_())
        page.get_by_role('button', name='Zapisz teraz / spróbuj ponownie').click()
        expect(page.get_by_role('alert')).to_contain_text('Raport został zmieniony')
        expect(page.get_by_label('Uwagi ogólne', exact=True)).to_have_value('Retained after network failure')
        page.unroute('**/api/shift-reports/*')
        page.get_by_role('button', name='Zapisz teraz / spróbuj ponownie').click()
        expect(page.get_by_role('status')).to_contain_text('Zapisano')
        page.get_by_role('button', name='Zakończ raport', exact=True).click()
        expect(page.get_by_role('button', name='Koryguj raport')).to_be_visible()
        expect(page.get_by_label('Ilość osób', exact=True)).to_be_disabled()
        expect(page.locator('.history-panel')).to_contain_text('Zakończony')
        assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
        page.get_by_role('button', name='Podgląd A4').click()
        expect(page.locator('.shift-print')).to_be_visible()
        expect(page.locator('.shift-print')).to_contain_text('KONTROLA KOŃCOWA')
        page.pdf(path=str(tmp_path / 'shift-report.pdf'), format='A4')
        page.screenshot(path=str(tmp_path / 'shift-report.png'), full_page=True)
        page.get_by_role('button', name='Koryguj raport').click()
        page.get_by_label('Powód korekty', exact=True).fill('Verified quantity')
        page.get_by_label('Gwiazdobloków zalanych na zmianie', exact=True).fill('13')
        page.get_by_role('button', name='Zatwierdź korektę').click()
        expect(page.locator('.history-panel')).to_contain_text('Skorygowany')
        page.get_by_role('button', name='Historia zmian', exact=True).click()
        expect(page.locator('.audit-list')).to_contain_text('Korekta')
        browser.close()
