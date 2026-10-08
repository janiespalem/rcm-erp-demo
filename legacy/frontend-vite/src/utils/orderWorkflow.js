import { isOverdue } from '@/utils/format'

const TERMINAL_STATUSES = ['gotowe', 'wydane', 'rejected', 'odrzut']
const BIURO_ROLES = ['biuro', 'technolog']
const TECHNOLOG_ROLES = ['technolog']

export function stageForStatus(status) {
  if (['draft', 'standard', 'niestandard'].includes(status)) return 'nowe'
  if (status === 'quoted') return 'wycena'
  if (status === 'in_production') return 'produkcja'
  if (['gotowe', 'wydane'].includes(status)) return 'gotowe'
  return 'inne'
}

// Whose move is it on this order, seen from `role`'s side.
// mine=true → the viewer acts; otherwise `actor` names who the order waits for.
export function nextStepFor(order, role) {
  const biuroSide = BIURO_ROLES.includes(role)
  const techSide = TECHNOLOG_ROLES.includes(role)
  switch (order.status) {
    case 'draft':
    case 'niestandard':
    case 'standard':
      return techSide
        ? { mine: true, label: 'Wyceń zlecenie' }
        : { mine: false, actor: 'Technolog', label: 'wycena' }
    case 'quoted':
      return biuroSide
        ? { mine: true, label: 'Zatwierdź wycenę' }
        : { mine: false, actor: 'Biuro', label: 'zatwierdzenie' }
    case 'in_production':
      // Jeden krok zamknięcia: produkcja gotowa = zlecenie zakończone (→ wydane).
      return techSide
        ? { mine: true, label: 'Zakończ zlecenie' }
        : { mine: false, actor: 'Warsztat', label: 'w realizacji' }
    case 'gotowe':
      // Legacy: stare zlecenia zatrzymane w 'gotowe' domykamy jednym "Wydaj".
      return biuroSide
        ? { mine: true, label: 'Wydaj klientowi' }
        : { mine: false, actor: 'Biuro', label: 'wydanie' }
    case 'wydane':
      return { mine: false, actor: null, label: 'Zakończone' }
    case 'rejected':
    case 'odrzut':
      return { mine: false, actor: null, label: 'Odrzucone' }
    default:
      return { mine: false, actor: null, label: '—' }
  }
}

export function isOrderOverdue(order) {
  return isOverdue(order.deadline) && !TERMINAL_STATUSES.includes(order.status)
}

export function canQuote(order) {
  return ['niestandard', 'standard', 'quoted', 'in_production'].includes(order.status)
}

export function canConfirm(order, role) {
  return order.status === 'quoted' && BIURO_ROLES.includes(role)
}

export function canCompleteProduction(order, role) {
  return order.status === 'in_production' && TECHNOLOG_ROLES.includes(role)
}

export function canDeliver(order, role) {
  // Zamknięcie z produkcji (technolog) lub legacy 'gotowe' (biuro).
  if (order.status === 'in_production') return TECHNOLOG_ROLES.includes(role)
  return order.status === 'gotowe' && BIURO_ROLES.includes(role)
}
