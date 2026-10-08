import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import date, timedelta
import json
from pathlib import Path
import tempfile
import urllib.error
import uuid

from fixtures import ACCOUNTS, login, request


def expect_status(status, action):
    try:
        action()
    except urllib.error.HTTPError as error:
        assert error.code == status, (error.code, error.read().decode())
    else:
        raise AssertionError(f'Expected HTTP {status}')


def verify(base, state_file, restart):
    health = request(base, '/health')
    for key in ['identityNativeReady', 'ordersNativeReady', 'catalogNativeReady', 'shiftReportsNativeReady', 'productionReviewReady']:
        assert health[key], (key, health)
    tokens = {actor: login(base, actor) for actor in ACCOUNTS}
    if restart:
        saved = json.loads(state_file.read_text())
        customer = request(base, f"/api/v1/crm/customers/{saved['customer']}/record", token=tokens['sales.north'])
        assert customer['fields']['displayName'] == saved['name']
        order = request(base, f"/api/v1/orders/{saved['order']}", token=tokens['office'])
        assert order['client'] == saved['name']
        attachment = request(base, f"/api/v1/orders/{saved['order']}/attachments/{saved['attachment']}/download", token=tokens['office'], raw=True)
        assert attachment.startswith(b'%PDF-')
        print('PASS: saved customer and order survived API restart.')
        return
    north = request(base, '/api/v1/crm/customers', token=tokens['sales.north'])['items']
    south = request(base, '/api/v1/crm/customers', token=tokens['sales.south'])['items']
    assert north and south
    north_ids = {item['customer']['id'] for item in north}
    south_ids = {item['customer']['id'] for item in south}
    assert not north_ids.intersection(south_ids)
    target = north[0]['customer']['id']
    expect_status(404, lambda: request(base, f'/api/v1/crm/customers/{target}/record', token=tokens['sales.south']))
    expect_status(403, lambda: request(base, '/api/v1/orders', token=tokens['sales.south']))
    name = 'Synthetic verification ' + uuid.uuid4().hex[:10]
    command = {'requestId': str(uuid.uuid4()), 'fields': {'displayName': name, 'originalNote': 'Synthetic concurrent/replay test'}, 'isSynthetic': True}
    with ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(lambda _: request(base, '/api/v1/crm/customers', command, tokens['sales.north']), range(8)))
    ids = {item['customer']['id'] for item in results}
    assert len(ids) == 1, 'Concurrent retries created duplicate customers'
    customer = results[0]['customer']
    replay = request(base, '/api/v1/crm/customers', command, tokens['sales.north'])
    assert replay == results[0], 'Lost-response replay changed result'
    expect_status(409, lambda: request(base, '/api/v1/crm/customers', {**command, 'fields': {'displayName': 'Changed payload'}}, tokens['sales.north']))
    edit = {'requestId': str(uuid.uuid4()), 'expectedVersion': customer['version'], 'fields': customer['fields']}
    import urllib.request
    def put(payload):
        req = urllib.request.Request(base + f"/api/v1/crm/customers/{customer['id']}", data=json.dumps(payload).encode(), headers={'Authorization': 'Bearer ' + tokens['sales.north'], 'Content-Type': 'application/json'}, method='PUT')
        with urllib.request.urlopen(req, timeout=30) as response:
            return json.load(response)
    put(edit)
    expect_status(409, lambda: put({**edit, 'requestId': str(uuid.uuid4())}))
    order_command = {'requestId': str(uuid.uuid4()), 'fields': {'client': name, 'deadline': (date.today() + timedelta(days=7)).isoformat(), 'description': 'Synthetic restart verification'}}
    with ThreadPoolExecutor(max_workers=8) as pool:
        orders = list(pool.map(lambda _: request(base, '/api/v1/orders', order_command, tokens['office']), range(8)))
    assert len({item['id'] for item in orders}) == 1, 'Concurrent order retries created duplicates'
    order = orders[0]
    document = request(base, f"/api/v1/orders/{order['id']}/documents/arkusz", token=tokens['office'], raw=True)
    assert document.startswith(b'%PDF-'), 'Native document did not return PDF'
    upload_url = base + f"/api/v1/orders/{order['id']}/attachments?filename=synthetic-check.pdf&requestId={uuid.uuid4()}"
    upload = urllib.request.Request(upload_url, data=document, headers={'Authorization': 'Bearer ' + tokens['office'], 'Content-Type': 'application/pdf'})
    with urllib.request.urlopen(upload, timeout=30) as response:
        attachment = json.load(response)
    downloaded = request(base, f"/api/v1/orders/{order['id']}/attachments/{attachment['id']}/download", token=tokens['office'], raw=True)
    assert downloaded == document, 'Attachment bytes changed'
    reports = request(base, '/api/v1/shift-reports', token=tokens['office'])['items']
    report = next(item for item in reports if item['fields']['reference'] == 'DEMO-CONTRACT-001')
    state = request(base, f"/api/v1/production/reports/{report['id']}", token=tokens['office'])
    if state['state'] != 'accepted':
        reviewed = request(base, f"/api/v1/production/reports/{report['id']}/review", {'requestId': str(uuid.uuid4()), 'expectedReportVersion': report['version'], 'expectedLinkVersion': state['link']['version'], 'decision': 'accepted', 'reason': 'Synthetic acceptance test'}, tokens['office'])
        assert reviewed['state'] == 'accepted', reviewed
    state_file.write_text(json.dumps({'customer': customer['id'], 'order': order['id'], 'attachment': attachment['id'], 'name': name}))
    print('PASS: all six logins; CRM team isolation; eight competing retries per customer/order; lost-response replay; stale edit conflict; native PDF and attachment; report acceptance.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--base-url', default='http://127.0.0.1:18081')
    parser.add_argument('--state-file', type=Path, default=Path(tempfile.gettempdir()) / 'factoryflow-verification.json')
    parser.add_argument('--after-restart', action='store_true')
    args = parser.parse_args()
    verify(args.base_url.rstrip('/'), args.state_file, args.after_restart)
