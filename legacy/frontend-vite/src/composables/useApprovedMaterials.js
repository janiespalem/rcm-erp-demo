import { ref } from 'vue'
import { api, readCache, writeCache } from './useApi'
import { useToast } from './useToast'
import { useConfirm } from './useConfirm'

// Hydratacja z cache — lista widoczna natychmiast, świeże dane dogrywają się w tle.
const approvedMaterials = ref(readCache('materials') || [])

export function useApprovedMaterials() {
  const { show } = useToast()
  const { confirm } = useConfirm()

  async function loadApprovedMaterials() {
    const fresh = await api('/approved-materials')
    approvedMaterials.value = fresh
    writeCache('materials', fresh)
  }

  async function saveMaterial(mat, draft) {
    if (mat?.id) {
      await api(`/approved-materials/${mat.id}`, { method: 'PATCH', body: mat })
    } else {
      if (!draft?.name) { show('Podaj nazwę materiału'); return }
      await api('/approved-materials', { method: 'POST', body: draft })
    }
    await loadApprovedMaterials()
  }

  async function deleteMaterial(mat) {
    if (!await confirm(`Usunąć materiał "${mat.name}"?`)) return
    await api(`/approved-materials/${mat.id}`, { method: 'DELETE' })
    await loadApprovedMaterials()
  }

  function materialOptionLabel(m) {
    const parts = [m.name]
    if (m.category) parts.push(m.category)
    if (m.default_rate_pln_kg) parts.push(`${m.default_rate_pln_kg} PLN/kg`)
    return parts.join(' · ')
  }

  function rateForMaterial(materialName) {
    if (!materialName) return 0
    const lower = String(materialName).toLowerCase()
    const mat = approvedMaterials.value.find(m => {
      const n = String(m.name || '').toLowerCase()
      return lower === n || lower.includes(n) || n.includes(lower)
    })
    return mat ? +(mat.default_rate_pln_kg || 0) : 0
  }

  return { approvedMaterials, loadApprovedMaterials, saveMaterial, deleteMaterial, materialOptionLabel, rateForMaterial }
}
