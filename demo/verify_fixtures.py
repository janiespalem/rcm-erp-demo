import argparse
import json
from pathlib import Path
import tempfile

from fixtures import login, request


def verify(base, snapshot_path, compare, documents):
    engineer = login(base, 'engineer')
    north = login(base, 'sales.north')
    materials = request(base, '/api/v1/catalog/materials?q=Demo', token=engineer)['items']
    operations = request(base, '/api/v1/catalog/operations?q=Demo', token=engineer)['items']
    assert len(materials) == len(operations) == 3
    assert {row['defaultRatePlnKg'] for row in materials} == {10, 20, 30}
    assert all(row['defaultRate'] == 100 for row in operations)
    templates = request(base, '/api/v1/templates?projectCode=DEMO-01', token=engineer)['items']
    assert len(templates) == 1
    template = templates[0]
    assert template['positionNumber'] == '001'
    assert len(template['materials']) == len(template['operations']) == len(template['instructions']) == 3
    assert len(template['machines']) == 2
    assert {row['approved_material_id'] for row in template['materials']} == {row['id'] for row in materials}
    assert {row['catalog_id'] for row in template['operations']} == {row['id'] for row in operations}
    projects = request(base, '/api/v1/projects?q=DEMO-01', token=engineer)['items']
    assert projects == [{'code': 'DEMO-01', 'positionsCount': 1}], projects
    customers = request(base, '/api/v1/crm/customers?q=Demo%20North%20Workshop%201', token=north)['items']
    assert len(customers) == 1 and len(customers[0]['topics']) == 1
    topic = customers[0]['topics'][0]
    history = request(base, f"/api/v1/crm/topics/{topic['id']}/history", token=north)['items']
    contacts = [event for event in history if event['kind'] == 'conversation' and event['note'].startswith('DEMO CONTACT:')]
    assert len(contacts) == 1 and topic['fields']['state'] == 'waiting'
    assert topic['fields']['nextContact']['action'] == 'Review the synthetic sample offer'
    queue = request(base, '/api/v1/crm/queue?group=today', token=north)['items']
    assert any(row['topic']['id'] == topic['id'] for row in queue)
    snapshot = {
        'materials': sorted((row['id'], row['version']) for row in materials),
        'operations': sorted((row['id'], row['version']) for row in operations),
        'template': [template['id'], template['version']],
        'topic': [topic['id'], topic['version']],
        'history': sorted(event['id'] for event in history),
    }
    snapshot = json.loads(json.dumps(snapshot))
    if compare:
        assert json.loads(snapshot_path.read_text()) == snapshot, 'Bootstrap duplicated or rewrote fixture records'
    else:
        snapshot_path.write_text(json.dumps(snapshot, indent=2) + '\n')
    documents.mkdir(parents=True, exist_ok=True)
    for filename, endpoint in [
        ('template.pdf', f"/api/v1/templates/{template['id']}/arkusz"),
        ('project.pdf', '/api/v1/projects/DEMO-01/arkusze'),
    ]:
        content = request(base, endpoint, token=engineer, raw=True)
        assert content.startswith(b'%PDF-')
        (documents / filename).write_bytes(content)
    print('PASS: synthetic catalog, complete SOP/BOM, project PDF, one recorded CRM contact and follow-up queue; stable fixture IDs and versions.' if compare else 'PASS: synthetic catalog, complete SOP/BOM, project PDF, recorded CRM contact and follow-up queue.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--base-url', default='http://127.0.0.1:18081')
    parser.add_argument('--snapshot', type=Path, default=Path(tempfile.gettempdir()) / 'factoryflow-fixtures.json')
    parser.add_argument('--documents', type=Path, default=Path(tempfile.gettempdir()) / 'factoryflow-fixture-documents')
    parser.add_argument('--compare', action='store_true')
    args = parser.parse_args()
    verify(args.base_url.rstrip('/'), args.snapshot, args.compare, args.documents)
