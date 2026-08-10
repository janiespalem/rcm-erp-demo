import { ref } from 'vue'
import { api, readCache, writeCache } from './useApi'

const templates = ref(readCache('templates') || [])

export function useTemplates() {
  async function loadTemplates() {
    const fresh = await api('/templates')
    templates.value = fresh
    writeCache('templates', fresh)
  }

  async function saveAsTemplate(orderId, payload) {
    return api(`/orders/${orderId}/save-as-template`, { method: 'POST', body: payload })
  }

  return { templates, loadTemplates, saveAsTemplate }
}
