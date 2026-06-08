import { ref, computed } from 'vue'
import { api } from './useApi'
import { useAuth } from './useAuth'

const orders       = ref([])
const serviceHistory = ref([])

export function useOrders() {
  const { currentUser } = useAuth()

  async function loadOrders() {
    const [all, history] = await Promise.all([
      api('/orders'),
      currentUser.value?.role === 'biuro'
        ? api('/service-history')
        : Promise.resolve(serviceHistory.value),
    ])
    orders.value = all.filter(o => o.status !== 'cancelled')
    if (currentUser.value?.role === 'biuro') serviceHistory.value = history
  }

  async function deleteOrder(id) {
    await api(`/orders/${id}`, { method: 'DELETE' })
    await loadOrders()
  }

  async function confirmOrder(id) {
    await api(`/orders/${id}/confirm`, { method: 'POST' })
    await loadOrders()
  }

  async function startOrder(id) {
    await api(`/orders/${id}/start`, { method: 'POST' })
    await loadOrders()
  }

  async function completeOrder(id) {
    await api(`/orders/${id}/complete`, { method: 'POST' })
    await loadOrders()
  }

  async function deliverOrder(id) {
    await api(`/orders/${id}/deliver`, { method: 'POST' })
    await loadOrders()
  }

  async function saveEdit(id, form) {
    const payload = {}
    if (form.order_number)   payload.order_number   = form.order_number
    if (form.client)         payload.client         = form.client
    if (form.deadline)       payload.deadline       = form.deadline
    payload.approved_material_id = form.approved_material_id || null
    if (form.material)       payload.material       = form.material
    if (form.order_type)     payload.order_type     = form.order_type
    if (form.quantity)       payload.quantity       = parseInt(form.quantity, 10) || 1
    if (form.description)    payload.description    = form.description
    if (form.notes)          payload.notes          = form.notes
    if (form.estimated_value !== '') payload.estimated_value = parseFloat(form.estimated_value) || 0
    payload.has_drawing   = !!form.has_drawing
    payload.requires_visit = !!form.requires_visit
    await api(`/orders/${id}`, { method: 'PATCH', body: payload })
    await loadOrders()
  }

  const nonstandardOrders = computed(() =>
    orders.value.filter(o =>
      o.triage_branch === 'niestandard' &&
      !['rejected', 'cancelled', 'done', 'wydane'].includes(o.status)
    )
  )

  const uniqueClients = computed(() => {
    const seen = new Set()
    return orders.value
      .filter(o => o.client)
      .map(o => o.client)
      .filter(c => !seen.has(c) && seen.add(c))
      .sort((a, b) => a.localeCompare(b, 'pl'))
  })

  return {
    orders, serviceHistory, loadOrders,
    deleteOrder, confirmOrder, startOrder, completeOrder, deliverOrder, saveEdit,
    nonstandardOrders, uniqueClients,
  }
}
