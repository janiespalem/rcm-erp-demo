import { isOverdue } from '@/utils/format'

// 'done' is a legacy backend alias for a delivered order (grouped with 'wydane');
// kept here so historical records still map to a stage and never count as overdue.
const TERMINAL_STATUSES = ['gotowe', 'wydane', 'done', 'rejected', 'odrzut']
const BIURO_ROLES = ['biuro', 'dyrektor_produkcji']
const TECHNOLOG_ROLES = ['technolog', 'dyrektor_produkcji']

export function stageForStatus(status) {
  if (['draft', 'triage', 'standard', 'niestandard'].includes(status)) return 'nowe'
  if (status === 'quoted') return 'wycena'
  if (['in_production', 'w_trakcie'].includes(status)) return 'produkcja'
  if (['gotowe', 'wydane', 'done'].includes(status)) return 'gotowe'
  return 'inne'
}

export function nextStepLabel(status) {
  switch (status) {
    case 'draft':
    case 'triage':
    case 'niestandard':
      return 'Do wyceny'
    case 'standard':
      return 'Standard - sprawdź'
    case 'quoted':
      return 'Do zatwierdzenia'
    case 'in_production':
    case 'w_trakcie':
      return 'W realizacji'
    case 'gotowe':
      return 'Do wydania'
    case 'wydane':
    case 'done':
      return 'Wydane'
    case 'rejected':
    case 'odrzut':
      return 'Odrzucone'
    default:
      return '—'
  }
}

export function isOrderOverdue(order) {
  return isOverdue(order.deadline) && !TERMINAL_STATUSES.includes(order.status)
}

export function canQuote(order) {
  return ['niestandard', 'quoted', 'in_production'].includes(order.status)
}

export function canConfirm(order, role) {
  return order.status === 'quoted' && BIURO_ROLES.includes(role)
}

export function canStartProduction(order, role) {
  return order.status === 'in_production' && TECHNOLOG_ROLES.includes(role)
}

export function canCompleteProduction(order, role) {
  return order.status === 'w_trakcie' && TECHNOLOG_ROLES.includes(role)
}

export function canDeliver(order, role) {
  return order.status === 'gotowe' && BIURO_ROLES.includes(role)
}
