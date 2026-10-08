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
  return deadline && deadline < localDate(new Date())
}

export function defaultDeadline() {
  const d = new Date()
  d.setDate(d.getDate() + 14)
  return localDate(d)
}

function localDate(date) {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

// User-facing status labels. Deliberately coarser than the backend vocabulary:
// users think in stages, not in triage branches. `in_production` covers the
// whole shop-floor phase (from confirm until the order is marked gotowe).
export const STATUS_PL = {
  draft:         'Nowe',
  standard:      'Standard',
  niestandard:   'Do wyceny',
  rejected:      'Odrzucone',
  quoted:        'Wycenione',
  in_production: 'W produkcji',
  gotowe:        'Gotowe',
  wydane:        'Wydane',
  odrzut:        'Odrzucone',
}
