import argparse
import subprocess

from fixtures import login, request


def verify(base):
    token = login(base, 'office')
    orders = request(base, '/api/v1/orders', token=token)['items']
    order = next(row for row in orders if row['client'] == 'Demo North Workshop 1')
    document = request(base, f"/api/v1/orders/{order['id']}/documents/oferta", token=token, raw=True)
    assert document.startswith(b'%PDF-'), 'Offer did not return PDF'
    extracted = subprocess.run(['pdftotext', '-layout', '-', '-'], input=document, capture_output=True, check=True).stdout.decode()
    text = ' '.join(extracted.split())
    assert 'Wystawił — Biuro FactoryFlow' in text, 'Offer signature does not identify FactoryFlow'
    assert 'RCM' not in text, 'Offer contains employee ERP branding'
    assert 'Demo North Workshop 1' in text and 'DEMO-NOT-A-TAX-ID' in text
    print('PASS: generated offer PDF identifies FactoryFlow, synthetic customer and synthetic company details.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--base-url', default='http://127.0.0.1:18081')
    args = parser.parse_args()
    verify(args.base_url.rstrip('/'))
