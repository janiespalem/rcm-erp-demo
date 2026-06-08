import { ref } from 'vue'
import { api } from './useApi'

const settings = ref({ labor_rate_pln: 90 })

export function useSettings() {
  async function loadSettings() {
    const list = await api('/settings')
    const map = {}
    for (const s of list) map[s.key] = s.value
    settings.value = { ...settings.value, ...map }
  }

  return { settings, loadSettings }
}
