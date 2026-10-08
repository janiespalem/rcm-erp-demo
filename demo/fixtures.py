import json
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo


PASSWORD = 'FactoryFlow-Demo-2026!'
ACCOUNTS = ['office', 'engineer', 'production', 'director', 'sales.north', 'sales.south']


def request(base, path, payload=None, token=None, raw=False):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    data = None if payload is None else json.dumps(payload).encode()
    try:
        with urllib.request.urlopen(urllib.request.Request(base + path, data=data, headers=headers), timeout=30) as response:
            body = response.read()
            return body if raw else json.loads(body) if body else None
    except urllib.error.HTTPError as error:
        error.msg = str(error.reason) + ': ' + error.read().decode()
        raise


def login(base, username):
    result = request(base, '/api/v1/session/login/password', {'username': username, 'password': PASSWORD})
    return result['access_token']


def seed(base, engine):
    from sqlalchemy import text
    tokens = {name: login(base, name) for name in ACCOUNTS}
    today = datetime.now(ZoneInfo('Europe/Warsaw')).date()

    def step(name, path, payload, actor='office'):
        with engine.connect() as connection:
            existing = connection.execute(text('SELECT result FROM factoryflow_demo.bootstrap_steps WHERE name=:name'), {'name': name}).scalar()
            if existing is not None:
                return existing
        payload['requestId'] = str(uuid.uuid5(uuid.NAMESPACE_URL, 'https://factoryflow.example/demo/' + name))
        result = request(base, path, payload, tokens[actor])
        with engine.begin() as connection:
            connection.execute(text('INSERT INTO factoryflow_demo.bootstrap_steps(name,result) VALUES(:name,CAST(:result AS jsonb)) ON CONFLICT DO NOTHING'), {'name': name, 'result': json.dumps(result)})
        return result

    for side, actor in [('North', 'sales.north'), ('South', 'sales.south')]:
        for number in range(1, 4):
            key = f'customer-{side}-{number}'
            customer = step(key, '/api/v1/crm/customers', {'fields': {'displayName': f'Demo {side} Workshop {number}', 'contactPerson': f'Demo Buyer {number}', 'email': f'buyer{number}@{side.lower()}.example', 'source': 'Synthetic fixture', 'originalNote': 'Entirely fictional customer, contact details and requirements.'}, 'isSynthetic': True}, actor)['customer']
            topic = step(key + '-topic', f"/api/v1/crm/customers/{customer['id']}/topics", {'fields': {'products': ['Zbrojenie'], 'need': 'Synthetic reinforcement inquiry', 'state': 'offer', 'nextContact': {'date': (today + timedelta(days=number-2)).isoformat(), 'description': None, 'action': 'Discuss fictional offer'}}}, actor)
            if side == 'North' and number == 1:
                step(key + '-contact', f"/api/v1/crm/topics/{topic['id']}/contacts", {
                    'expectedVersion': topic['version'], 'note': 'DEMO CONTACT: fictional buyer requested a sample offer; no real communication occurred.',
                    'state': 'waiting', 'nextContact': {'date': today.isoformat(), 'description': None, 'action': 'Review the synthetic sample offer'}}, actor)
    materials = []
    for number, name in enumerate(['Demo steel profile', 'Demo steel plate', 'Demo fastener kit'], 1):
        materials.append(step(f'catalog-material-{number}', '/api/v1/catalog/materials', {
            'name': name, 'category': 'demo', 'defaultRatePlnKg': number * 10, 'isActive': True,
            'notes': 'Fictional demonstration material and price; not a purchasing specification.'}, 'engineer'))
    operations = []
    for number, name in enumerate(['Demo sample cutting', 'Demo trial assembly', 'Demo visual review'], 1):
        operations.append(step(f'catalog-operation-{number}', '/api/v1/catalog/operations', {
            'name': name, 'department': 'Demo workshop', 'defaultRate': 100, 'formula': None}, 'engineer'))
    template = step('template', '/api/v1/templates', {'draft': {'name': 'Demo reinforcement assembly', 'category': 'demo', 'operations': [], 'materials': [], 'instructions': [], 'machines': [], 'basePricePln': 1200, 'marginPct': 0.25, 'projectCode': 'DEMO-01', 'positionNumber': '001', 'notes': 'Synthetic assembly and price; not a manufacturing specification.'}}, 'engineer')
    template = step('template-content-v2', f"/api/v1/templates/{template['id']}/update", {
        'expectedVersion': template['version'], 'draft': {
            'name': 'Demo reinforcement assembly', 'category': 'demo',
            'operations': [
                {'catalog_id': operations[0]['id'], 'op': 'Demo sample cutting', 'wydział': 'Demo workshop', 'hours': 1, 'rate_per_hour': 100},
                {'catalog_id': operations[1]['id'], 'op': 'Demo trial assembly', 'wydział': 'Demo workshop', 'hours': 2, 'rate_per_hour': 100},
                {'catalog_id': operations[2]['id'], 'op': 'Demo visual review', 'wydział': 'Demo workshop', 'hours': 1, 'rate_per_hour': 100}],
            'materials': [
                {'approved_material_id': materials[0]['id'], 'part_no': 'DEMO-P01', 'mat': 'Demo steel profile', 'dim': 'Synthetic 40 x 40 x 500 mm', 'qty': 4, 'unit': 'szt', 'mass_kg': 8},
                {'approved_material_id': materials[1]['id'], 'part_no': 'DEMO-P02', 'mat': 'Demo steel plate', 'dim': 'Synthetic 200 x 100 x 5 mm', 'qty': 2, 'unit': 'szt', 'mass_kg': 2},
                {'approved_material_id': materials[2]['id'], 'part_no': 'DEMO-P03', 'mat': 'Demo fastener kit', 'dim': 'Fictional sample kit', 'qty': 1, 'unit': 'kpl', 'mass_kg': 1}],
            'instructions': [
                {'order': 1, 'text': 'DEMO-SOP-01: compare the fictional part numbers with the demonstration material list.'},
                {'order': 2, 'text': 'DEMO-SOP-02: record sample assembly observations in this training worksheet.'},
                {'order': 3, 'text': 'DEMO-SOP-03: mark the exercise reviewed; this is not an approved manufacturing instruction.'}],
            'machines': [{'name': 'Demo training workbench'}, {'name': 'Demo inspection station'}],
            'basePricePln': 1200, 'marginPct': 0.25, 'projectCode': 'DEMO-01', 'positionNumber': '001',
            'notes': 'DEMO-01 / 001: fictional harbour demonstration assembly. All dimensions, quantities, rates and instructions are training data.'}}, 'engineer')
    for number in range(1, 4):
        order = step(f'order-{number}', '/api/v1/orders', {'fields': {'client': f'Demo North Workshop {number}', 'deadline': (today + timedelta(days=14+number)).isoformat(), 'description': 'Fictional assembly for local demonstration', 'approvedMaterialId': materials[0]['id'], 'material': 'Demo steel profile', 'sopName': 'DEMO-SOP-01', 'notes': 'Synthetic price and specification', 'quantity': 2, 'templateId': template['id'], 'estimatedValue': 2400}})
        if number < 3:
            step(f'triage-{number}', f"/api/v1/orders/{order['id']}/triage", {})
            step(f'quote-{number}', f"/api/v1/orders/{order['id']}/quote", {'materialCost': 800, 'laborHours': 6, 'overheadPct': 0.10, 'marginPct': 0.25, 'transportCost': 100}, 'engineer')
        if number == 2:
            step('order-confirm', f"/api/v1/orders/{order['id']}/confirm", {})
        if number == 1:
            # A generated document is itself the fixture attachment; it contains no imported bytes.
            document = request(base, f"/api/v1/orders/{order['id']}/documents/arkusz", token=tokens['office'], raw=True)
            name = 'order-document-attachment'
            with engine.connect() as connection:
                exists = connection.execute(text('SELECT 1 FROM factoryflow_demo.bootstrap_steps WHERE name=:name'), {'name': name}).scalar()
            if not exists:
                request_id = str(uuid.uuid5(uuid.NAMESPACE_URL, 'https://factoryflow.example/demo/' + name))
                req = urllib.request.Request(base + f"/api/v1/orders/{order['id']}/attachments?filename=synthetic-drawing.pdf&requestId={request_id}", data=document, headers={'Authorization': 'Bearer ' + tokens['office'], 'Content-Type': 'application/pdf'})
                with urllib.request.urlopen(req, timeout=30) as response:
                    attachment = json.load(response)
                with engine.begin() as connection:
                    connection.execute(text('INSERT INTO factoryflow_demo.bootstrap_steps(name,result) VALUES(:name,CAST(:result AS jsonb)) ON CONFLICT DO NOTHING'), {'name': name, 'result': json.dumps(attachment)})
    contract = step('contract', '/api/v1/production/contracts', {'fields': {'name': 'Demo harbour reinforcement', 'reference': 'DEMO-CONTRACT-001', 'plannedTetrapods': 24, 'deadline': (today + timedelta(days=30)).isoformat(), 'openingDate': today.isoformat(), 'openingSteel': {'diameter6': 1000000, 'diameter12': 2000000, 'diameter16': 3000000}, 'normConfirmed': False}, 'isSynthetic': True})
    step('delivery', f"/api/v1/production/contracts/{contract['id']}/deliveries", {'expectedContractVersion': contract['version'], 'fields': {'deliveryDate': today.isoformat(), 'steel': {'diameter6': 500000, 'diameter12': 700000, 'diameter16': 900000}, 'note': 'Fictional delivery; integer grams.'}})
    report = step('report', '/api/v1/shift-reports', {'reportDate': today.isoformat(), 'shift': 'I'}, 'production')
    fields = {'leader': 'Demo Shift Leader', 'responsible': 'Demo Supervisor', 'people': 4, 'reference': 'DEMO-CONTRACT-001', 'assembled': 8, 'prepared': 8, 'poured': 6, 'checked': 6, 'demoulded': 4, 'damaged': 0, 'vibrators': {'condition': 'sprawne'}, 'extensions': {'condition': 'sprawne'}, 'checks': ['OK'] * 9, 'productionPerson': 'Demo Production', 'controller': 'Demo Controller', 'remarks': 'Synthetic demonstration report.'}
    saved = step('report-save', f"/api/v1/shift-reports/{report['id']}/save", {'expectedVersion': report['version'], 'fields': fields}, 'production')
    finalized = step('report-finalize', f"/api/v1/shift-reports/{report['id']}/finalize", {'expectedVersion': saved['version']}, 'production')
    step('report-link', f"/api/v1/production/reports/{report['id']}/link", {'expectedReportVersion': finalized['version'], 'expectedLinkVersion': 0, 'contractId': contract['id']}, 'engineer')
    print('Synthetic fixtures created or retained; the finalized report awaits Office acceptance.')
