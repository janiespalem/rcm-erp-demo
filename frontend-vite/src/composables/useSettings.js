import { ref } from 'vue'
import { api, readCache, writeCache } from './useApi'

const settings = ref({ labor_rate_pln: 90, ...(readCache('settings') || {}) })

export function useSettings() {
  async function loadSettings() {
    const list = await api('/settings')
    const map = {}
    for (const s of list) map[s.key] = s.value
    settings.value = { ...settings.value, ...map }
    writeCache('settings', map)
  }

  return { settings, loadSettings }
}
