import { ref } from 'vue'
import { api } from './useApi'

const templates = ref([])

export function useTemplates() {
  async function loadTemplates() {
    templates.value = await api('/templates')
  }

  async function saveAsTemplate(orderId, payload) {
    return api(`/orders/${orderId}/save-as-template`, { method: 'POST', body: payload })
  }

  return { templates, loadTemplates, saveAsTemplate }
}
