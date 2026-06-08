export function moneyLabel(value) {
  const amount = Number(value || 0)
  return amount > 0 ? `${amount.toLocaleString('pl-PL', { maximumFractionDigits: 2 })} zł` : '—'
}

export function hoursLabel(value) {
  const hours = Number(value || 0)
  return hours > 0 ? `${hours.toLocaleString('pl-PL', { maximumFractionDigits: 2 })} h` : '—'
}

export function shortText(value, limit = 80) {
  const text = String(value || '').trim()
  if (text.length <= limit) return text
  return `${text.slice(0, limit - 1).trim()}…`
}

export function biuroTemplatePriceLabel(t) {
  if (t.project_code) return 'Do wyceny'
  return moneyLabel(t.base_price_pln)
}

export function isOverdue(deadline) {
  return deadline && deadline < new Date().toISOString().slice(0, 10)
}

export function defaultDeadline() {
  const d = new Date()
  d.setDate(d.getDate() + 14)
  return d.toISOString().slice(0, 10)
}

export const STATUS_PL = {
  draft:         'Szkic',
  triage:        'Triage',
  standard:      'Standard',
  niestandard:   'Niestandardowy',
  rejected:      'Odrzucony',
  quoted:        'Wyceniony',
  in_production: 'W produkcji',
  w_trakcie:     'W trakcie',
  gotowe:        'Gotowe',
  wydane:        'Wydane',
  done:          'Zakończone',
  odrzut:        'Odrzut',
}
