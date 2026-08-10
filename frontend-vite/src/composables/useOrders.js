import { ref, computed } from 'vue'
import { api } from './useApi'
import { useAuth } from './useAuth'

const orders       = ref([])
const archivedOrders = ref([])
const serviceHistory = ref([])
// Order to auto-select when the Zlecenia tab opens next (set after creating an order)
const focusOrderId = ref(null)

export function useOrders() {
  const { currentUser } = useAuth()

  async function loadOrders() {
    const [all, archived, history] = await Promise.all([
      api('/orders'),
      api('/orders?archived=true'),
      currentUser.value?.role === 'biuro'
        ? api('/service-history')
        : Promise.resolve(serviceHistory.value),
    ])
    orders.value = all
    archivedOrders.value = archived
    if (currentUser.value?.role === 'biuro') serviceHistory.value = history
  }

  async function deleteOrder(id) {
    await api(`/orders/${id}`, { method: 'DELETE' })
    await loadOrders()
  }

  async function archiveOrder(id) {
    await api(`/orders/${id}/archive`, { method: 'POST' })
    await loadOrders()
  }

  async function restoreOrder(id) {
    await api(`/orders/${id}/restore`, { method: 'POST' })
    await loadOrders()
  }

  async function confirmOrder(id) {
    await api(`/orders/${id}/confirm`, { method: 'POST' })
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

  const nonstandardOrders = computed(() =>
    orders.value.filter(o =>
      o.triage_branch === 'niestandard' &&
      !['rejected', 'wydane'].includes(o.status)
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
    orders, archivedOrders, serviceHistory, focusOrderId, loadOrders,
    deleteOrder, archiveOrder, restoreOrder, confirmOrder, completeOrder, deliverOrder,
    nonstandardOrders, uniqueClients,
  }
}
